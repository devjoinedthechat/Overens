<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/logo-dark.svg">
    <img src="assets/logo-light.svg" alt="" width="112" height="112">
  </picture>
</p>

<h1 align="center">Overens</h1>

<p align="center">
  <b>An AI agent that checks payslips against Danish collective agreements.</b><br>
  The model reads and words things. Code decides and calculates. A person approves every rule.
</p>

<p align="center">
  <a href="https://github.com/devjoinedthechat/Overens/actions/workflows/ci.yml"><img src="https://github.com/devjoinedthechat/Overens/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="./LICENSE"><img src="https://img.shields.io/badge/license-MIT-15803d" alt="MIT licence"></a>
  <img src="https://img.shields.io/badge/.NET-10-15803d" alt=".NET 10">
  <img src="https://img.shields.io/badge/C%23-warnings%20as%20errors-15803d" alt="C#, warnings as errors">
</p>

<p align="center">
  <a href="#walkthrough">Walkthrough</a> ·
  <a href="#what-it-proves">What it proves</a> ·
  <a href="#results-on-real-models">Results</a> ·
  <a href="#quick-start">Quick start</a> ·
  <a href="#how-an-agreement-becomes-rules">How it works</a> ·
  <a href="#design-decisions">Design decisions</a>
</p>

A collective agreement (*overenskomst*) says what people must be paid: minimum wages by seniority, overtime, evening and night supplements, pension, holiday allowance. Overens reads an agreement's PDF into typed rules, has a payroll specialist approve them, and then checks payslips against them and answers questions about the agreement with the clause cited.

*Overens* is Danish for "in agreement". The repo is organised around one question: what happens when the model is wrong? A model reads the agreement and words the findings. It never calculates, never decides whether a payslip is right, and never publishes a rule. Every property claimed below has a test that tries to break it.

```mermaid
flowchart LR
  specialist([Payroll specialist]) --> api
  employer([Employer]) --> api
  api[API and console] -- stores, then starts --> run

  subgraph scheduler [Durable Task]
    run[Ingestion run<br/>one per upload]
  end

  subgraph worker [Worker]
    read[Read PDF<br/>into clauses] --> extract[Extract rules] --> checks[Check each rule<br/>no model]
  end

  run --> read
  extract --> gateway[Model gateway<br/>CPR removed, budget, failover] --> model([Claude])
  checks --> review{{A person<br/>approves}}
  review --> publish[(Rule sets<br/>never changed)]

  api -- payslip --> engine[Rule engine<br/>decimal, no model]
  publish --> engine
  engine --> findings[Findings<br/>with clause]
  api -- question --> agent[Question agent<br/>own loop] --> answercheck[Answer check<br/>no model]
  agent -- calculator tool --> engine
  agent --> gateway

  idp[Identity provider] -. tokens .-> api
  api --> db[(Postgres<br/>row-level security)]
  worker --> db
  db --> audit[(Audit log<br/>written by triggers)]
```

## Walkthrough

![A specialist uploads an agreement, reviews each proposed rule beside its clause and publishes; an employer checks a payslip and asks a question](docs/walkthrough/overens-walkthrough.svg)

The system running on one machine with Claude as the model. Every picture is the real console. On the left of each rule are its values; on the right, in print, the clauses it was read from, with the quoted sentence marked. The explanation and the answer in the last two pictures are the model's, after passing the checks.

## Stack

| Layer | Choice | What it does here |
| --- | --- | --- |
| Language | C# on .NET 10, warnings as errors | One language across services, engine, evals and tests |
| Model | Claude Sonnet and Haiku, Anthropic C# SDK | Reads agreements, words findings, answers questions |
| Model seam | `Microsoft.Extensions.AI` | Redaction, failover, budget and replay are layers on one interface |
| Agent loop | Own, about a hundred lines | Five read-only tools; the answer is checked before it is shown |
| Rules | Typed records and an engine on `decimal` | Every sum, with no model |
| Durable work | Durable Task Scheduler | One orchestration per upload; waits days for a reviewer; survives a dead worker |
| Data | PostgreSQL, plain-SQL schema, EF Core for access | Row-level security per company, three roles, immutable tables |
| Identity | OpenID Connect; Keycloak locally | Roles and company from the token |
| PDF | PdfPig to read, MigraDoc to generate | Clauses from layout; tables from their ruled borders |
| Telemetry | OpenTelemetry | Spans whose attributes come from a fixed list |
| Local | .NET Aspire | The whole system from one command |
| Tests | xUnit, Testcontainers, CsCheck, Playwright | Real Postgres and scheduler; property tests; a real browser |

## What it proves

Each row is a property and the test that attacks it. Where a control was added, a bug was planted to confirm the test fails without it.

### The model does not decide

| Property | How the test tries to break it |
| --- | --- |
| The model never calculates | A stand-in model adds up an increase itself; the sums are printed nowhere and are refused. Reported as an increase, code adds it |
| Money is never a float | Reflection over the rules assembly for any `float` or `double` |
| Every rule quotes its clause | A reworded quote, a clause that does not exist |
| Every number is the agreement's | The pension total read as the employer's share; a rate off by one krone; a start date printed nowhere |
| A missing rule is reported | A stand-in returns only the first year; nothing is flagged on any rule, and the gap is reported |
| A contradiction is put to a person | Two clauses give different figures; both are proposed, the conflict names both clauses, and approving both is refused |
| An explanation uses only the finding's numbers | A percentage the model worked out; a deadline it invented; "too much" said of a shortfall |
| An answer rests on the agreement | A sum the model made; a reworded quote; a number from a clause it did not cite |
| What the agreement does not say is not answered | Twelve unanswerable questions per model; the fixed sentence is the only correct outcome |

### A person publishes

| Property | How the test tries to break it |
| --- | --- |
| Nothing is published without a person's approval | The worker calls publish with no approval; tries to write an approval, a decision and a rule set directly |
| The API cannot publish | The API's database role calls the publish function |
| A flagged rule cannot be approved | Through the API, and directly in the database |
| What was approved is what is published | A decision changed after the approval and before publication |
| A published rule set cannot change | Update, delete and truncate, as each role and as the database's owner |
| A payslip is checked against the rules of its own date | A renewal from 2029; payslips on both sides of it |

### Isolation and privacy

| Property | How the test tries to break it |
| --- | --- |
| One company cannot see another's data | Another company's token on every route; another tenant's scope on every table |
| A token is checked, not trusted | Wrong key, expired, wrong audience, no role, no company |
| An employer cannot do a specialist's work | Upload and rule sets with an employer's token |
| A CPR number never reaches a model | In a question and in a tool result; removed at the layer next to the provider |
| Pay data is not kept | Thirty payslips checked; the audit log holds counts and no employee id |
| Telemetry carries no content | Every attribute of every span of a run searched for the agreement's words |
| A company can be erased, by the owner only | Each service role tries; the owner tries another company's rows |

### Hostile input

| Property | How the test tries to break it |
| --- | --- |
| Text in a document cannot give orders | Five passages addressed to AI systems, one inside the wage clause, one dressed as a protocol; both real models |
| A damaged file does not reach the model | A PDF cut short, scrambled, or only a header |
| Markup is shown as text | Typed into the console; any script or console error fails the browser check |
| The console loads only its own code | The content policy is read back and may not contain `unsafe` |

### Operations

| Property | How the test tries to break it |
| --- | --- |
| A run survives its worker dying | The worker is stopped while a run waits; the review is done with nothing running; a second worker finishes without reading again |
| An upload is never lost | The scheduler is unreachable at upload; the worker finds the run and starts it once |
| A model that is down is replaced | The asked-for model fails; the other answers, and the run records which |
| One company cannot spend without limit | A daily budget of five cents; the second agreement is refused at once |
| Every change is logged, and the log cannot be edited | Each role tries to write, change and empty it; the owner's edit breaks the hash chain |
| An agent cannot run forever | A stand-in that only ever searches |

## Results on real models

Every document is synthetic: an agreement is generated from a rule set, so the right answer is known before any model reads it. No real agreement has been measured.

| What | Claude Sonnet | Claude Haiku |
| --- | --- | --- |
| Rules read out of ten agreements | 121 of 121 | 121 of 121 |
| Wrong rules the model-free checks missed | 0 | 0 |
| Payslips checked correctly with those rules | 720 of 720 | 720 of 720 |
| Questions answered correctly, of 48 | 48 | 48 |
| Wrong answers shown | 0 | 0 |
| Rules changed by five planted instructions | 0 | 0 |
| Cost to read one agreement | $0.08 | $0.005 |

Both models are at the ceiling, so these documents cannot tell them apart. The numbers are after fixes: on first contact with documents the prompt had not seen, a wage given as "raised by 5,00 kr." broke extraction, Sonnet silently chose between two contradicting clauses, and Haiku searched thirteen times for something the agreement does not cover.

The [eval report](docs/eval-report/README.md) has each failure, its fix, and what the numbers do not show. A test fails if the published results differ from what the recorded answers give.

## Quick start

It needs the .NET 10 SDK and Docker. The browser part of the smoke test also needs Google Chrome.

```sh
git clone https://github.com/devjoinedthechat/Overens && cd Overens
scripts/check.sh      # secrets scan, build, format check, every test
scripts/smoke.sh      # the whole system from nothing, one agreement from upload to checked payslips
```

No model key is needed for either. Tests use stand-in models, and the system replays the answers recorded for the corpus agreements.

### Run the whole system locally

```sh
dotnet run --project src/AppHost
```

| Service | Address | What it is |
| --- | --- | --- |
| Console | `http://localhost:5058/app/` | Upload and review as a specialist; check pay and ask as an employer |
| API | `http://localhost:5058` | The same, for programs |
| Keycloak | `http://localhost:8081` | Sign-in. Realm `overens`, admin `admin` / `admin` |
| Scheduler | `http://localhost:8092` | Each run, step by step |
| Dashboard | `http://localhost:15034` | Logs and traces of every service |

Local accounts, all with the password `local-demo-password`:

| User | Company | Role |
| --- | --- | --- |
| `anna` | `tvaerhavn-loen` | Specialist: uploads, reviews, approves |
| `bo` | `elmekaer-regnskab` | Specialist at another company |
| `eva` | `tvaerhavn-loen` | Employer: checks pay and asks questions |
| `erik` | `tvaerhavn-loen` | None: signed in, and let in nowhere |

Upload `data/synthetic/hard-2026/agreement.pdf` as `anna`. Check a case from `data/synthetic/hard-2026/pay.json` as `eva`; `pay-answers.json` says which are wrong and how.

With `ANTHROPIC_API_KEY` set, Claude is called under a spending cap: findings are worded by a model and questions are answered. Without it, findings get fixed wording and questions are refused.

### Evals

```sh
dotnet run --project evals/Runner                    # extraction on ten agreements
dotnet run --project evals/Runner -- --questions     # 48 questions with known answers
dotnet run --project evals/Runner -- --attacks       # planted instructions and a contradiction
dotnet run --project evals/Runner -- --explain       # how often a model's wording passes the guard
```

Each replays recorded answers and spends nothing. Add `--model claude-haiku-5-5` for the other model, and `--record --cap 2` with a key to call the model for anything not yet recorded.

## How an agreement becomes rules

1. **Stored.** The PDF is kept under the hash of its bytes and never changed, so the words a rule quotes can always be produced.
2. **Read, with no model.** Text from the letters and their positions; tables from their ruled borders. Front page, table of contents, running titles and page numbers are dropped. Clauses split by a page break are joined. Every clause gets an id such as `§ 5, stk. 2`.
3. **Extracted.** One request per kind of rule, each with a strict schema. The agreement is passed as data. The model returns typed rules, each naming its clause and quoting a sentence from it.
4. **Checked, with no model.** The clause exists; the quote stands in it word for word; every number and date is printed where the rule says; values are in range. Missing kinds, uncovered days and conflicting rules are reported for the run.
5. **Reviewed.** A specialist sees each rule beside its clause with the quote marked, and what publishing would change against the rules already published. They approve or reject each rule, then the run.
6. **Published.** The database publishes the approved rules as the agreement's next version, and only if an approval is on record. A version is never edited; a later one takes over from the day it starts.

A payslip is then checked by arithmetic: the engine works out what the period should have paid, minute by minute, and reports every line that differs with the rule and clause behind it.

## Test material

| What | Where |
| --- | --- |
| Twelve agreements as PDF, text and structure, with the rules each states | `data/synthetic` |
| Six months of timesheets and payslips for each, with nine kinds of seeded payroll mistake and the answers | `data/synthetic/*/pay.json` |
| The generators | `src/Synthetic` |
| Recorded model answers | `evals/recordings` |

The corpus is generated, never edited by hand: `dotnet run --project tools/Corpus` writes it, and a test fails if the committed files differ. The organisations and agreement names are invented, and each PDF says so on its front page. The PDFs use Liberation Serif, under the SIL Open Font License.

## Repository layout

| Path | What is there |
| --- | --- |
| `src/Rules` | Rule records, the pay engine, the validator, versions and comparison. No dependencies, no model |
| `src/Documents` | The structure of an agreement: sections, clauses, text and tables |
| `src/Ingestion` | PDF to clauses, the extraction prompt and schemas, the checks, the pipeline's steps |
| `src/Agent` | The finding explainer and its guard; the question agent, its tools and the answer check |
| `src/Gateway` | Model access: redaction, failover, budget, usage, replay, telemetry |
| `src/Data` | The schema in SQL, the migrator, tenant-scoped access |
| `src/Worker` | The durable orchestration and the starter for runs that were never dispatched |
| `src/Api` | The API and the console |
| `src/AppHost` | The local system: Postgres, scheduler, Keycloak, worker, API |
| `src/Synthetic` | Generators for agreements, PDFs and pay data |
| `evals` | The eval runner and the recorded answers |
| `tests` | One project per source project |
| `tools` | Corpus writer, migrator and admin commands, the browser check and walkthrough recorder |
| `docs` | Decision records, eval report, threat model, cut list, a note on the EU AI Act |

## Design decisions

1. [The model never calculates](docs/adr/0001-the-model-never-calculates.md)
2. [The rules come first and the documents are written from them](docs/adr/0002-rules-before-documents.md)
3. [A person publishes, and the database insists](docs/adr/0003-a-person-publishes.md)
4. [Every proposed rule is checked against the agreement with no model](docs/adr/0004-grounding-without-a-model.md)
5. [Durable Task for the pipeline](docs/adr/0005-durable-task.md)
6. [Own agent loop instead of a framework](docs/adr/0006-own-agent-loop.md)
7. [An answer is shown only if it can be checked](docs/adr/0007-answers-must-be-checkable.md)
8. [Rule sets are never changed, and the newest version takes over by date](docs/adr/0008-immutable-versions.md)
9. [Pay data is checked and not kept](docs/adr/0009-pay-data-is-not-kept.md)
10. [The audit log is written by the database, and telemetry carries no content](docs/adr/0010-logged-by-the-database.md)
11. [One model provider, and no real agreements](docs/adr/0011-one-provider.md)

See also the [threat model](docs/threat-model.md), [what was left out, and why](docs/cut-list.md), and [the EU AI Act and this system](docs/ai-act.md).

## Scope

- **Synthetic documents only.** A published agreement is its parties' copyrighted text. None is downloaded, read or sent to a model, so nothing here is evidence about real agreements.
- **Six kinds of rule.** Minimum wage, overtime, inconvenient hours, pension, holiday allowance and special savings. A real agreement says more about pay than that.
- **One model provider.** Failover is between Claude models, which covers a model short of capacity and not the provider being down.
- **One machine.** The system runs locally, with development settings: plain HTTP, fixed demo passwords and a default Keycloak admin.
- **A reviewer approves or rejects.** Editing a proposed rule is not built.
- **A small console.** One page, in English, written without a framework or a build step. Agreements, findings and answers are in Danish, and the agreement's own words are set apart from the interface as print.

## Licence

[MIT](LICENSE)

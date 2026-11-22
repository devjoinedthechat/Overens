# Changelog

What changed, by the day it was pushed. There is no release yet; the first will be `0.1.0`. The figures behind each entry are in the [eval report](docs/eval-report/README.md), and the reasons for each design choice in [the decision records](docs/adr).

## Unreleased

### 2026-11-22

- **A second reading.** Every agreement is read by a second model too, and a rule the two readings differ on is put to the reviewer. With Sonnet first and Haiku second, wrong rules that pass on the unseen agreements go from 7 to 3. [Decision 12](docs/adr/0012-a-second-reading.md).
- **The answer keys checked.** A reader shown only the eight hand-written agreements wrote down the same 99 rules as their keys. Its rules and notes stand beside each key.
- **Eight hand-written agreements.** Four the prompt was then fixed against, and four read once to measure the fix: Sonnet finds 40 of 51 rules, Haiku 30, and wrong rules pass.
- **Prompt `2026-11-22.1`.** A step added per seniority band, a share of a total, and a change in percentage points are reported as printed and summed by code.
- **One parser for every figure** in an agreement's text, in place of string patterns per check.
- **A reviewer study, built and not yet taken:** 36 rule cards beside their clauses, 12 wrong in ways that pass every check, with a scorer.
- **Uncertainty** on small samples, as 95 pct. Wilson intervals.
- Recorded through Claude Code's print mode (`--via claude-code`); earlier prompts' results kept under their own version.

### 2026-11-21

- **The checks measured on wrong answers.** 2,366 mistakes of 48 kinds planted in correct answers, one at a time. The first run found three faults in the checks.
- **Checks that read tables:** a figure must stand under the rule's date and in its row.
- **Mutation testing** of the rule engine, 96.4 pct., and a floor of 94 pct. under line coverage, both in CI.
- **The same extraction asked again,** three times for Sonnet and five for Haiku.

### 2026-11-16

- Six long agreements of about 24 pages; a share stated as a part of another figure; runs whose pipeline is gone are ended.
- The Azure deployment defined and checked without an account.
- A line with no approved rule is reported as not checked, not as wrong.

### 2026-11-15

- The README, eleven decision records, the threat model, the cut list, a note on the EU AI Act, and the walkthrough.
- The console's own look, and a logo.
- Fixes from a code review; locked package versions, pinned CI actions, CodeQL and Dependabot.

### 2026-11-14

- The rule model and the pay engine on `decimal`; invented agreements generated from their rules as PDFs, with pay data and known answers.
- Reading a PDF into clauses with no model; extraction by a model, and checks with no model on every rule.
- The database with three roles, row-level security, and publishing that needs a person's approval.
- A durable pipeline that waits for a reviewer and survives its worker dying.
- The API and the console; payslip checks; a question agent whose answers are checked; hostile agreements; failover, budgets and an audit log the database writes.

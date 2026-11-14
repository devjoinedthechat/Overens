# Eval report

How well Overens reads pay rules out of a collective agreement, and what happens to payslips checked with the rules it read.

Prompt version `2026-11-15.1`. Recorded on 15 November 2026. Every figure here is reproduced, at no cost and with no model key, by:

```sh
dotnet run --project evals/Runner                             # Claude Sonnet
dotnet run --project evals/Runner -- --model claude-haiku-5-5
```

A test fails if the files in [`results/`](results) differ from what the recorded answers give.

## What is measured

Each agreement is a PDF generated from a rule set, so the right answer is known before any model reads it. The eval starts from the PDF: layout reading, clause splitting, extraction and the model-free checks all run.

| Measure | Meaning |
| --- | --- |
| Rules found | True rules that a proposed rule states exactly, in period and every value |
| Flagged | Proposed rules the model-free checks raised a problem on |
| Wrong and unflagged | Wrong rules the checks let through. Only a reviewer stands between these and a published rule set |
| Right but flagged | Correct rules the checks raised a problem on. Needless work for a reviewer |
| Payslips right | Payslips whose findings, using the unflagged rules, are exactly the known answers |

Rules are compared by what they say, not how they are written: ids and quotes are ignored, `6.0` equals `6`, and time windows are compared by what they pay at each half hour of the week.

## Documents

| Set | Agreements | Rules | Payslips | What it is |
| --- | --- | --- | --- | --- |
| Corpus | 4 | 44 | 288 | Three levels of difficulty and a renewal. The prompt was written while looking at these |
| Challenge | 6 | 77 | 432 | The same kinds of agreement written in six ways the prompt was not written against. Added after the prompt was fixed |

The six ways, each taken from how real agreements are written:

| Twist | What changes |
| --- | --- |
| Increases | Only the first year's wage is printed. Later years are "raised by 5,00 kr.", so the rate is a sum |
| Numeric dates | Table headings say `01.03.2027`, not `1. marts 2027` |
| Split night | The night supplement is two rows, 22-24 and 00-06 |
| Old rates | A table of last period's rates stands beside the current ones, and the top seniority band starts at two years |
| Mid-year change | A second protocol raises a rate from 1 September, between two yearly steps |
| All | Every twist at once |

## Results

Current prompt, `2026-11-15.1`:

| Model | Set | Rules found | Wrong and unflagged | Payslips right |
| --- | --- | --- | --- | --- |
| Sonnet | Corpus | 44 of 44 | 0 | 288 of 288 |
| Sonnet | Challenge | 77 of 77 | 0 | 432 of 432 |
| Haiku | Corpus | 44 of 44 | 0 | 288 of 288 |
| Haiku | Challenge | 77 of 77 | 0 | 432 of 432 |

Cost and size, for all ten agreements:

| Model | Cost | Per agreement | Calls | Tokens in | Tokens out |
| --- | --- | --- | --- | --- | --- |
| Sonnet | $0.79 | $0.08 | 60 | 286,418 | 22,052 |
| Haiku | $0.05 | $0.005 | 60 | 286,418 | 47,105 |

The agreement is sent once per kind of rule, six times in all, which is most of the input. Prompt caching is not yet used.

## What failed first

The challenge set was first run on the earlier prompt, `2026-11-14.1`, unchanged. Five of the six twists made no difference to either model. One did.

| Model | Agreement | Rules found | Flagged | Wrong and unflagged | Payslips right |
| --- | --- | --- | --- | --- | --- |
| Sonnet | Increases | 12 of 12 | 2 | 0 | 71 of 72 |
| Sonnet | All | 14 of 14 | 2 | 0 | 70 of 72 |
| Haiku | Increases | 10 of 12 | 0 | 0 | 71 of 72 |
| Haiku | All | 12 of 14 | 0 | 0 | 70 of 72 |

**What happened.** The agreement printed the 2026 wage and then said the rates "are raised by 5,00 kr. per hour from 1 March 2027". The prompt told the model to copy numbers and never compute.

- **Sonnet added the numbers up anyway**, correctly. The sums are printed nowhere in the agreement, so the check "every number is printed in the cited clauses" flagged both later years. The rules were right and a reviewer would have had to reject or retype them.
- **Haiku obeyed the instruction** and returned only the first year. The later years had no minimum wage at all, with nothing flagged, because a missing rule is not a wrong rule.
- **Either way the published rule set had no minimum wage after March 2027**, so payslips that paid below the minimum from then on were not caught: three of 432.

**What this showed.** The checks did their job: nothing wrong reached a reviewer looking sound. But the design had a hole. A model was being asked either to do arithmetic or to drop a rule.

**The fix.** The model now reports an increase as the agreement prints it: an empty rate list and `increase: 5.00`, quoting the sentence. Code adds it to the previous period's rates. The check verifies that the increase is printed, not the sum. The model still never calculates. With that change both models find all 77 rules.

This fix was made after seeing the failure, so the "Increases" result above is no longer a result on unseen documents. The other five twists were never fitted to.

## What the checks caught, in tests

The models made no error the checks had to catch on these documents, so the checks are shown by tests that spoil a correct answer one way at a time (`tests/Ingestion.Tests/ExtractionTests.cs`):

| Spoiled answer | Caught as |
| --- | --- |
| A reworded quote | `quote_not_in_clause` |
| The pension total read as the employer's share | `value_not_in_cited_clauses` |
| A rate off by one krone | `value_not_in_cited_clauses` |
| A daily norm from a clause the rule does not cite | `value_not_in_cited_clauses` |
| A clause that does not exist | `unknown_clause` |
| A start date printed nowhere | `date_not_in_agreement` |
| The same rule twice | `overlapping_period` |
| An increase the agreement does not print | `value_not_in_cited_clauses` |

## Explanations of findings

A finding is decided by arithmetic. A model may then word it in plain Danish, and its wording is shown only if every number in it is the finding's own and it does not say the opposite of what was found. Otherwise fixed wording is shown.

On the 60 findings in the hardest corpus agreement's pay data (`dotnet run --project evals/Runner -- --explain --model claude-haiku-5-5`):

| Model | Wording shown | Replaced by fixed wording | Why replaced | Cost |
| --- | --- | --- | --- | --- |
| Haiku | 59 of 60 | 1 | Too long: cut off at the length limit | $0.01 |
| Sonnet | 52 of 60 | 8 | Too long: cut off at the length limit | $0.18 |

Neither model put a number of its own into an explanation. Sonnet writes longer and runs into the limit more often, so Haiku is the default for this job.

The first version of the guard refused two correct wordings: the model had been shown an hourly rate rounded to the øre, and the guard only accepted the unrounded one. The guard now accepts a figure as it was shown.

Whether the wording is good Danish, or easier to act on than the fixed wording, is not measured.

## Questions

The question agent is asked 48 questions in Danish about four agreements, with the true rules and the agreement's clauses to hand. Every question is written from the rule set, so its answer is known (`dotnet run --project evals/Runner -- --questions`). Question prompt `2026-11-15.3`.

| Kind | Example | Questions |
| --- | --- | --- |
| Lookup | What percentage is the holiday allowance? | 8 |
| Dated | What is the minimum wage on 15 April 2027 for a new hire? | 16 |
| Calculation | What is the overtime supplement for a shift from 14.00 to 23.30 at 200 kr. an hour? | 8 |
| Text | How long is the employer's notice in the first year? (not a pay rule; only in the text) | 4 |
| Unanswerable | How many weeks of paid parental leave? (the agreement does not say) | 12 |

An answer is correct when it is shown, states the known figure and cites a clause the rule comes from. For an unanswerable question, correct is the fixed sentence "the agreement does not answer the question".

| Model | Correct | Wrong answer shown | Answered the unanswerable | Withheld | Cost |
| --- | --- | --- | --- | --- | --- |
| Haiku | 48 of 48 | 0 | 0 | 0 | $0.06 |
| Sonnet | 48 of 48 | 0 | 0 | 0 | $0.97 |

**What went wrong on the way, and was fixed before these numbers:**

- **A model that kept searching.** On the first run Haiku searched thirteen times for parental leave and ran out of turns. The agent now gets the agreement's table of contents with the question, so it can see what the agreement covers.
- **A model that cannot be forced to call a tool.** Sonnet rejects a forced tool choice. Nothing is forced now; text outside a tool call is never shown, and the model is reminded once.
- **A judge that was too strict.** "118 kr." was marked wrong against "118,00". Numbers are now compared as numbers.
- **A "not answered" that was refused.** On a parental-leave question Sonnet called `give_answer` with only `answered: false`, twice, and was stopped for leaving fields out. The person would have got "ask a payroll specialist": unhelpful, not wrong. A call that says only "not answered" is now taken as it is. The same recorded answers then give 48 of 48; before the change they gave 47 and one withheld.
- **A check that was too strict.** An answer that said which date it had looked the rules up for was refused, because the date was not in the question. A date or figure the agent put into a tool is now allowed in the answer.

**What the answer check did.** In every calculation answer the amounts came from the calculator, and the check confirmed it. No model put a figure of its own into an answer that was shown.

**Limits.** The questions are written from templates, twelve per agreement, and each was asked once. The agent is given the true rules, not extracted ones. Nobody judged whether the Danish reads well. The search is a keyword ranking over a few dozen clauses; it has not been measured on its own.

## What these results do not show

- **The documents are synthetic and both models are at the ceiling.** The sets cannot tell Sonnet from Haiku, and say little about where extraction breaks on a real agreement.
- **Each model answered once.** There is no measure of how much an answer varies from run to run.
- **Six kinds of rule.** Anything else an agreement says about pay, such as shift work, on-call pay or public holidays, is not extracted and not counted as missing.
- **Text PDFs with ruled tables in one column.** A scanned agreement is not read at all.
- **What the checks cannot catch** is a value that is printed in the cited clause but belongs to something else: the apprentice rate taken as the minimum wage, a supplement attached to the wrong weekday, a right value for the wrong period. No model did this here. A reviewer is the only guard against it.
- **A missing rule is reported, not prevented.** Since Haiku's first run, an extraction says which kinds of rule have no rule, or days with none, and the reviewer sees that list. Both models report no gaps on the current prompt. Whether a reviewer acts on the list is not measured.

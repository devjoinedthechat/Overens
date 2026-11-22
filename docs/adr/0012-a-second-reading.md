# 12. Every agreement is read twice, by two models

Status: accepted, 2026-11-22. Revisits one option that [decision 4](0004-grounding-without-a-model.md) turned down.

## Context

The checks hold every figure of a rule to the agreement's text. On four agreements nobody here had seen before reading them, that was not enough: a model put printed figures together wrongly, and the rule passed.

- A seniority supplement was reported as added to the band before, where the agreement adds it to the base.
- A Sunday supplement was given as the "5" of "raised by 5 percentage points".
- A rise for the top band was applied to every band.

No check on the text can see these. Every figure in them is printed where the rule says.

## Options

| Option | For | Against |
| --- | --- | --- |
| A field for each way of stating a figure | Code does every sum | Each field is one more way to hand the code the wrong sum; the list did not end |
| Trust the reviewer | Nothing to build | Unmeasured; the rule looks sound beside its clause |
| A second model reads the agreement too | Two readings seldom make the same mistake | Costs more; both can read it wrong the same way |

## Decision

The worker reads every agreement with its model and then with the other one. A rule the second reading does not state the same way, on any day the rule applies, is flagged `second_reading_differs` and cannot be approved as it stands. Rules only the second reading has are not added.

- **No failover for the second reader.** If it could fall back to the first model, the second reading would be the first one again. When it fails, the run goes on and the reviewer is told: `second_reading_missing`.
- **Same meaning, not same wording.** Readings are compared by what they pay on each day, so 22–06 and 22–24 with 00–06 agree, and so do 6,0 and 6.
- **Configurable.** `SECOND_MODEL` names the second model, and `none` reads once.

## Why

Measured on the recorded answers ([eval report](../eval-report/README.md#a-second-reading)):

| Sonnet reads, Haiku second | Without | With |
| --- | --- | --- |
| Wrong rules that pass, unseen agreements | 7 | 3 |
| Correct rules flagged, unseen agreements | 13 | 14 |
| Correct rules flagged, 200 rules of the generated agreements, 15 pairings of repeat passes | 0 | 0 |

Haiku costs about a sixteenth as much as Sonnet, so the second reading adds about 6 pct. to a run.

## Cost

- **It does not catch a mistake both models make.** Three passed: a holiday allowance taken as its statutory 1 pct., a Sunday supplement given as its change in points, and one band's rise given to every band. Decision 4's objection holds for that kind of mistake.
- **The readers share a prompt.** The last two are mistakes the prompt's fields invite, so both models made them. A second reader with a prompt of its own would share fewer of them; that is not built.
- **With Haiku first, it did nothing on these agreements.** Haiku's two wrong rules were mistakes Sonnet made too. The first reader should be the stronger model.
- **Twice the calls, and twice the recordings.** The replay the system uses without a key needs both models' answers.

## When this breaks

When both readers learn the same habit, and on any agreement whose figures are stated in a way neither model reads right.

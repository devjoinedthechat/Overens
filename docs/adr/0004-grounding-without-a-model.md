# 4. Every proposed rule is checked against the agreement with no model

Status: accepted, 2026-11-16

## Context

A model can misread a table, round a number, paraphrase a clause or invent one. A reviewer who has to verify every figure by hand gains little from the extraction.

## Options

| Option | For | Against |
| --- | --- | --- |
| Trust the model's output | Nothing to build | Invented and misread values reach the reviewer looking sound |
| Ask a second model to verify | Catches some errors | Two models can agree on a wrong reading; costs twice; not reproducible |
| Check mechanically: clause exists, quote word for word, numbers and dates printed where cited | Deterministic; cheap; says exactly what failed | Cannot tell a right rule from a wrong one built of real words and numbers |

## Decision

Each rule names its clause and quotes a sentence from it. Code checks that:

- the clause exists, and the quote stands in it word for word;
- every number in the rule is printed in the quote's section or another cited clause;
- a number that stands in a table stands under the rule's date and in the rule's row;
- every date is printed in the agreement, and the rule's values are in range.

A rule with a problem cannot be approved. Kinds of rule with none proposed, days no rule covers, and rules that cover the same days are reported for the run.

## Why

- **It fails the errors models make most:** paraphrase, rounding, a value from nowhere.
- **A reviewer's attention goes where it is needed.** A sound rule is shown beside its clause with the quote marked.

## Cost

- The check is on form. A value printed in the cited clause that belongs to something else can pass: two percentages in one sentence exchanged, an apprentice rate taken as the minimum wage, a figure planted in the clause. The reviewer is the only guard there.
- How much gets through is measured. Of 2,366 mistakes planted in correct answers, 1,904 are stopped, 200 are reported and 262 pass in silence. The [eval report](../eval-report/README.md#the-checks-measured-on-wrong-answers) lists which.
- It flagged correct rules twice when a model left a clause out of its citations. The check was widened to the quote's whole section.

## When this breaks

Scanned documents, and agreements that state a value in a form the check does not know, where correct rules would be flagged and reviewers would learn to ignore the flag.

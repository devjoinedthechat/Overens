# 1. The model never calculates

Status: accepted, 2026-11-16

## Context

Checking pay is arithmetic: minutes times rates, percentages of sums, rounding to the øre. A language model does arithmetic plausibly, not reliably, and a payslip check that is right most of the time is worse than none.

## Options

| Option | For | Against |
| --- | --- | --- |
| The model reads the agreement and computes the pay | One step, no rule model to design | Wrong sums that look right; nothing to test; a different answer on a second run |
| The model writes code that computes the pay | Flexible | Generated code per agreement has to be reviewed and tested like any other code, by someone |
| The model extracts typed rules; a fixed engine computes | The engine is ordinary code with ordinary tests; the same rules give the same pay every time | Only the kinds of rule the engine knows can be extracted |

## Decision

The model turns clauses into typed rule records. A C# engine using `decimal` does every sum, in the payslip check and in the question agent's calculator tool. Where an agreement states a rate as an increase on an earlier one, the model reports the increase and code adds it.

## Why

- **The decision is reproducible.** A payslip checked today and in a year gives the same findings.
- **It can be tested without a model.** Property tests on thousands of random timesheets, and a reflection test that nothing in the rules assembly uses `float` or `double`.
- **A model's wording cannot change a number.** Explanations and answers are checked against what the engine produced.

## Cost

- Six kinds of rule. An agreement says more about pay than that, and the rest is not extracted.
- The engine embodies conventions (which day's rates apply to a shift over midnight, how overlapping supplements combine). The synthetic agreements state them as clauses. A real one may not.

## When this breaks

An agreement whose pay rules do not fit the rule model: monthly salaries with an hourly rate derived by a divisor, half rates for young workers, public holidays. The model then has nothing honest to return, and the extraction reports a gap.

# 7. An answer is shown only if it can be checked

Status: accepted, 2026-11-16

## Context

A fluent answer about pay that is wrong is worse than no answer. The agent reads clauses and computes with a tool, and can still state a number from neither.

## Options

| Option | For | Against |
| --- | --- | --- |
| Show what the model says | Always an answer | Invented figures; answers from general knowledge of Danish law |
| Have a model grade the answer | Flexible | Not reproducible; a grader can be persuaded |
| Check mechanically and fall back to fixed wording | Deterministic; says why an answer was refused | Refuses some good answers; cannot judge whether the answer addresses the question |

## Decision

An answer must cite a clause that exists, quote it word for word, and use only numbers printed in a cited clause, present in the question, or put into or returned by the rule lookup and the calculator. A refused answer goes back to the model once with the reason. A second refusal gives a fixed sentence. "The agreement does not say" is given in fixed words, never the model's.

## Why

- **Every figure a person reads has a source they can open.**
- **Abstaining is a first-class outcome,** measured in the evals alongside correctness.

## Cost

- The check is on provenance, not sense. A right number attached to the wrong question would pass.
- It was too strict twice in testing: it refused an answer for naming the date it had looked up, and a judge of mine marked "118 kr." wrong against "118,00". Both are in the eval report.

## When this breaks

Questions whose honest answer needs reasoning the tools do not cover. The agent then withholds, which is safe and unhelpful.

# 2. The rules come first and the documents are written from them

Status: accepted, 2026-11-16

## Context

To measure extraction, the right answer must be known for every document. Hand-labelling long agreements is slow and error-prone, and published agreements are someone's copyrighted text.

## Options

| Option | For | Against |
| --- | --- | --- |
| Hand-label real agreements | Real difficulty | Slow; labels drift; the documents cannot be redistributed with the repo |
| Ask a model to write agreements, then label them | Fast | The writer and the reader share habits; the labels are still made after the fact |
| Choose the rules, then generate the agreement from them with fixed phrasings | The answer exists before any model reads the document; everything can be committed | Cleaner than a real document; says less about real difficulty |

## Decision

A generator chooses rates from a seed, builds the rule set, and writes a Danish agreement around it with templates: sections, clauses, tables, protocols. Pay data with seeded payroll mistakes is generated the same way. A test fails if the committed corpus differs from what the generator writes.

## Why

- **Ground truth is free and exact.** Scoring is a comparison, with no judge model.
- **Difficulty is controlled.** Each hard feature (an increase in prose, a split night window, a contradiction, a planted instruction) is a switch, and can be tested alone.
- **No circularity.** No model writes the documents.

## Cost

- Both models score at the ceiling on these documents, so the documents cannot rank them and say little about real agreements.
- The extraction prompt was written while looking at the first four agreements. A second set was added after the prompt was fixed, and one of its six twists broke extraction.

## When this breaks

As evidence of accuracy on real agreements. This repo does not claim it. No real agreement has been measured.

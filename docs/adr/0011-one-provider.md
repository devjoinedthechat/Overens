# 11. One model provider, and no real agreements

Status: accepted, 2026-11-16

## Context

The models are Anthropic's. Published collective agreements are copyrighted text belonging to their parties.

## Options

| Option | For | Against |
| --- | --- | --- |
| Add a second provider for failover | Covers a provider outage | A second set of prompts, evals and prices to keep honest |
| Fail over between one provider's models | One prompt, one eval; covers a model short of capacity | Does not cover the provider being down |
| Test on downloaded real agreements | Real difficulty | Someone else's text in the pipeline and, in quotes, in the repo |

## Decision

Sonnet falls back to Haiku and the reverse when a model cannot be reached; the run records which model answered. A request the provider rejects as wrong is not retried elsewhere. No real agreement is downloaded, read or sent to a model. Every document in the repo is generated.

## Why

- **Both models pass the same evals,** so falling back does not lower the bar that is measured.
- **Everything in the repo can be redistributed.**

## Cost

- A provider outage stops extraction and questions. Pay checks still work, with fixed wording.
- Nothing here is evidence about real agreements.

## When this breaks

As soon as a real customer's agreement is the input. The first real document will show where the reader and the rule model fall short, and the reader has already needed a second clause-numbering convention.

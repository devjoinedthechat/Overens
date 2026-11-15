# 5. Durable Task for the pipeline

Status: accepted, 2026-11-16

## Context

Ingestion reads a document, calls a model, then waits for a person for hours or days. A worker can die at any point. The target platform is Azure.

## Options

| Option | For | Against |
| --- | --- | --- |
| Durable Task Scheduler | Azure's own; an emulator for local runs; orchestrations as plain C# | Younger than the alternatives; local emulator image tags are unreliable |
| Temporal | Mature; strong tooling | Another service to run; not what an Azure team reaches for |
| A queue and status columns | No new dependency | Retries, timers and waiting for a signal rebuilt by hand |

## Decision

One orchestration per run, with the run's id as its instance id. Read and extract are activities with retries. The orchestration then waits for a `decision` event. Every step is safe to repeat: it checks for its own earlier work in the transaction that writes.

## Why

- **Waiting for a person is the central case,** and it is one line.
- **A dead worker is replaced.** A test stops the worker while a run waits, completes the review, and starts another worker.
- **Starting a run twice is harmless,** which lets the worker start runs the API stored and could not dispatch.

## Cost

- A document that cannot be read and a budget that is spent must not be retried, so those are returned as results, not thrown.
- The emulator image is pinned by digest: its `v0.0.10` tag is an old build that does not start.

## When this breaks

A team with no Azure footprint, or one that needs workflow versioning for long-lived orchestrations that change shape. Temporal covers both better.

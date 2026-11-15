# 10. The audit log is written by the database, and telemetry carries no content

Status: accepted, 2026-11-16

## Context

An audit log is only as complete as the code that remembers to write to it. Telemetry tends to collect whatever is convenient to attach, including what was read and said.

## Options

| Option | For | Against |
| --- | --- | --- |
| Log from application code | Flexible | A new code path forgets; a failed write after a committed change loses the entry |
| Log from triggers, in the change's own transaction | Cannot be skipped; cannot be out of step | Less context than the application has |
| Free-form span attributes | Easy to add detail | A clause, a question or an employee id ends up in a dashboard |

## Decision

Triggers append an entry for every stored document, started run, decision, approval and publication, each carrying the hash of the entry before it. Neither service role can write, change or remove an entry. The API may note two events that change no row, a pay check and a question, with sizes only. Spans take their attributes from a fixed list; a text value must be shaped like an id and not like a CPR number, or it is withheld.

## Why

- **An unlogged change is not possible,** and an edit to the log is detectable.
- **Telemetry is safe to share** with whoever operates the system.

## Cost

- The log holds staff user names, and is kept when a company is erased.
- The hash chain shows tampering after the fact. It does not prevent the database's owner from tampering.

## When this breaks

When the party to be defended against is the database's owner. The head of the chain would then have to be stored somewhere the owner cannot reach.

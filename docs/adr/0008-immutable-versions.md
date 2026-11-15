# 8. Rule sets are never changed, and the newest version takes over by date

Status: accepted, 2026-11-16

## Context

Agreements are renewed, corrected and amended by protocol. A payslip from two years ago must be checkable against the rules in force then, and it must be possible to say which version said what.

## Options

| Option | For | Against |
| --- | --- | --- |
| One current rule set, edited in place | Simple | The past is lost; an edit cannot be audited |
| Versions, newest wins entirely | Simple | A renewal starting in 2029 would erase the 2026 rules |
| Versions, each taking over a kind of rule from the day its first rule of that kind starts | Renewals and corrections both work; nothing is edited | A rule to reason about |

## Decision

A published rule set cannot be updated, deleted or truncated, even by the database's owner. The rules in force are computed from all versions: a later version takes over a kind of rule from the day its first rule of that kind starts. Before approving, a reviewer is shown which rates would differ on which days against what is published.

## Why

- **Any past payslip can be rechecked** against the rules of its date.
- **A version that quietly alters a past period shows up** in the comparison before it is approved.

## Cost

- A wrong published rule is corrected by publishing again, never by editing.
- Erasing a company's data needs a deliberate way through the immutability, which only the database's owner has.

## When this breaks

Agreements whose versions overlap in ways the take-over rule does not express, such as a protocol that changes one rate for a closed period in the middle of another rule's term without restating what follows.

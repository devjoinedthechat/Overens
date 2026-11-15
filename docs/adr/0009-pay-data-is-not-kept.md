# 9. Pay data is checked and not kept

Status: accepted, 2026-11-16

## Context

Timesheets and payslips are personal data about employees who never chose to use this system. In Denmark pay records often carry CPR numbers.

## Options

| Option | For | Against |
| --- | --- | --- |
| Store pay data and findings | History; trends; re-checks without re-upload | A store of salary data to secure, justify and delete |
| Check statelessly and return the findings | Nothing to leak or delete | No history; the caller keeps the result |

## Decision

A pay check stores nothing about the employee. An employee id shaped like a CPR number is refused. Anything shaped like a CPR number is removed from every model call at the layer next to the provider. The audit log records that a check was made and how many findings it had. A model is shown one finding's figures and clause, never an id.

## Why

- **The safest personal data is the data not held.**
- **One place enforces the CPR rule** for every caller, so no caller can forget it.

## Cost

- No regression replay of past payslips against a changed rule set. The comparison of rule sets stands in for it.
- The redaction also removes any other ten-digit number shaped like a CPR number.

## When this breaks

A customer who needs findings kept for audit. That is a different product decision with a retention policy behind it.

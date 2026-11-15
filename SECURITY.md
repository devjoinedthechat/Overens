# Security

## Reporting a vulnerability

Report it privately through [GitHub's advisory form](https://github.com/devjoinedthechat/Overens/security/advisories/new). Do not open a public issue.

Say what you found, how to reproduce it, and what it lets someone do. You will get an answer within a week.

## Scope

In scope is the code in this repository:

- the API, the worker and the console
- the database schema, its roles and row-level security
- the checks that stand between a model's answer and a person

Out of scope:

- the demo sign-in in `infra/keycloak`. Its users and their password are published on purpose and are for one machine only.
- the model provider and the other services this code calls

## What the code is meant to hold

[The threat model](docs/threat-model.md) lists what the system protects, from whom, and what it does not protect. A finding that breaks one of its claims is the kind most worth reporting.

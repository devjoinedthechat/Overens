# 3. A person publishes, and the database insists

Status: accepted, 2026-11-16

## Context

A rule set decides whether payslips are right. A wrong rule published silently would produce wrong findings for every payslip checked against it.

## Options

| Option | For | Against |
| --- | --- | --- |
| Publish what the model extracts | No waiting | A misread rate goes straight into use |
| Require approval in the API | Simple | A bug, a second code path or a compromised worker bypasses it |
| Require approval in the database | No code path can publish without it | More SQL; roles to manage |

## Decision

Rows reach `rule_sets` only through one function. Only the worker's role may call it, and it refuses unless an approval row exists. Only the API's role may write an approval, for a signed-in person. The worker cannot approve, cannot decide on a rule and cannot insert a rule set. The API cannot publish. Once a run is approved its decisions are frozen.

## Why

- **The property holds whatever the application does.** It is tested by connecting as each role and trying.
- **A reviewer's approval means something.** What was approved is what is published; nothing can be swapped in between.

## Cost

- Three database roles and their grants.
- A run that is approved cannot be amended. A mistake after approval needs another upload.

## When this breaks

A team that wants reviewers to edit a proposed rule. That is not built: a reviewer approves or rejects, and a rejected rule leaves a gap until the agreement is uploaded again.

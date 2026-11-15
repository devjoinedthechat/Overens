# 6. Own agent loop instead of a framework

Status: accepted, 2026-11-16

## Context

The question agent needs a handful of tools, a turn limit, a budget, and a check on its final answer. Frameworks offer loops, memory and tool plumbing.

## Options

| Option | For | Against |
| --- | --- | --- |
| Microsoft Agent Framework or Semantic Kernel | What an Azure team may already use; less code | The controls become callbacks around someone else's control flow |
| Own loop on `IChatClient` | Every control is ordinary code with ordinary tests | More to write; no ecosystem |

## Decision

A loop of about a hundred lines on `Microsoft.Extensions.AI`. The model can only call five declared tools. Its answer arrives through one of them, so it can be checked before anyone sees it. Text outside a tool call is never shown.

## Why

- **The interesting part is between the model's turn and what is shown.** In a framework that is a filter; here it is the loop.
- **It runs on any `IChatClient`.** Tests script a model; evals replay recorded turns.

## Cost

- Tool choice cannot be forced on every model, so nothing is forced and a turn without a tool call is handled explicitly.
- No memory across questions. Each is answered from the agreement.

## When this breaks

Many agents and tools maintained by several teams, where shared conventions matter more than seeing every step.

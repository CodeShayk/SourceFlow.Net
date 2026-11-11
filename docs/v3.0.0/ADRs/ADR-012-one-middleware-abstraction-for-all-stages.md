# ADR-012: One middleware abstraction for all stages

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

v2 has four near-identical middleware interfaces (EXT-01), each building its own pipeline.

## Decision

One interface, `IMessageMiddleware`, with `ValueTask InvokeAsync(MessageContext ctx, MessageDelegate next)`. `ctx.Stage` is one of `Send`, `Handle`, `Receive`, `Publish` or `Project`. Registration can filter by stage and message type. Pipelines are composed once per (stage, message type) and cached in a `FrozenDictionary`. Built-in behaviours are themselves middleware: telemetry, logging scope, redaction, payload protection, inbox, validation and retry.

## Consequences

(+) DRY, one mental model, cheap composition. (−) Middleware authors check `ctx.Stage` when they care about it.

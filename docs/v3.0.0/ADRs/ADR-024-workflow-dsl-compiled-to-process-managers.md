# ADR-024: Workflow DSL (YAML) compiled to process managers

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

The product now requires a saga DSL and a visual designer for workflows that span the services of a business domain. The workflows must show success, failure, retry and rollback paths ([product doc 07](../../product/07-Workflow-Designer-Requirements.md)). The original design had put this in the YAGNI register ([doc 01 §4](../01-Architecture-Overview.md#4-deliberately-not-built-yagni-register)), with "repeated user demand" as the trigger; that trigger has now occurred.

v3 already has a durable process-manager runtime ([ADR-020](ADR-020-process-managers-with-durable-state-and-timers.md)): state with optimistic concurrency, outbox commands, durable timers and inbox deduplication. Command ownership is fixed ([ADR-007](ADR-007-single-command-ownership-route-local-xor-remote.md)), and rejections are values that can be replied to the sender ([ADR-018](ADR-018-business-rejections-are-values-not-exceptions.md)).

## Decision

1. **The source of truth is a small, versioned YAML DSL** (`sourceflow.workflow/1`), validated by a JSON Schema plus semantic rules WF-V01…V16. It has explicit domain concepts: owner service, steps sending **commands to their owning service**, awaited events, rejection codes, timeouts, business retries, compensations, a pivot, and manual intervention.
2. **Compile, don't interpret.** The Roslyn `WorkflowGenerator` turns each workflow into an ordinary `[ProcessManager]` in the owning service. There is no second execution model, and the runtime, storage, telemetry and tests are the same as for hand-written process managers.
3. **One semantic core.** `SourceFlow.Workflows` (C#) implements parsing, validation, simulation and path enumeration. The generator, the CLI and the browser (through WebAssembly, ADR-025) all use it.
4. **Expressions use CESQL** (shared with SFQL and subscription filters). Anything more complex uses `$hook` partial methods written in C#, which must be pure.
5. **Versioning:** `name@version` side by side. In-flight instances finish on the version they started on.

## Alternatives

| Option | Why not chosen |
|--------|----------------|
| C# fluent state-machine DSL (MassTransit-style) | Code-first DSLs don't round-trip losslessly with a visual designer, and domain experts can't review them |
| A runtime workflow interpreter (engine) | A second execution model beside process managers (violates DRY and ADR-020). Harder to version in-flight instances, to make AOT-safe and to test |
| BPMN 2.0 as the DSL | Verbose XML; generic tasks don't map to aggregate-owned commands and rejection codes. **Offered as an export** |
| CNCF Serverless Workflow 1.0 | Generic function and HTTP orchestration. Commands, aggregates and compensation would need extensions, losing compile-time checks against manifests. A possible future export |
| AWS Step Functions ASL | Vendor-specific; we need a cloud-neutral format |
| SCXML / XState statecharts | Strong state semantics but no domain concepts. Statechart *visual conventions* are borrowed |

## Consequences

- (+) Workflows can be reviewed, diffed and tested, and their failure paths are validated at build time. Zero extra runtime machinery.
- (+) Simulation in the designer matches production exactly, because the code is shared and differential tests check it.
- (−) A DSL schema to maintain and evolve (versioned, with a migration command).
- (−) The generator now bundles `SourceFlow.Workflows` and YamlDotNet in the analyzer package, which has to be packaged carefully (implementation risk IR-14).

## Traces

WFD-20…29, WFD-NFR-1, WFD-NFR-2, WFD-NFR-6.

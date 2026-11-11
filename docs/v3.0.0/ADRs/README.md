# SourceFlow.Net v3 — Architecture Decision Records

One file per decision, in the [MADR](https://adr.github.io/madr/)-style layout: Context → Decision → Alternatives → Consequences → Traces.
Every ADR is **Proposed** until the v3 architecture review. "Traces" lists the [product requirements](../../product/README.md) and gaps the decision addresses.

## Rules for this folder

- One ADR per file, named `ADR-nnn-short-title.md`. Numbers are never reused.
- An accepted ADR is not edited in substance. To change a decision, write a new ADR and mark the old one **Superseded by ADR-nnn**.
- Status values: Proposed → Accepted | Rejected; Accepted → Deprecated | Superseded.

## Index

| ADR | Title | Status |
|-----|-------|--------|
| [ADR-001](ADR-001-target-dotnet-10-and-csharp-14.md) | Target .NET 10 and C# 14 only | Proposed |
| [ADR-002](ADR-002-event-sourcing-replaces-command-sourcing.md) | Event sourcing replaces command sourcing | Accepted |
| [ADR-003](ADR-003-aggregates-as-pure-deciders-decide-evolve.md) | Aggregates as pure deciders (decide/evolve) | Proposed |
| [ADR-004](ADR-004-source-generation-instead-of-reflection.md) | Compile-time source generation instead of runtime reflection | Proposed |
| [ADR-005](ADR-005-logical-message-type-names-and-registry.md) | Stable logical message type names, with a registry as allow-list | Proposed |
| [ADR-006](ADR-006-transactional-outbox-and-inbox-for-effectively-once.md) | Transactional outbox and inbox for effectively-once processing | Accepted |
| [ADR-007](ADR-007-single-command-ownership-route-local-xor-remote.md) | Single command ownership: route local XOR remote | Accepted |
| [ADR-008](ADR-008-optimistic-concurrency-with-automatic-re-decide.md) | Optimistic concurrency with automatic re-decide | Proposed |
| [ADR-009](ADR-009-event-store-on-adonet-postgresql-first.md) | Event store on raw ADO.NET; PostgreSQL first | Proposed |
| [ADR-010](ADR-010-commit-ordered-global-position-for-gap-free.md) | Commit-ordered global position for gap-free subscriptions | Proposed |
| [ADR-011](ADR-011-cloudevents-envelope-and-w3c-trace-context.md) | CloudEvents 1.0 envelope and W3C trace context | Proposed |
| [ADR-012](ADR-012-one-middleware-abstraction-for-all-stages.md) | One middleware abstraction for all stages | Proposed |
| [ADR-013](ADR-013-use-platform-libraries-delete-bespoke-equivalents.md) | Use platform libraries; delete bespoke equivalents | Proposed |
| [ADR-014](ADR-014-transport-spi-and-conformance-kit-parity-required.md) | Transport SPI and conformance kit; parity required for GA | Accepted |
| [ADR-015](ADR-015-strongly-typed-caller-assigned-ids-uuidv7-default.md) | Strongly typed, caller-assigned ids (UUIDv7 default) | Accepted |
| [ADR-016](ADR-016-projections-inline-or-async-with-partitioned-fenced.md) | Projections: inline or async, with partitioned, fenced subscriptions | Accepted |
| [ADR-017](ADR-017-provisioning-modes-and-topology-as-data.md) | Provisioning modes and topology as data | Proposed |
| [ADR-018](ADR-018-business-rejections-are-values-not-exceptions.md) | Business rejections are values, not exceptions | Proposed |
| [ADR-019](ADR-019-telemetry-always-on-with-standard-semantic-conventions.md) | Telemetry always on, with standard semantic conventions | Proposed |
| [ADR-020](ADR-020-process-managers-with-durable-state-and-timers.md) | Process managers with durable state and timers replace "Saga" | Proposed |
| [ADR-021](ADR-021-dynamic-consistency-boundaries-dcb-in-3-1.md) | Dynamic consistency boundaries (DCB): schema-ready in 3.0, API in 3.1 | Proposed |
| [ADR-022](ADR-022-conjoined-multi-tenancy-with-row-level-security.md) | Conjoined multi-tenancy with row-level security in 3.0 | Proposed |
| [ADR-023](ADR-023-sfql-query-language-with-cesql-predicates.md) | SFQL: a read-only query language with CESQL-compatible predicates | Accepted |
| [ADR-024](ADR-024-workflow-dsl-compiled-to-process-managers.md) | Workflow DSL (YAML) compiled to process managers | Proposed |
| [ADR-025](ADR-025-studio-react-spa-with-shared-wasm-core.md) | SourceFlow Studio: React SPA with a shared WebAssembly core, hosted locally | Proposed |
| [ADR-026](ADR-026-per-service-query-api-with-sql-syntax-and-injection-proof-compilation.md) | Per-service query API with a SQL syntax and injection-proof compilation | Proposed |
| [ADR-027](ADR-027-single-core-package-with-extensibility-spis-and-categorised-adapters.md) | Single core package with extensibility SPIs, and categorised adapter packages | Accepted |
| [ADR-028](ADR-028-postgresql-payload-columns-and-adapter-refinements.md) | PostgreSQL payload columns are `json`, plus adapter refinements | Accepted |
| [ADR-029](ADR-029-schema-catalog-derived-from-serializer-metadata.md) | The schema catalog is derived at startup from the serializer's source-generated metadata | Accepted |
| [ADR-030](ADR-030-domain-queries-bind-at-startup.md) | Domain queries are checked in two stages: shape at build time, binding at startup | Accepted |
| [ADR-031](ADR-031-cli-runs-store-commands-globally-and-model-commands-in-the-service.md) | The CLI runs store commands as a global tool and model commands inside the service | Accepted |
| [ADR-032](ADR-032-blue-green-read-model-generations-behind-forward-only-aliases.md) | Blue/green read-model generations behind forward-only aliases | Accepted |

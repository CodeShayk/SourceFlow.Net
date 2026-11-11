# v3 implementation status

Updated in the PR that completes a work package (CONTRIBUTING.md). Status values: `todo`, `in progress`, `done`.

| WP | Title | Estimate (ew) | Status | Notes |
|----|-------|---------------|--------|-------|
| WP-0.1 | Branch and versioning | 0.5 | done | |
| WP-0.2 | Repository scaffold | 1 | done | |
| WP-0.3 | CI pipelines (skeleton) | 1 | done | |
| WP-0.4 | Quality harness | 1 | done | |
| WP-0.5 | Design baseline | 0.5 | done | |
| WP-1.1 | Core contracts and SPIs (`SourceFlow.Net`) | 1.5 | done | Contracts in namespace SourceFlow; UT-ABS-01..18 |
| WP-1.2 | `SourceFlow.Generators` (generators + analyzers) | 4 | in progress | Generator (ids, aggregates, PMs, projections, module) + SF0001-0004, 0007-0009 done; SF0005/0006/0010/0014 pending |
| WP-1.3 | Messaging core | 2 | done | Envelope, CloudEvents, UUIDv5/v7, registry, serializer, upcasters; UT-MSG-01..28 |
| WP-1.4 | Storage SPI + in-memory store | 2.5 | done | Storage SPI, InMemoryStore, SCK (49 cases) green |
| WP-1.5 | Runtime model and routing | 1.5 | done | RoutingBuilder/RoutingTable (RT-R1, RT-R5), options + validators; UT-RT |
| WP-1.6 | Command processing + temporal reads | 3 | in progress | CommandProcessor, CommandSender, AggregateReader (AsOf) done; snapshots and aggregate cache not wired yet |
| WP-1.7 | Middleware and diagnostics | 1.5 | done | MessagePipeline, built-in middleware, ErrorClassifier, SourceFlowMetrics with InstrumentAdvice; UT-MW |
| WP-1.8 | Outbox, scheduler, transport SPI, in-memory broker, receive pipeline | 3.5 | in progress | Transport SPI, InMemoryBroker, OutboxRelay, ReceiveEndpoint, dead letters done; transport conformance kit (TCK) pending |
| WP-1.9 | Subscriptions, projections, process managers, event handlers | 4 | in progress | SubscriptionEngine (fenced, partitioned), projections, process managers, event handlers done; CommandRejected replies (E2E-15) and node fair-share pending |
| WP-1.10 | Hosting, roles, tenancy context | 1.5 | done | AddSourceFlow, SourceFlowBuilder Use* slots, roles, SourceFlowHost lifecycle, health check |
| WP-1.11 | `SourceFlow.Testing` | 2.5 | done | SourceFlowTestHost, AggregateSpec, ProcessManagerSpec, ProjectionSpec, approvals; UT-TST |
| WP-1.12 | Schema catalog | 2 | todo | |
| WP-1.13 | SFQL front end (pipeline + SQL syntax) + in-memory interpreter + domain queries | 5.5 | todo | |
| WP-1.14 | Bank sample (ported v2 E2E domain) | 1.5 | in progress | Bank.Contracts/Accounts/Transfers/Notifications + E2E-01..07, 11, 13, 14, 17..20 green; Bank.Api waits for WP-4.2 |
| WP-1.R | Retire v2 core | 0.5 | todo | |
| WP-2.1 | PostgreSQL schema and migrator | 1.5 | todo | |
| WP-2.2 | PostgreSQL append and stream read | 2 | todo | |
| WP-2.3 | Global log reader and notifications | 1.5 | todo | |
| WP-2.4 | Outbox, inbox, checkpoints, leases, dead letters, process state, documents | 2.5 | todo | |
| WP-2.5 | Snapshots and aggregate cache | 1.5 | todo | |
| WP-2.6 | Tenancy with row-level security | 1 | todo | |
| WP-2.7 | `SourceFlow.Storage.EntityFrameworkCore` read models | 2 | todo | |
| WP-2.8 | SFQL on PostgreSQL (incl. joins and grouping) + index sync + domain queries | 3.5 | todo | |
| WP-2.9 | Storage conformance kit complete | 2 | todo | |
| WP-2.10 | Performance baseline | 1 | todo | |
| WP-2.R | Retire v2 EF stores | 0.5 | todo | |
| WP-3.1 | Transport SPI final + Transport Conformance Kit | 3 | todo | |
| WP-3.2 | `SourceFlow.Cloud.Aws` | 4 | todo | |
| WP-3.3 | `SourceFlow.Cloud.Gcp` | 3.5 | todo | |
| WP-3.4 | `SourceFlow.Cloud.Azure` | 4 | todo | |
| WP-3.5 | Provisioning, topology export, DLQ ingestor, IaC modules | 2.5 | todo | |
| WP-3.6 | Cross-service E2E per transport | 1.5 | todo | |
| WP-3.R | Retire v2 cloud providers | 0.5 | todo | |
| WP-4.1 | `SourceFlow.Tool` (`dotnet sourceflow`) | 3 | todo | |
| WP-4.2 | `SourceFlow.AspNetCore` (incl. the per-service query API) | 2.5 | todo | |
| WP-4.3 | Health checks, dashboards and alerts | 2 | todo | |
| WP-4.4 | `SourceFlow.Storage.SqlServer` + SFQL SQL Server compiler | 4 | todo | |
| WP-4.5 | `PartitionEvents` + stream archiving | 1.5 | todo | |
| WP-4.6 | OpenTelemetry integration (no package) | 1 | todo | |
| WP-4.7 | Chaos suite + tier-M load test | 2.5 | todo | |
| WP-5.1 | Payload protection + KMS key providers | 3 | todo | |
| WP-5.2 | Crypto-shredding | 3 | todo | |
| WP-5.3 | Redaction wiring | 0.5 | todo | |
| WP-5.4 | v2 migration tool + v2 wire bridge | 3 | todo | |
| WP-5.5 | Fuzzing + security review | 1.5 | todo | |
| WP-S.1 | Static manifest emission | 2 | todo | |
| WP-S.2 | Design-time manifest export + workspace | 1.5 | todo | |
| WP-S.3 | DSL core: model, reader, schema, validator | 3.5 | todo | |
| WP-S.4 | Simulator, path enumerator, spec generator, exporters | 3 | todo | |
| WP-S.5 | Workflow code generation + runtime abstractions | 4 | todo | |
| WP-S.6 | Runtime support: metrics, CLI, version guard | 1.5 | todo | |
| WP-S.7 | WASM core + TypeScript facade | 1.5 | todo | |
| WP-S.8 | Studio shell + local host | 2.5 | todo | |
| WP-S.9 | Views: domain map, statechart, swimlane | 3.5 | todo | |
| WP-S.10 | Editing and round trip | 3 | todo | |
| WP-S.11 | Simulation, path explorer, spec generation UI | 2 | todo | |
| WP-S.12 | Exports, static build, PR diagram action | 2 | todo | |
| WP-S.13 | OrderFulfilment sample, docs, hardening | 2 | todo | |

## Notes

- WP-0.4: the benchmark harness lands with the first measurable code (WP-1.3 serializer benchmarks); the coverage gate is `tools/CoverageGate` with `thresholds.json`.
- WP-0.5: GitHub issues per WP are opened by the maintainers; this table is the in-repo tracker.
- Snapshot approvals use `SourceFlow.Testing.Approvals` (approved files), not Verify (licence decision pending).

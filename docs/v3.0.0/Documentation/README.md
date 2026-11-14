# SourceFlow.Net v3.0.0 documentation

How to use SourceFlow.Net v3 and how it works, as built. Start with the [wiki](wiki.md) for a complete walk-through, or go
straight to the package you are using.

Every package targets .NET 10 (`net10.0`). The documentation for v1 and v2 is in [docs/legacy-version](../../legacy-version).

## Start here

| Document | For |
|----------|-----|
| [Wiki: the complete guide](wiki.md) | Concepts, getting started, every feature end to end, best practices, production checklist, FAQ |
| [Changelog for v3.0.0](Versions/v3.0.0/CHANGELOG.md) | What is new, what changed from v2, what was removed |
| [Migrating from v2](../guides/migrating-from-v2.md) | Moving a v2 service and its data to v3 |

## Packages

| Package | README |
|---------|--------|
| `SourceFlow.Net` (core, source generator and analyzers) | [SourceFlow.Net-README.md](SourceFlow.Net-README.md) |
| `SourceFlow.Storage.PostgreSQL` | [SourceFlow.Storage.PostgreSQL-README.md](SourceFlow.Storage.PostgreSQL-README.md) |
| `SourceFlow.Storage.SqlServer` | [SourceFlow.Storage.SqlServer-README.md](SourceFlow.Storage.SqlServer-README.md) |
| `SourceFlow.Storage.EntityFrameworkCore` | [SourceFlow.Storage.EntityFrameworkCore-README.md](SourceFlow.Storage.EntityFrameworkCore-README.md) |
| `SourceFlow.Cloud.Aws` | [SourceFlow.Cloud.Aws-README.md](SourceFlow.Cloud.Aws-README.md) |
| `SourceFlow.Cloud.Azure` | [SourceFlow.Cloud.Azure-README.md](SourceFlow.Cloud.Azure-README.md) |
| `SourceFlow.Cloud.Gcp` | [SourceFlow.Cloud.Gcp-README.md](SourceFlow.Cloud.Gcp-README.md) |
| `SourceFlow.AspNetCore` | [SourceFlow.AspNetCore-README.md](SourceFlow.AspNetCore-README.md) |
| `SourceFlow.Testing` | [SourceFlow.Testing-README.md](SourceFlow.Testing-README.md) |
| `SourceFlow.Tool` (`dotnet sourceflow`) | [SourceFlow.Tool-README.md](SourceFlow.Tool-README.md) |
| `SourceFlow.Studio`, `@sourceflow/studio` and the `workflow-diff` action | [SourceFlow.Studio-README.md](SourceFlow.Studio-README.md) |

## Architecture

How the framework works inside: [Architecture](Architecture/README.md).

| # | Document |
|---|----------|
| 01 | [Architecture overview](Architecture/01-Architecture-Overview.md) |
| 02 | [Command flow](Architecture/02-Command-Flow.md) |
| 03 | [Event flow](Architecture/03-Event-Flow.md) |
| 04 | [Messaging pipeline and middleware](Architecture/04-Messaging-Pipeline-and-Middleware.md) |
| 05 | [Event store and persistence](Architecture/05-Event-Store-and-Persistence.md) |
| 06 | [Projections and read models](Architecture/06-Projections-and-Read-Models.md) |
| 07 | [Cloud transports](Architecture/07-Cloud-Transports.md) |
| 08 | [Query side and SFQL](Architecture/08-Query-Side-and-SFQL.md) |
| 09 | [Process managers and workflows](Architecture/09-Process-Managers-and-Workflows.md) |
| 10 | [Security and multi-tenancy](Architecture/10-Security-and-Multi-Tenancy.md) |

## Guides

| Guide | Covers |
|-------|--------|
| [Message idempotency](Message-Idempotency-Guide.md) | Effectively-once delivery: outbox, inbox, command ids, idempotent side effects |
| [Cloud integration testing](Cloud-Integration-Testing.md) | Emulators, the transport conformance kit, end-to-end and real-cloud tests |
| [Security and compliance](Security-and-Compliance-Guide.md) | Personal data, encryption and crypto-shredding, query permissions, production checklist |
| [Observability and performance](Observability-and-Performance.md) | Traces, metrics, health checks, dashboards, benchmarks and tuning |
| [GitHub Actions setup](GitHub-Actions-Setup.md) | The CI workflows, quality gates, secrets and versioning |
| [Workflows and SourceFlow Studio](../guides/workflows.md) | Writing, wiring, testing and operating a workflow |
| [Workflow DSL reference](../guides/workflow-dsl-reference.md) | Every key of `*.sfflow.yaml` |
| [OpenTelemetry](../guides/opentelemetry.md) | Subscribing to SourceFlow's traces and metrics |
| [Reading generated code](../guides/reading-generated-code.md) | What the source generator writes and how to review it |

## Samples

| Sample | Shows |
|--------|-------|
| [TravelBooking](../../../samples/TravelBooking/README.md) | Two services, Bookings and Hotels, on PostgreSQL with Amazon SNS/SQS (LocalStack) between them, and the trip-booking workflow: retries, timeouts, compensation, a point of no return and manual intervention. Aggregates, projections, a domain query, HTTP endpoints, the query API, health checks, OpenTelemetry, the in-service CLI, the Studio workspace and generated specs. `docker compose up -d --build` from the repository root starts it with SourceFlow Studio (read-only) and the Aspire dashboard. |

The test suites also run against two fixture domains in [`tests/Fixtures`](../../../tests/Fixtures): a Bank domain and the
OrderFulfilment services. They are test data, not examples.

## Design and decisions

These describe why v3 is built the way it is. Where they differ from the documentation here, their "As built" sections and
the code are what shipped.

- [Design documents](../Design/README.md) (01 to 15)
- [Architecture decision records](../ADRs/README.md) (ADR-001 to ADR-033)
- [Implementation plans and status](../implementation/README.md)

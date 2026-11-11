# SourceFlow.Net — Product Requirements

This folder holds the product requirements for SourceFlow.Net. They were **reverse-engineered from the source code** at commit `086673b` (master, 10 Nov 2026, version 2.0.0) and cross-checked against the existing architecture docs, READMEs, changelogs and tests.

They record two things:

1. **What the product must do.** Each requirement is a testable "shall" statement.
2. **What the code does today.** Each requirement has a status and a link to the code that implements it, so the documents can double as a gap analysis.

## Documents

| # | Document | Covers |
|---|----------|--------|
| 01 | [Product Vision and Scope](01-Product-Vision-and-Scope.md) | Problem, vision, users, goals, non-goals, packages, key concepts, user journeys, roadmap |
| 02 | [Core Framework Requirements](02-Core-Framework-Requirements.md) | Domain model (aggregates, sagas, views), the command and event pipelines, command log and replay, store contracts, DI registration, middleware, telemetry |
| 03 | [Persistence (Entity Framework) Requirements](03-Persistence-EntityFramework-Requirements.md) | EF Core stores, database providers, schema, naming, resilience, idempotency store |
| 04 | [Cloud Messaging Requirements](04-Cloud-Messaging-Requirements.md) | Bus configuration, bootstrapping, AWS (SQS/SNS), GCP (Pub/Sub), idempotency, dead letters, encryption, circuit breaker, health, cloud telemetry |
| 05 | [Non-Functional Requirements](05-Non-Functional-Requirements.md) | Compatibility, delivery and consistency guarantees, security, performance, observability, testability, packaging and release |
| 06 | [Gaps, Risks and Open Questions](06-Gaps-Risks-and-Open-Questions.md) | Places where the code falls short of a requirement or of its own docs, with evidence and a suggested fix |
| 07 | [Workflow DSL and Visual Designer Requirements](07-Workflow-Designer-Requirements.md) | **New for v3.** Saga DSL and a React state-machine designer spanning the services of a business domain: success, failure, retry and rollback paths |

## Requirement IDs

Each requirement has a stable ID: `<AREA>-<NN>`. Areas:

| Prefix | Area | Doc |
|--------|------|-----|
| `DOM` | Domain modelling (aggregates, sagas, views, entities) | 02 |
| `CMD` | Command model, bus, command log, replay | 02 |
| `EVT` | Event model and event pipeline | 02 |
| `STO` | Store contracts and adapters | 02 |
| `REG` | Dependency-injection registration and discovery | 02 |
| `EXT` | Middleware and extension points | 02 |
| `OBS` | Domain observability | 02 |
| `EF`  | Entity Framework persistence | 03 |
| `BUS` | Cloud bus configuration and bootstrapping | 04 |
| `IDM` | Idempotency (duplicate-message detection) | 04 |
| `DLQ` | Dead-letter handling | 04 |
| `RES` | Resilience (retry, circuit breaker) | 04 |
| `SEC` | Message security (encryption, masking, credentials) | 04 |
| `CTL` | Cloud telemetry and health | 04 |
| `AWS` | AWS provider | 04 |
| `GCP` | GCP provider | 04 |
| `NFR` | Non-functional | 05 |
| `GAP` | Gap or risk finding | 06 |
| `WFD` | Workflow DSL and visual designer (new requirement, status Planned) | 07 |

## Status values

| Status | Meaning |
|--------|---------|
| **Implemented** | Built, wired by the standard registration call, and covered by tests or clearly exercised. |
| **Partial** | Built, but incomplete or with a caveat that matters. The caveat is noted, usually with a `GAP` reference. |
| **Opt-in** | The code exists but the standard registration call does not wire it. Consumers must register it themselves. |
| **Planned** | On the published roadmap but not in the code. |

## Maintaining these documents

- When behaviour changes, update the requirement's status and source link in the same pull request.
- When a `GAP` item is fixed, mark it **Resolved** in doc 06 and update the requirement it references.
- Keep requirement IDs stable. Retire an ID by striking it through rather than renumbering.

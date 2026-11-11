# ADR-017: Provisioning modes and topology as data

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

v2 always creates resources at startup, so runtime credentials need create permissions and resources drift from infrastructure-as-code (GAP-12).

## Decision

Routing produces a `Topology` model (endpoints, topics, subscriptions, dead-letter queues, ordering, encryption). `ProvisioningMode` is one of:
- `CreateIfMissing` (the default in Development);
- `Validate` (the default in Production): check existence and compatible configuration, and fail fast otherwise;
- `None`.

`dotnet sourceflow topology export --format json` writes the model out for IaC.

## Consequences

(+) Least privilege, and drift is detected at startup. (−) Production teams must create resources through IaC or a release step.

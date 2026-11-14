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

**Amendment (WP-C.13, CR-41).**
- **Encryption is a transport option.** It isn't part of the topology model. On AWS, `RequireEncryption` and `KmsKeyId` apply; on Google Cloud, `KmsKeyName`.
- **Validate checks it there.** It reports `Unencrypted` resources, and `CreateIfMissing` creates them encrypted.
- **Azure Service Bus is not checked.** Its customer-managed key is a namespace property that only Azure Resource Manager shows, so it is required at deployment instead, with Azure Policy or the IaC modules.
- **Existing resources are never changed.** On AWS, a queue policy keeps every statement except SourceFlow's own, which is merged by its Sid (CR-24).

## Consequences

(+) Least privilege, and drift is detected at startup. (−) Production teams must create resources through IaC or a release step.

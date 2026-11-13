# ADR-001: Target .NET 10 and C# 14 only

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

v2 multi-targets netstandard2.0, netstandard2.1 and net8–10 (NFR-CMP-01). That blocks `static abstract` interface members, `FrozenDictionary`, `TimeProvider`, `Guid.CreateVersion7`, `System.Threading.Lock`, metric instrument advice, keyed DI, the `IHostedLifecycleService` ordering, and full Native AOT annotations. It also needs `#if` branches (see the v2 `IocExtensions` in the AWS provider).

## Decision

Every package targets `net10.0` and is compiled with C# 14 (extension members, the `field` keyword, null-conditional assignment). Packages are annotated `IsAotCompatible=true` and `IsTrimmable=true`.

## Alternatives

(a) Keep net8 LTS as well. That doubles the test matrix and loses .NET 9/10 APIs. Rejected because .NET 10 is the current LTS, released Nov 2025. (b) Keep netstandard. Rejected: it forces reflection-based designs.

## Consequences

(+) Smaller, faster code; AOT; one build. (−) .NET Framework and older runtimes stay on v2. v2 receives security fixes for 12 months after v3 GA ([doc 12](../Design/12-Migration-Delivery-and-Traceability.md)).

# ADR-004: Compile-time source generation instead of runtime reflection

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

v2 scans assemblies with reflection (REG-03, GAP-16), builds generic methods with `MakeGenericMethod` on every message, instantiates types with `Activator.CreateInstance`, and resolves `Type.GetType` from wire strings. That is slow, not trimmable, and fails at runtime instead of at build time.

## Decision

Incremental source generators produce:
- per module: a `[SourceFlowModule]` registration (handlers, projections, process managers, type registry entries);
- per aggregate: the `Evolve` and `Decide` switches and the command→stream-id extractor;
- per message assembly: **no** JSON context. Roslyn generators can't see each other's output, so a SourceFlow-generated `[JsonSerializable]` context would never be processed by the System.Text.Json generator. Each module instead **declares** its own `JsonSerializerContext` (`[SourceFlowModule(typeof(AccountsJsonContext))]`). Analyzer SF0010 checks that every message, document and process-state type in the module is listed, and a code fix adds missing entries (design correction DC-1, found during the implementation spike);
- strongly typed id types (`Parse`, `TryFormat`, a JSON converter, a stream-id formatter).

Analyzers enforce the contracts (§ SF diagnostics in [doc 03](../Design/03-Domain-Programming-Model.md#9-analyzers)). The runtime uses `FrozenDictionary` lookups built from the generated tables.

## Alternatives

Reflection with caching (v2 listeners). It still isn't AOT-safe and still fails late.

## Consequences

(+) AOT, faster startup, errors at compile time. (−) Generator maintenance cost; users need the generator in their build (it ships inside `SourceFlow.Net` under `analyzers/dotnet/cs`; it targets `netstandard2.0` as a compiler plugin, the only non-`net10.0` project, see [ADR-027](ADR-027-single-core-package-with-extensibility-spis-and-categorised-adapters.md)).

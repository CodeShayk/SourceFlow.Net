# ADR-005: Stable logical message type names, with a registry as allow-list

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

v2 identifies stored and wire types by CLR assembly-qualified name (GAP-04, NFR-CMP-06), and listeners instantiate whatever type the sender names (GAP-10).

## Decision

Every message carries `[MessageType("bank.account.deposited", Version = 1)]`. The registry maps `(name, version)` ↔ CLR type and is built **only** from generated module registrations. Unknown names are rejected before deserialisation. Schema evolution works in two ways: additive changes rely on the tolerant reader (STJ ignores unknown members, and missing members get defaults), and breaking changes go through **upcasters** (`IUpcaster<TFrom, TTo>`), which the registry chains when reading. Renaming the CLR type doesn't affect stored data.

## Alternatives

(a) CLR full names without the assembly: still breaks on rename or move. (b) A schema registry service (Avro or Protobuf): heavyweight, and JSON is the norm for this audience. A pluggable `IMessageSerializer` keeps this option open.

## Consequences

(+) Refactoring-safe storage and wire formats; secure deserialisation. (−) Authors must name their messages. The analyzer SF0001 flags a missing `[MessageType]`.

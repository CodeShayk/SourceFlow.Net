# ADR-030: Domain queries are checked in two stages: shape at build time, binding at startup

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-11 |
| **Deciders** | v3 architecture review; WP-1.13 implementation |
| **Amends** | [doc 14](../Design/14-Query-Side-and-SFQL.md) Q-B4 ("the generator binds every `[DomainQuery]` at build time against the catalog") |

## Context

Doc 14 says analyzer SF0018 binds each `[DomainQuery]` against the catalog at build time, so a broken contract fails compilation. That needs two things the compiler doesn't have:

- **The SFQL parser and binder.** They live in `SourceFlow.Net` (`net10.0`). Analyzers and generators load into the compiler as `netstandard2.0` assemblies (ADR-004, ADR-027), so they can't reference them.
- **The service's catalog.** It is built from the runtime model (ADR-029): every module the host adds, the serializer's naming policy, and its converters. A domain query in a module assembly is often bound against read models from other modules that the analyzer can't see.

## Decision

1. **SF0018 at build time** checks what the compilation can decide on its own, and reports an error when:
   - the attribute has no name;
   - the type doesn't implement `IDomainQuery<TResult>`;
   - the type isn't a top-level partial record or class;
   - there is no `const string Sfql` (or `Sql`);
   - the result has no public constructor with parameters;
   - an `@parameter` in the text, outside string literals, has no matching member.
2. **Binding at startup.** `SourceFlowHost` binds every registered domain query against the service's own catalog before it reports ready, using the real parser and binder. It checks three things:
   - the text binds (SFQ01xx/02xx diagnostics with positions);
   - every bound parameter is a member;
   - every result constructor parameter is an output column (matched case-insensitively).

   Any failure stops startup with `DomainQueryException`, whose message starts with "SF0018". A broken contract therefore fails the first test or deployment that starts the host, not the first caller.
3. **Generated runtime.** The generator emits `IDomainQueryRuntime<TQuery, TResult>`:
   - the name, version, text and syntax;
   - the parameters (camelCase member names);
   - the result columns;
   - `ToParameters()`;
   - a typed `Materialize(QueryRow)` that converts through `DomainQueryValues`. Ids are recognised by `[StronglyTypedId]`, because the id interface comes from the same generator run.

   The module registers each query (`AddDomainQuery<TQuery, TResult>()`), and `IQueryService.ExecuteAsync(query)` runs it in-process.

## Consequences

- Some contract errors (unknown read model, missing output column) surface at host startup instead of compilation. A plain host start in a unit test catches them, as UT-QL-92 does.
- The analyzer stays small and fast, with no copy of the grammar.

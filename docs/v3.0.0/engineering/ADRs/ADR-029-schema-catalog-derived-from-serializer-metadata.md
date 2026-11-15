# ADR-029: The schema catalog is derived at startup from the serializer's source-generated metadata

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-11 |
| **Deciders** | v3 architecture review; WP-1.12 implementation |
| **Amends** | [doc 14](../Design/14-Query-Side-and-SFQL.md) §2.1 and §2.2; WBS WP-1.12 ("a generator extension that emits a JSON Schema") |

## Context

Doc 14 §2.1 says the source generator emits a JSON Schema 2020-12 document for every `[MessageType]`. The WBS repeats this for WP-1.12. Building the catalog showed three problems with emitting schemas from the SourceFlow generator:

- **The generator can't see the wire contract.** System.Text.Json decides member names, ignored members, converters, number handling and nullability. The `JsonSerializerContext` and its options control that, along with any custom converters. A Roslyn generator in `netstandard2.0` would have to re-implement those rules (ADR-004, DC-1), and every mismatch would produce a schema that doesn't describe what is actually stored or sent.
- **One contract would have two sources.** The serializer already has source-generated, AOT-safe metadata for every message, document and state type. That metadata is `JsonTypeInfo`, the same one SFQL's query catalog uses (doc 14 §5). A second description written by the SourceFlow generator could drift from it.
- **Decimals aren't strings.** Doc 14 §2.1 says decimals are serialised as strings with `format: decimal`. In fact System.Text.Json writes them as JSON numbers, and the PostgreSQL compiler reads them with `::numeric`, which is exact.

## Decision

1. **Schemas come from `JsonTypeInfo` at runtime.** `SourceFlow.Catalog.JsonSchemaGenerator` walks the type's `JsonTypeInfo` and those of its members. It covers object properties, enumerables, dictionaries, nullable value types and `JsonPropertyInfo.IsGetNullable`/`IsRequired` for `required`. It writes JSON Schema 2020-12. Member attributes become the `x-sourceflow-queryable`, `x-sourceflow-personal-data`, `x-sourceflow-renamed-from` and `x-sourceflow-tag` extensions.
   - It reads attributes through `JsonPropertyInfo.AttributeProvider`, which source generation populates, so no reflection over members is needed. The code stays AOT-compatible.
2. **Value formats follow the serializer:**
   - decimals are `{"type": "number", "format": "decimal"}`;
   - strongly typed ids follow their `[StronglyTypedId]` backing (uuid, string or int64);
     *Amended (WP-C.8, CR-30):* the backing is what the id's generated converter reports (`IStronglyTypedIdConverter`, from the type of `Value`, also emitted as the id's static `Backing`). The schema catalog and the SFQL catalog both read it (`IdBackings.Of`), so an Int64 id is `integer` in the schema and `Integer` in SFQL. SF0022 fails the build when `[StronglyTypedId(Backing = …)]` disagrees with `Value`.
   - enums are `integer` or `string`, decided by serializing a value through the enum's own `JsonTypeInfo`, so a configured string converter is respected.
3. **`SchemaCatalog.Build(RuntimeModel)` describes the whole service:**
   - every registered message type and version, including `CommandRejected<T>` replies;
   - every document projection's document (`readmodel`: projection name and version);
   - every process manager's state (`process-state`, version 1).
4. **Fingerprints exclude the service.** The fingerprint is the SHA-256 of the canonical schema text (keys sorted ordinally, no whitespace). `x-sourceflow` holds the type, version, kind and `upcastsTo`, but not the service or the owning aggregate. Several services that share a contract (an integration event, or a command sent to a remote owner) therefore register the same fingerprint.
   *Amended (WP-C.8, CR-17): what is fingerprinted.* The fingerprint covers the schema's **structure** only (`SchemaEntry.Structure`, from `JsonSchemaGenerator.Structure`). These annotations are left out, because they don't change what is on the wire:
   - the root `title`, the CLR type name, so renaming the type keeps the fingerprint (ADR-005);
   - `x-sourceflow-enum`, an enum's CLR name;
   - `x-sourceflow.upcastsTo`, which is set on a version when a later version and its upcaster are added (doc 03 §2.1).

   Everything else is fingerprinted: types, formats, properties, `required`, the `x-sourceflow` type, version and kind, and the queryable, personal-data, renamed-from and tag extensions. The canonical text that is stored and exported keeps the annotations.
   Fingerprints registered under the earlier rule are migrated at startup, once. When a registered fingerprint differs but the stored schema, fingerprinted under the current rule, has the same structure, the entry is replaced (`ISchemaStore.ReplaceAsync`) instead of being reported as a conflict.
5. **Registration and immutability follow doc 14 §2.2.** Host startup registers the catalog through the optional `ISchemaStore` port, implemented in memory and by PostgreSQL's `schemas` table. A different fingerprint for a registered `(type, version)` fails startup with `SchemaConflictException`. `SourceFlowOptions.SchemaRegistration` can set this to `Warn` or `Off`.
6. **The SourceFlow generator emits no schemas.** The GEN-41..46 cases planned for the generator are covered by UT-CAT-01..03 against the runtime generator.

## Consequences

- The catalog describes exactly what the serializer writes, including custom naming policies and converters, and costs one walk over the metadata at startup.
- Tools that need schemas without running the service get them through a host:
  - the CLI's `schema export` and `schema check`;
  - the lock file written by `SchemaCatalog.ToLockFile()`.
- Doc 14 §2.1 is amended: decimals are numbers with `format: decimal`.

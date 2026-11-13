# ADR-032: Blue/green read-model generations behind forward-only aliases

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-11 |
| **Deciders** | v3 architecture review; WP-2.7 implementation |
| **Amends** | [doc 06](../Design/06-Projections-and-Subscriptions.md) §6; [ADR-031](ADR-031-cli-runs-store-commands-globally-and-model-commands-in-the-service.md) decision 6 |

## Context

Doc 06 §6 describes blue/green rebuilds:

- A projection version bump, or `dotnet sourceflow projection rebuild`, builds a new **generation** from position zero.
- The old generation keeps serving reads until the new one has caught up.
- Reads then switch through an alias, and the old generation is dropped after a retention period.

Several details were left open:

- the generation naming;
- how a running service learns about a requested rebuild;
- what happens to nodes still running the old code during a rolling deploy;
- how relational (EF Core) read models take part;
- what "replay has no side effects" means for handlers.

## Decision

1. **Generation ids.**
   - The first build of version *v* is `{name}@{v}`, as before, so existing collections and checkpoints keep their names.
   - A rebuild of the same version is `{name}@{v}.{n}`, with *n* starting at 2.
   - Generations are ordered by version, then by number (`ReadModelGeneration`).
   - Each generation has its own subscription, `projection:{generation}`, with its own checkpoints and leases.
2. **One alias row per projection name** (`collection_aliases`, extended by migration V004). The row holds:
   - `target`: the generation serving reads;
   - `previous`: the generation the last switch retired;
   - `building`: a rebuild that `projection rebuild` requested.

   Every change is a compare-and-set through the `ICollectionAliases` storage port (SCK-73..75), so concurrent nodes switch an alias exactly once.
3. **What each node runs.** Each node runs at most two generations of each async projection: the one serving reads, if it is this code's version, and one being built. The build target is one of these:
   - the requested rebuild;
   - this code's first generation, when the alias points at an older version;
   - this code's first generation, before any alias exists.

   A node whose version is older than the alias target runs nothing for that projection. In a rolling deploy, old nodes keep the old generation current until the switch, then retire it within one poll interval.
4. **Switching forward only.** A node points the alias at the generation it is building once every partition is within `SwitchLagThreshold` events of the log (default 100). The check runs on idle passes and on each alias refresh.
   - Reads resolve the alias on every query. The aliases are cached for one poll interval, and a switch made by this node takes effect at once.
   - This covers SFQL, domain queries, `at_least` consistency waits, `IReadModelResolver` and the test host.
   - `projection switch` can point the alias back at a retained generation (a rollback). Nodes on a newer version then build again.
5. **Retention.** Once `RetainPreviousFor` has passed (default 7 days, checked once per `RetentionInterval`), the node drops the retired generation's data, then clears `previous`:
   - documents through `IDocumentAdmin`;
   - an EF Core generation's schema through `IProjectionGenerationStore`.

   `projection drop` does the same on demand. It refuses the serving generation and the one being built.
6. **EF Core opts in to a schema per generation.**
   - Opt in with `UseEntityFrameworkProjections<T>(configure, (options, schema) => …, generations)`. The context implements `IGenerationSchemaContext` and maps `HasDefaultSchema(Schema)`. Models are cached per schema.
   - A generation's schema and tables are created from the EF model on first use.
   - Reads resolve the generation with `IReadModelResolver` and open a context with `CreateForGeneration`.
   - Without the opt-in, an EF projection keeps one shared schema and does not take part in blue/green switching.
7. **One version of a projection per service.** Two registered projections with the same name fail at startup. A host that carries a new version of a library's projection leaves the old one out with `Projections(p => p.Remove<T>())`.
8. **Rebuilds never re-run side effects.** Process managers and event handlers have their own subscriptions, which no rebuild touches. The `IncludeInReplay` switch in doc 06 §6 is dropped: re-running a handler is a different operation (reset its subscription), not part of a rebuild.

## Consequences

- A version bump and a rebuild without a code change behave the same way. Readers see the old generation until the new one is close to the head. There is never an empty or half-built read model, except during the lag window allowed by `SwitchLagThreshold`.
- Inline projections are not rebuilt blue/green; they always write their first generation. To rebuild one, make it async for the rebuild.
- During the overlap, an old-version node reads the new generation's documents with its old document type. Additive document changes are safe; incompatible ones need the old nodes gone before the switch (set `SwitchLagThreshold` to 0 and finish the rollout first).
- Each alias refresh is one small query per poll interval per node. Lag checks read at most `SwitchLagThreshold + 1` log rows per partition, and only while a generation is being built.

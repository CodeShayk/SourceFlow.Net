# ADR-031: The CLI runs store commands as a global tool and model commands inside the service

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-11 |
| **Deciders** | v3 architecture review; WP-4.1 implementation |
| **Amends** | WBS WP-4.1; [doc 06](../06-Projections-and-Subscriptions.md) §6 (`projection rebuild`) |

## Context

`dotnet sourceflow` has two kinds of command:

- **Store commands** need only the database. These are `db migrate|script`, `dlq list|show|replay|discard|purge` and `projection skip|repartition|rebuild|drop`.
- **Model commands** need the service's model. This means its modules, JSON contracts, routing and projections. These are `db index sync`, `query`, `schema check|export` and `topology export|validate`.

A global tool can't see a service's model without loading the service's assembly by reflection. That would mean finding its modules, guessing its routing (which lives in `Program.cs`, not in modules), and matching its dependency versions. It would be fragile, and it would bypass the source-generated, AOT-safe model (ADR-004).

## Decision

1. **One command tree, two hosts.** `SourceFlow.Tool` holds the commands in System.CommandLine (`SourceFlowCli`).
2. **The global tool** (`dotnet sourceflow …`) runs store commands against PostgreSQL. It takes `--connection` (or `SOURCEFLOW_CONNECTION`), `--schema` and, for row-level security, `--system-connection`. Model commands explain how to run them inside the service.
3. **Inside a service**, `Program.cs` calls `if (await SourceFlowCli.TryRunAsync(args, app.Services) is { } code) return code;` before `app.Run()`. Every command then works with the service's own storage, model, routing and query service: `dotnet run --project <service> -- sourceflow schema check --baseline schemas.lock.json`. The Bank sample does this.
4. **Without a transport, the global tool replays dead letters through the outbox.** It enqueues the original envelope, with the same message id, so the inbox still protects. The running service's relay then sends it.
5. **Projection admin needs the leases.** `projection skip`, `repartition` and `rebuild` take the partitions' leases as `sourceflow-cli`. They refuse while a running node holds them, so subscriptions must be stopped first; `skip` runs on the node that holds the lease.
6. **`projection rebuild <name@version>` is blue/green** ([ADR-032](ADR-032-blue-green-read-model-generations-behind-forward-only-aliases.md)). It requests the next generation, and the running service builds it and switches reads once it has caught up. With `--in-place`, it instead drops the generation's documents in every tenant (`IDocumentAdmin`) and resets its checkpoints; the service then replays it from the start on its next run. `projection generations` lists the aliases, `projection switch` rolls reads back to a retained generation, and `projection drop` refuses a generation that is serving or being built.

## Consequences

- CI can check schemas with the service's own build, and nothing is loaded by reflection.
- Operators can run store commands without deploying anything.
- An in-place rebuild serves an empty, growing read model while it catches up; the default blue/green rebuild does not.

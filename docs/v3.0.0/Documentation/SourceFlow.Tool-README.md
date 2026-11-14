# SourceFlow.Tool (`dotnet sourceflow`)

`SourceFlow.Tool` is the SourceFlow command line. It migrates and scripts the database schema, manages dead letters,
projections, read-model generations and event partitions, runs SFQL queries, exports and checks schemas, topologies
and service manifests, operates DSL workflow instances, migrates SourceFlow v2 databases, generates dashboards and alert
rules, and runs SourceFlow Studio locally. The same command tree runs in two places: as a global tool against a
database, and inside a service, where it uses the service's own model and storage
([ADR-031](../ADRs/ADR-031-cli-runs-store-commands-globally-and-model-commands-in-the-service.md)).

| | |
|---|---|
| Project | `src/Tooling/SourceFlow.Tool` (`PackAsTool`, command name `sourceflow`) |
| Target | `net10.0` only |
| Parser | System.CommandLine |
| Library entry points | `SourceFlow.Tool.SourceFlowCli.RunAsync`, `SourceFlowCli.TryRunAsync` |
| Global-tool storage | PostgreSQL (the v2 migration also reads SQL Server) |

## Contents

- [Installation](#installation)
- [Global or in the service](#global-or-in-the-service)
- [Command reference](#command-reference)
- [db: schema, indexes and partitions](#db-schema-indexes-and-partitions)
- [dlq: dead letters](#dlq-dead-letters)
- [projection: subscriptions and read-model generations](#projection-subscriptions-and-read-model-generations)
- [query](#query)
- [schema](#schema)
- [topology](#topology)
- [manifest](#manifest)
- [workflow](#workflow)
- [studio](#studio)
- [observability](#observability)
- [migrate v2](#migrate-v2)
- [Environment variables and exit codes](#environment-variables-and-exit-codes)
- [Troubleshooting](#troubleshooting)
- [Links](#links)

## Installation

The tool needs the .NET 10 runtime. `<command> --help` lists every option of a command.

### As a .NET tool

`SourceFlow.Tool` is packed as a .NET tool (command name `sourceflow`) and published with the other 3.0 packages; the
release build embeds the Studio web app and WebAssembly core in it, so `sourceflow studio` needs nothing else:

```bash
dotnet tool install --global SourceFlow.Tool    # once 3.0 packages are published; or --version 3.0.0-alpha.N from GitHub Packages
dotnet sourceflow --help
```

The examples in this document use `dotnet sourceflow`. Until a package is available to you, run the tool from source or
inside a service, as below.

### From source

```bash
dotnet run --project src/Tooling/SourceFlow.Tool -- <command> [options]
# or, after a build:
dotnet src/Tooling/SourceFlow.Tool/bin/Release/net10.0/SourceFlow.Tool.dll <command> [options]
```

CI uses the second form for the manifest drift checks. The `studio` command serves the Studio web app and WebAssembly
core embedded in the tool when they were built first (`npm run build` and `npm run build:wasm` in `studio/`); otherwise
pass `--assets` and `--framework`.

### Inside a service

Reference `SourceFlow.Tool` from the service's host project and add one line to `Program.cs`, after the service
provider is built and before the host runs:

```csharp
WebApplication app = builder.Build();

// `dotnet run -- sourceflow <command>` runs the SourceFlow CLI with this service's model and storage (ADR-031).
if (await SourceFlow.Tool.SourceFlowCli.TryRunAsync(args, app.Services) is { } exitCode)
{
    return exitCode;
}
```

`TryRunAsync` runs the CLI only when the first argument is `sourceflow`, and returns `null` otherwise. The two services
of the [TravelBooking sample](../../../samples/TravelBooking/README.md) do this. A generic host
(`Host.CreateApplicationBuilder`) works the same way with `host.Services`.

```bash
# In the demo's container (docker compose up -d --build at the repository root)
docker compose exec bookings sh -c 'dotnet Bookings.Api.dll sourceflow workflow list --name trip-booking --active'

# From the source, against the demo's database
export ConnectionStrings__SourceFlow="Host=localhost;Port=55432;Username=sourceflow;Password=sourceflow;Database=travel"
dotnet run --project samples/TravelBooking/Bookings.Api -- sourceflow query 'from readmodel "booking-summary" | take 5'
```

Configuration arguments for the host (`--ConnectionStrings:SourceFlow=...`, `--Transport:Kind=gcp`) go before
`sourceflow`.

## Global or in the service

| | Global tool | Inside the service |
|---|---|---|
| How | `dotnet sourceflow …` | `dotnet run --project <service> -- sourceflow …` |
| Storage | PostgreSQL from `--connection` or `SOURCEFLOW_CONNECTION`, `--schema` (default `sourceflow`), `--system-connection` for row-level security | The service's configured storage (any adapter); the connection options are ignored |
| Model | none | The service's modules, routing, projections, catalog and query service |
| Store commands (`db migrate`, `db script`, `db partitions`, `dlq`, `projection`, `workflow`) | yes | yes |
| Model commands (`db index sync`, `query`, `schema`, `topology`, `manifest export` without `--project`) | no: they print how to run them inside the service | yes |
| Design-time commands (`manifest export --project`, `studio`, `observability export`) | yes | yes |

Why the split: a global tool cannot see a service's model without loading its assembly by reflection and guessing its
routing, which would bypass the source-generated, AOT-safe model. Running inside the service uses exactly the build and
configuration you deploy.

A few behaviours differ:

- **Dead-letter replay.** Inside the service it goes through `IDeadLetterManager` and the service's transport. The
  global tool has no transport, so it enqueues the original envelope (same message id) in the outbox, and the running
  service's relay sends it. Pass `--outbox-partitions` if the service changed `OutboxPartitions` from 16.
- **Workflow actions.** Inside the service they are queued in the service's own outbox. The global tool needs
  `--endpoint` (the owning service's endpoint) and `--outbox-partitions`.
- **`projection repartition`** checks inside the service that the projection is a document projection with the automatic
  version guard; from the global tool you must confirm with `--idempotent`.

## Command reference

`[S]` = needs the service's model (run inside the service). Every command that touches the store accepts
`--connection`, `--schema` and `--system-connection` (shown once below as *store options*).

| Command | Purpose | Key options |
|---------|---------|-------------|
| `db migrate` | Apply pending schema migrations | *store options*, `--rls`, `--partition-events` |
| `db script` | Print the migration SQL for review, or the runtime role's grants | `--schema`, `--rls`, `--partition-events`, `--from <version>`, `--grants <role>` |
| `db index sync` [S] | Compare, or with `--apply` create and drop, SFQL expression indexes for `[Queryable]` fields | `--apply` |
| `db partitions list` | List monthly events partitions, oldest first | *store options* |
| `db partitions ensure` | Create the monthly partitions ahead now | *store options* |
| `db partitions detach <name>` | Detach an old partition whose streams are all archived or deleted | *store options* |
| `dlq list` | List dead letters, newest first | `--consumer`, `--reason`, `--type`, `--status`, `--limit` |
| `dlq show <id>` | Show a dead letter's envelope, destination and error | `--payload` |
| `dlq replay <id>` | Re-send a parked message; its id is kept so the inbox still protects | `--note`, `--outbox-partitions` |
| `dlq discard <id>` | Discard a parked message (audited) | `--reason` (required) |
| `dlq purge` | Delete replayed and discarded dead letters resolved before a time; parked ones are kept | `--before` (required) |
| `projection skip <subscription>` | Skip the next event of a stopped partition (audited) | `--partition`, `--partitions`, `--reason` (required) |
| `projection repartition <subscription>` | Change a subscription's partition count | `--from`, `--to` (required), `--idempotent` |
| `projection rebuild <name@version>` | Rebuild a projection blue/green, or in place | `--partitions`, `--in-place` |
| `projection drop <name@version>` | Delete a retired read-model generation's documents | |
| `projection generations` | List read-model aliases: serving, building, previous | `--format text\|json` |
| `projection switch <projection> <generation>` | Point reads at another generation (rollback) | `--reason` (required) |
| `query <query>` [S] | Run an SFQL or SQL-syntax query against the service's data | `--sql`, `--param`, `--validate`, `--explain`, `--format`, `--page-size` |
| `schema export` [S] | Export a schema lock file, JSON Schemas or an AsyncAPI 3.0 document | `--format lock\|jsonschema\|asyncapi`, `--out`, `--version` |
| `schema check` [S] | Check schemas against a baseline lock file; exit 1 on a breaking change | `--baseline` (required) |
| `topology export` [S] | Write the messaging topology (logical and physical) for infrastructure as code | `--format json\|text`, `--output` |
| `topology validate` [S] | Check that the broker has the endpoint, topics and subscriptions | |
| `manifest export` | Write the service manifest for SourceFlow Studio; `--check` fails on drift | `--project`, `--output`, `-c`, `--no-build`, `--check`, `-- <app args>` |
| `workflow list` | List workflow instances, newest first | `--tenant`, `--name`, `--version`, `--status`, `--active`, `--limit` |
| `workflow show <workflow> <instance>` | Show an instance: status, step, attempt, failure | `--tenant` |
| `workflow resume <workflow> <instance>` | Resume an instance in manual intervention | `--step`, `--note`, `--tenant`, `--endpoint`, `--outbox-partitions` |
| `workflow retry-step <workflow> <instance>` | Re-send the current step or compensation as a new attempt | `--note`, `--tenant`, `--endpoint`, `--outbox-partitions` |
| `workflow abort <workflow> <instance>` | Abort an unfinished instance, optionally compensating first | `--reason` (required), `--compensate`, `--tenant`, `--endpoint`, `--outbox-partitions` |
| `studio` | Run SourceFlow Studio on this machine (loopback only) | `--workspace`, `--port`, `--read-only`, `--assets`, `--framework`, `--open` |
| `observability export` | Write the Grafana dashboard and Prometheus alert rules | `--output` (required) |
| `migrate v2 plan` | Count v2 entities and commands and check every v2 table has an import | v2 options |
| `migrate v2 scaffold` | Generate a v2 row type and an `IV2Import` stub per table | v2 options, `--table`, `--namespace`, `--output` |
| `migrate v2 import` | Import every v2 entity as genesis events (resumable) | v2 options, `--batch`, `--limit`, `--tenant` |
| `migrate v2 archive` | Copy the v2 command table to a read-only archive | v2 options |
| `migrate v2 verify` | Compare rebuilt v3 state with each v2 row; exit 1 on a mismatch | v2 options, `--tenant`, `--batch`, `--max` |

*Store options:*

| Option | Default | Meaning |
|--------|---------|---------|
| `--connection` | `SOURCEFLOW_CONNECTION` | PostgreSQL connection string. Not needed inside a service. |
| `--schema` | `sourceflow` | PostgreSQL schema. |
| `--system-connection` | none | A `BYPASSRLS` connection for stores with row-level security (ADR-022). When given, the tool opens the store with `TenantIsolation.RowLevelSecurity`. |

There is no dedicated command to browse streams, the outbox or the inbox. Use `query` for events and streams (for
example `from stream @s` or `from events | where sfcorrelationid = @cid`), `dlq` for messages that failed, the
`sourceflow.outbox.*` gauges and the `sourceflow.outbox` health check for the outbox backlog
([Observability and performance](Observability-and-Performance.md)).

## db: schema, indexes and partitions

### `db migrate`

```bash
dotnet sourceflow db migrate --connection "Host=db;Username=owner;Password=...;Database=bank" --schema bank
```

Applies the pending PostgreSQL migrations under an advisory lock, records them in a checksummed `schema_history`, and
prints `Applied V001, V002, ...` or `The schema is up to date.`

| Option | Meaning |
|--------|---------|
| `--rls` | Install row-level security tenant isolation (ADR-022). |
| `--partition-events` | Partition the `events` table by month, keeping its rows. An existing table is converted in place once. |

Inside a service the store's own data source and options are used, and `--partition-events` is implied when the
service sets `PostgreSqlStorageOptions.PartitionEvents`. Services can also migrate at startup with `AutoMigrate`;
production deployments usually run `db migrate` as a separate step with an owner role. SQL Server stores migrate through
`UseSqlServer` (see the [SQL Server README](SourceFlow.Storage.SqlServer-README.md)), not through this command.

### `db script`

```bash
dotnet sourceflow db script --schema bank > migrations.sql                 # every migration, for DBA review
dotnet sourceflow db script --schema bank --from 3 > upgrade.sql           # only migrations after V003
dotnet sourceflow db script --schema bank --grants bank_runtime > grants.sql
```

Prints SQL without connecting to a database. `--grants <role>` prints least-privilege grants for the runtime role
instead (the events table is append-only for it); run them as the owner after the migrations.

### `db index sync` [S]

```bash
dotnet run --project samples/TravelBooking/Bookings.Api -- sourceflow db index sync           # show the plan
dotnet run --project samples/TravelBooking/Bookings.Api -- sourceflow db index sync --apply   # create and drop
```

Compares the expression indexes SFQL relies on (one per `[Queryable]` event field and read-model field) with the
service's catalog. Each line is `create`/`created <index> <source>` or `drop`/`dropped <index>`, followed by
`N unchanged`. With `--apply`, indexes are created `CONCURRENTLY` (except on partitioned tables). PostgreSQL storage
only.

### `db partitions`

For stores with `PartitionEvents`:

```bash
dotnet sourceflow db partitions list   --connection "$PG" --schema bank
dotnet sourceflow db partitions ensure --connection "$PG" --schema bank
dotnet sourceflow db partitions detach events_2026_01 --connection "$PG" --schema bank
```

`list` prints each partition's name, range and whether it is attached. `ensure` creates the partitions ahead now (the
service's maintenance job also does this every `PartitionMaintenanceInterval`). `detach` detaches an ended partition
whose streams are all archived or deleted; the table keeps its rows, so archive it (for example with `pg_dump -t`) and
drop it when ready.

## dlq: dead letters

Messages that fail permanently, or exhaust their retries, are parked in the dead-letter store with their full envelope.

```bash
dotnet sourceflow dlq list --connection "$PG" --schema bank --status parked --limit 20
dotnet sourceflow dlq show 0193b0c2-... --connection "$PG" --schema bank
dotnet sourceflow dlq replay 0193b0c2-... --connection "$PG" --schema bank --note "fixed the template"
dotnet sourceflow dlq discard 0193b0c2-... --connection "$PG" --schema bank --reason "duplicate customer, merged by hand"
dotnet sourceflow dlq purge --before 2026-10-01T00:00:00Z --connection "$PG" --schema bank
```

| Command | Options |
|---------|---------|
| `list` | `--consumer`, `--reason`, `--type` (message type), `--status` (`parked` default, `replayed`, `discarded`, `all`), `--limit` (default 100) |
| `show <id>` | `--payload`: include the raw payload. It may contain personal data. |
| `replay <id>` | `--note` (audit note), `--outbox-partitions` (default 16, global tool only) |
| `discard <id>` | `--reason` (required, audited) |
| `purge` | `--before` (required): deletes replayed and discarded dead letters resolved before this time |

Only parked messages can be replayed. A dead letter that came from a subscription has no destination; fix the cause and
`projection skip` or rebuild instead. Replays, discards and skips record who ran them (`by <user>@<machine>`, plus the
note or reason), so operator actions are not anonymous.

## projection: subscriptions and read-model generations

Subscriptions are named after what they run, for example `projection:account-summary@1`. Read-model generations are
named `name@version` or `name@version.n` (ADR-032).

```bash
# A Stop-policy projection is halted at a poison event: skip it (audited).
dotnet sourceflow projection skip projection:account-summary@1 --partition 0 --reason "malformed legacy event" \
  --connection "$PG" --schema bank

# Rebuild blue/green: the service builds account-summary@1.2 beside @1 and switches reads when it has caught up.
dotnet sourceflow projection rebuild account-summary@1 --connection "$PG" --schema bank
dotnet sourceflow projection generations --connection "$PG" --schema bank

# Roll reads back to the previous generation while it is kept.
dotnet sourceflow projection switch account-summary account-summary@1 --reason "regression in 1.2" \
  --connection "$PG" --schema bank

# Delete a retired generation's documents.
dotnet sourceflow projection drop account-summary@1 --connection "$PG" --schema bank
```

| Command | Notes |
|---------|-------|
| `skip <subscription>` | `--partition` (default 0), `--partitions` (the subscription's count, default 1), `--reason` (required). Records the skipped event as a discarded dead letter and moves the checkpoint past it. Takes the partition's lease, so a node holding it must run the skip itself (inside the service) or be stopped. |
| `repartition <subscription>` | `--from` and `--to` (powers of two, required). Every new partition starts at the minimum old checkpoint, so events are re-applied: the projection must be version-guarded. Takes every partition's lease: stop the subscription first. Then set `SubscriptionPartitions` to the new count and restart. |
| `rebuild <name@version>` | Blue/green by default: requests the next generation of the serving version; the running service builds it from the start and switches reads at `SwitchLagThreshold`. `--in-place` instead drops the generation's documents and resets its checkpoints (stop other nodes' subscriptions first); reads see an empty, growing read model meanwhile. |
| `drop <name@version>` | Refuses a generation that serves reads or is being built. |
| `generations` | `--format text` (default) or `json`: projection, serving, building, previous and switch time. |
| `switch <projection> <generation>` | `--reason` (required). Compare-and-set on the alias; nodes on an older version resume it, newer versions build again. |

## query

```bash
dotnet run --project samples/TravelBooking/Bookings.Api -- sourceflow query \
  'from events of "bookings.booking-requested" | where data.nights >= @min and time >= ago(7d) | project stream, time, nights = data.nights | order by time desc | take 20' \
  --param min=7

dotnet run --project samples/TravelBooking/Bookings.Api -- sourceflow query --sql \
  'SELECT id, hotelId, nights FROM readmodel."booking-summary" WHERE status = @status ORDER BY nights' \
  --param status=3 --format csv > declined.csv

dotnet run --project samples/TravelBooking/Bookings.Api -- sourceflow query --explain \
  'from readmodel "booking-summary" | where status = 1 and nights > 14'
```

| Option | Default | Meaning |
|--------|---------|---------|
| `--sql` | off | The query is in the SQL syntax instead of the pipeline syntax. |
| `--param name=value` | none | A parameter (repeatable). A leading `@` on the name is ignored. Values are converted to the type the binder expects. |
| `--validate` | off | Only parse, bind and policy-check. Prints diagnostics and the result columns; exit 1 when not valid. |
| `--explain` | off | Print the engine, columns, sources with their consistency, parameter count, query hash and the parameterised statement. Nothing runs. |
| `--format` | `table` | `table`, `json` (one object per line) or `csv` (formula cells neutralised). |
| `--page-size` | `100` | Rows per page; the command reads every page. |

Diagnostics go to standard error as `SFQ0401 (1:6): ...`, and the exit code is 1. The CLI runs with the service's own
identity, which holds every query permission, including personal data. The language is described in
[Architecture/08 Query side and SFQL](Architecture/08-Query-Side-and-SFQL.md).

## schema

```bash
# Write the lock file once a version is published, and commit it.
dotnet run --project src/Accounts.Host -- sourceflow schema export --format lock --out schemas.lock.json

# In CI: fail the build on a breaking change.
dotnet run --project src/Accounts.Host -- sourceflow schema check --baseline schemas.lock.json

# An AsyncAPI 3.0 document for an event catalog.
dotnet run --project src/Accounts.Host -- sourceflow schema export --format asyncapi --version 2.1.0 --out asyncapi.json
```

`export --format`: `lock` (default), `jsonschema` (an array of JSON Schema 2020-12 documents) or `asyncapi`. `--out`
writes to a file; `--version` is the AsyncAPI document version (default `1.0.0`).

`check --baseline <file>` compares the service's catalog with the published lock file and prints one line per finding
(`SFC04 breaking bank.account.deposited v1.amount: The field's type changed ...`). Breaking findings go to standard error, and any breaking
finding makes the exit code 1. The rules SFC01..SFC10 are listed in
[Architecture/08](Architecture/08-Query-Side-and-SFQL.md#schema-catalog) and ADR-029.

## topology

```bash
dotnet run --project src/Accounts.Host -- sourceflow topology export --format json --output topology.json
dotnet run --project src/Accounts.Host -- sourceflow topology export --format text
dotnet run --project src/Accounts.Host -- sourceflow topology validate
```

`export` writes the service's endpoint, published and subscribed topics, filters, sent commands and, when the transport
describes them, the physical broker resources. The JSON is what the Terraform and Bicep modules in
[`deploy/topology`](../../../deploy/topology/README.md) consume. `validate` checks the broker in
`ProvisioningMode.Validate` and prints `The topology is in place.` or `Topology mismatch: ...` (exit 1). See
[Architecture/07 Cloud transports](Architecture/07-Cloud-Transports.md).

## manifest

A service manifest (`sourceflow.manifest.json`) describes a service for SourceFlow Studio: its endpoint, messages,
aggregates, process managers, projections, publications, subscriptions, command routes, workflows and schemas. It is the
generator's static manifest merged with the routing configured at startup.

```bash
# Design time: build the project and run its configuration without starting it.
dotnet sourceflow manifest export --project samples/TravelBooking/Bookings.Api \
  --output samples/TravelBooking/Bookings/sourceflow.manifest.json -- "--ConnectionStrings:SourceFlow=Host=localhost;Port=1"

# CI: fail when the committed manifest is out of date.
dotnet sourceflow manifest export --project samples/TravelBooking/Hotels.Api -c Release --no-build --check \
  --output samples/TravelBooking/Hotels/sourceflow.manifest.json -- "--ConnectionStrings:SourceFlow=Host=localhost;Port=1"
```

| Option | Default | Meaning |
|--------|---------|---------|
| `--project` | none | The service project (`.csproj`, or a folder with exactly one). The project is built, then run with `SOURCEFLOW_DESIGN_TIME=1`: the host is built but never started, so nothing connects to storage or a broker, and the manifest is written to a temporary file. |
| `--output` | `<project folder>/artifacts/sourceflow.manifest.json` with `--project`; standard output inside a service | The manifest file. |
| `-c`, `--configuration` | `Debug` | Build configuration. |
| `--no-build` | off | Do not build the project first. |
| `--check` | off | Compare with the file at `--output` instead of writing it. On drift, print the first differing line and exit 1. |
| `-- <app args>` | none | Arguments for the application, for example a placeholder connection string the configuration requires. |

The design-time run has a 5-minute timeout. The project must build a generic host (`Host.CreateApplicationBuilder` or
`WebApplication.CreateBuilder`/`CreateSlimBuilder`) that calls `AddSourceFlow`. Without `--project`, the command must
run inside the service and writes the running service's manifest.

## workflow

Operations on DSL workflow instances. Instances are read from the `workflow-instances` read model. Actions are
`WorkflowControl` messages queued in the owning service's outbox and delivered to its endpoint, so they run in the
instance's own transaction.

```bash
# Inside the TravelBooking demo's Bookings service, which owns the trip-booking workflow
docker compose exec bookings sh -c 'dotnet Bookings.Api.dll sourceflow workflow list --name trip-booking --status ManualIntervention'

docker compose exec bookings sh -c 'dotnet Bookings.Api.dll sourceflow workflow show trip-booking@1 0193a7a0-0000-7000-8000-0000000000b1'

docker compose exec bookings sh -c "dotnet Bookings.Api.dll sourceflow workflow retry-step trip-booking@1 0193a7a0-0000-7000-8000-0000000000b1 --note 'hotel restored the hold'"

# The same from the global tool, against the demo's database
PG="Host=localhost;Port=55432;Username=sourceflow;Password=sourceflow;Database=travel"
dotnet sourceflow workflow abort trip-booking@1 0193a7a0-0000-7000-8000-0000000000b1 \
  --reason "rebooked by phone" \
  --endpoint bookings --connection "$PG" --schema bookings
```

| Command | Notes |
|---------|-------|
| `list` | Filters: `--name` (without version), `--version`, `--status` (`Running`, `Compensating`, `ManualIntervention`, `Completed`, `Compensated`, `Aborted`), `--active` (unfinished only), `--limit` (default 100), `--tenant` (default the default tenant). With `--name` and `--version` it also says whether that version still has active instances in any tenant, and so whether it can be removed. |
| `show <workflow> <instance>` | `<workflow>` is `name@version`; `<instance>` is the correlation value (for example the booking id). |
| `resume` | Only for an instance in `ManualIntervention`: the compensation continues, or the step runs again. `--step` resumes at another step. |
| `retry-step` | Only for `ManualIntervention`: re-sends the current step or compensation as a new attempt. |
| `abort` | For any unfinished instance. `--reason` is required; `--compensate` rolls back completed steps first, and the instance ends `Aborted`. |

From the global tool, `resume`, `retry-step` and `abort` need `--endpoint` (the owning service's endpoint) and, if the
service changed it, `--outbox-partitions`. The owner's relay must be running to deliver the action. The alert
`SourceFlowWorkflowManualIntervention` tells you when an instance needs one of these commands. See
[Architecture/09 Process managers and workflows](Architecture/09-Process-Managers-and-Workflows.md) and the
[workflows guide](../guides/workflows.md).

## studio

```bash
dotnet sourceflow studio --workspace samples/TravelBooking --open
dotnet run --project src/Tooling/SourceFlow.Tool -- studio --workspace samples/TravelBooking --port 0 --read-only
```

| Option | Default | Meaning |
|--------|---------|---------|
| `--workspace` | `sourceflow.domain.yaml` | A `sourceflow.domain.yaml`, or the folder that holds one. |
| `--port` | `5180` | The loopback port; `0` picks a free one. |
| `--read-only` | off | Refuse every write: workflow and layout files, and manifest exports. |
| `--assets` | embedded | Serve the Studio app from this folder (`studio/app/dist`). |
| `--framework` | embedded | Serve the WebAssembly core from this `_framework` folder. |
| `--open` | off | Open Studio in the default browser. |

Studio listens on `http://127.0.0.1:<port>/` only. When it re-exports a service's manifest it uses the same design-time
export as `manifest export --project`. See the [SourceFlow.Studio README](SourceFlow.Studio-README.md).

## observability

```bash
dotnet sourceflow observability export --output deploy/observability
```

Writes `sourceflow-dashboard.json` (Grafana) and `sourceflow-alerts.yaml` (Prometheus) generated from the instrument
catalogue, so they cannot drift from the code. The repository's copies are in `deploy/observability`, and a test fails
when they differ from the generator. See [Observability and performance](Observability-and-Performance.md#dashboards-and-alerts).

## migrate v2

Moves a SourceFlow v2 database to v3: v2 entity rows become genesis events of v3 aggregates. The full procedure is in
the [migration guide](../guides/migrating-from-v2.md).

```bash
export SOURCEFLOW_V2_CONNECTION="Host=old-db;Database=bank;Username=...;Password=..."
dotnet sourceflow migrate v2 plan                                   # what is there, and is every table covered?
dotnet sourceflow migrate v2 scaffold --output V2Imports            # row types and IV2Import stubs
dotnet run --project src/Bank.Host -- sourceflow migrate v2 import --batch 1000 --limit 50000
dotnet run --project src/Bank.Host -- sourceflow migrate v2 verify  # the cut-over gate
dotnet sourceflow migrate v2 archive                                # read-only copy of the v2 command table
```

*v2 options* (every `migrate v2` command):

| Option | Default | Meaning |
|--------|---------|---------|
| `--v2-connection` | `SOURCEFLOW_V2_CONNECTION` | The v2 database. Not needed inside a service that calls `sf.MigrateFromV2(...)`. |
| `--v2-provider` | `postgresql` | `postgresql` or `sqlserver`. |
| `--v2-schema` | the connection's | Schema of the v2 tables. |
| `--v2-command-table` | `CommandRecord` | The v2 command table name. |
| `--v2-archive-schema` | `sourceflow_v2_archive` | Schema of the archive copy (table `commands`). |

| Command | Options | Exit code |
|---------|---------|-----------|
| `plan` | | 1 when a v2 table has no import (add an `IV2Import`, or ignore tables that hold no entities) |
| `scaffold` | `--table` (repeatable; default every table without an import), `--namespace` (default `V2Migration`), `--output` (a directory; default standard output) | 0 |
| `import` | `--batch` (rows per query, default 1000), `--limit` (new streams in this run), `--tenant` | 0; re-run to continue after `--limit` |
| `archive` | | 1 when fewer rows were archived than the source holds |
| `verify` | `--tenant`, `--batch` (default 1000), `--max` (mismatches to print, default 50) | 1 on any mismatch |

`plan`, `scaffold` and `archive` run from the global tool. `import` and `verify` need the imports you wrote, so they run
inside the service that registers them with `sf.MigrateFromV2(...)`. Each stream is imported in its own session with
`ExpectedVersion.NoStream`, so an import can be stopped and re-run without repeating anything.

## Environment variables and exit codes

| Variable | Used by | Meaning |
|----------|---------|---------|
| `SOURCEFLOW_CONNECTION` | store commands | PostgreSQL connection string when `--connection` is not given. |
| `SOURCEFLOW_V2_CONNECTION` | `migrate v2` | The v2 database when `--v2-connection` is not given. |
| `SOURCEFLOW_DESIGN_TIME`, `SOURCEFLOW_MANIFEST_OUT` | `manifest export --project` | Set by the tool on the design-time run of your project; you do not set them. |
| `DOTNET_HOST_PATH` | `manifest export --project` | The `dotnet` executable used to build and run the project, when set. |

| Exit code | Meaning |
|-----------|---------|
| `0` | Success. |
| `1` | The command failed (the message is on standard error), or it found a problem: invalid query, breaking schema change, manifest drift, topology mismatch, v2 plan not ready or verification mismatch. |
| non-zero | Usage error (unknown command, missing required option or invalid value): System.CommandLine prints the error and the help. Scripts should treat any non-zero code as failure. |

## Troubleshooting

| Message | Cause | Fix |
|---------|-------|-----|
| `Pass --connection or set SOURCEFLOW_CONNECTION (or run inside the service).` | A store command from the global tool without a connection | Pass `--connection`, set the variable, or run it through the service. |
| `'query' needs the service's model: run it inside the service, for example 'dotnet run --project <service> -- sourceflow query'.` | A model command from the global tool | Add `TryRunAsync` to the service and run it there. |
| `A running node holds <subscription> partition <n>; stop the subscription first.` | `repartition`, `rebuild --in-place` or `skip` needs a lease a live node owns | Stop the subscription (or the nodes), or run `skip` inside the service that holds the lease. |
| `Repartitioning re-applies events; confirm the projection is version-guarded with --idempotent.` | Global-tool repartition without confirmation | Confirm the projection is idempotent, then pass `--idempotent`. |
| `<name> serves reads` / `is being built; only a retired generation can be dropped.` | `projection drop` on a live generation | Drop only the `previous` generation shown by `projection generations`. |
| `The dead letter is Replayed; only parked messages can be replayed.` | Replaying twice | Nothing to do; check `dlq show`. |
| `This dead letter came from a subscription and has no destination; ...` | Replay of a subscription failure | Fix the projection, then `projection skip` or rebuild. |
| `Pass --endpoint: the owning service's endpoint (or run inside the service).` | A workflow action from the global tool | Pass `--endpoint` or run it inside the owner. |
| `<workflow> <instance> is Running: 'retry-step' needs ManualIntervention.` | The instance is not waiting for an operator | Only `abort` works on a running instance. |
| `Manifest drift: ... differs from the regenerated manifest at line N.` | The committed manifest is out of date | Run `manifest export` without `--check` and commit the file. |
| `<project> exited without writing a manifest.` | The project does not build a generic host that calls `AddSourceFlow`, or it exited early | Make sure `Program.cs` reaches `Build()` with SourceFlow registered; pass required configuration after `--`. |
| `The Studio host could not listen on port 5180: ...` | The port is taken | Pass `--port 0`. |
| `PostgresException ... permission denied` on `db migrate` | The connection uses the runtime role | Migrate with the owner role; give the runtime role the output of `db script --grants`. |

## Links

- [SourceFlow.AspNetCore README](SourceFlow.AspNetCore-README.md): the CLI hook in a web host.
- [SourceFlow.Storage.PostgreSQL README](SourceFlow.Storage.PostgreSQL-README.md): schema, partitions, row-level security.
- [Architecture/06 Projections and read models](Architecture/06-Projections-and-Read-Models.md): subscriptions, generations, rebuilds.
- [Architecture/08 Query side and SFQL](Architecture/08-Query-Side-and-SFQL.md).
- [Observability and performance](Observability-and-Performance.md).
- [SourceFlow.Studio README](SourceFlow.Studio-README.md).
- Guides: [migrating from v2](../guides/migrating-from-v2.md), [workflows](../guides/workflows.md).
- ADRs: [ADR-031](../ADRs/ADR-031-cli-runs-store-commands-globally-and-model-commands-in-the-service.md),
  [ADR-032](../ADRs/ADR-032-blue-green-read-model-generations-behind-forward-only-aliases.md),
  [ADR-029](../ADRs/ADR-029-schema-catalog-derived-from-serializer-metadata.md).
- Design: [doc 06 §6](../Design/06-Projections-and-Subscriptions.md), [doc 12 §3](../Design/12-Migration-Delivery-and-Traceability.md),
  [doc 15 §3 and §7](../Design/15-Workflow-DSL-and-Studio.md).

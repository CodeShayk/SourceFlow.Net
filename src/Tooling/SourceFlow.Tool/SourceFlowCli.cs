using System.CommandLine;
using ParseResult = System.CommandLine.ParseResult;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Catalog;
using SourceFlow.Messaging;
using SourceFlow.Query;
using SourceFlow.Runtime;
using SourceFlow.Storage;
using SourceFlow.Storage.PostgreSQL;
using SourceFlow.Storage.PostgreSQL.Query;
using SourceFlow.Transport;

namespace SourceFlow.Tool;

/// <summary>
/// The <c>dotnet sourceflow</c> command line (WP-4.1, ADR-031). Store-level commands (<c>db migrate|script</c>,
/// <c>dlq …</c>, <c>projection skip|repartition|rebuild|drop</c>) run from the global tool against a PostgreSQL connection;
/// inside a service (<c>dotnet run -- sourceflow …</c>, see <see cref="RunAsync"/> with services) they use the service's
/// storage, and the commands that need its model (<c>db index sync</c>, <c>query</c>, <c>schema</c>, <c>topology</c>) work too.
/// Exit codes: 0 success, 1 failure or a breaking finding, 2 usage error.
/// </summary>
public static class SourceFlowCli
{
    /// <summary>Runs a command line.</summary>
    /// <param name="args">The arguments (without the leading <c>sourceflow</c>).</param>
    /// <param name="services">The service's provider (after <c>AddSourceFlow</c>), or null for the global tool.</param>
    /// <param name="output">Standard output (default the console).</param>
    /// <param name="error">Standard error (default the console).</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    public static Task<int> RunAsync(string[] args, IServiceProvider? services = null, TextWriter? output = null, TextWriter? error = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        var cli = new Cli(services, output ?? Console.Out, error ?? Console.Error);
        return cli.Root().Parse(args).InvokeAsync(new InvocationConfiguration { Output = cli.Out, Error = cli.Err }, cancellationToken);
    }

    /// <summary>
    /// Runs the CLI inside a service when its arguments start with <c>sourceflow</c>; returns null otherwise, so
    /// <c>Program.cs</c> can do <c>if (await SourceFlowCli.TryRunAsync(args, app.Services) is { } code) return code;</c>.
    /// </summary>
    public static async Task<int?> TryRunAsync(string[] args, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Length > 0 && args[0] == "sourceflow" ? await RunAsync(args[1..], services, cancellationToken: cancellationToken).ConfigureAwait(false) : null;
    }
}

internal sealed partial class Cli(IServiceProvider? services, TextWriter output, TextWriter error)
{
    private const string CliOwner = "sourceflow-cli";

    private readonly Option<string?> _connection = new("--connection") { Description = "PostgreSQL connection string (or SOURCEFLOW_CONNECTION). Not needed inside a service." };
    private readonly Option<string> _schema = new("--schema") { Description = "PostgreSQL schema.", DefaultValueFactory = _ => "sourceflow" };
    private readonly Option<string?> _systemConnection = new("--system-connection") { Description = "BYPASSRLS connection for row-level security schemas." };

    public TextWriter Out { get; } = output;

    public TextWriter Err { get; } = error;

    public RootCommand Root()
    {
        var root = new RootCommand("SourceFlow.Net operations: database, dead letters, projections, topology, queries, schemas, v2 migration and service manifests.");
        root.Subcommands.Add(Db());
        root.Subcommands.Add(Dlq());
        root.Subcommands.Add(Projection());
        root.Subcommands.Add(Topology());
        root.Subcommands.Add(QueryCommand());
        root.Subcommands.Add(Schema());
        root.Subcommands.Add(ObservabilityCommand());
        root.Subcommands.Add(MigrateCommand());
        root.Subcommands.Add(ManifestCommand());
        return root;
    }

    // ================================================================ db

    private Command Db()
    {
        var rls = new Option<bool>("--rls") { Description = "Install row-level security tenant isolation (ADR-022)." };
        var partitionEvents = new Option<bool>("--partition-events") { Description = "Partition the events table by month, keeping its rows (PartitionEvents, doc 05 §9)." };
        var migrate = Store(new Command("migrate", "Apply pending schema migrations."), rls, partitionEvents);
        migrate.SetAction((r, ct) => Guard(async () =>
        {
            PostgreSqlSchemaMigrator migrator = await MigratorAsync(r, ct);
            IReadOnlyList<int> applied = await migrator.MigrateAsync(ct);
            await Out.WriteLineAsync(applied.Count == 0 ? "The schema is up to date." : "Applied " + string.Join(", ", applied.Select(v => "V" + v.ToString("000", CultureInfo.InvariantCulture))));
            return 0;
        }));

        var from = new Option<int>("--from") { Description = "Script migrations after this version.", DefaultValueFactory = _ => 0 };
        var grants = new Option<string?>("--grants") { Description = "Print least-privilege grants for this runtime role instead (events append-only, doc 10 §1); run as the owner after migrations." };
        var script = new Command("script", "Print the migration SQL (for DBA review), or with --grants the runtime role's grants.");
        script.Options.Add(_schema);
        script.Options.Add(rls);
        script.Options.Add(partitionEvents);
        script.Options.Add(from);
        script.Options.Add(grants);
        script.SetAction((r, _) => Guard(async () =>
        {
            var migrator = new PostgreSqlSchemaMigrator(Npgsql.NpgsqlDataSource.Create("Host=localhost"), r.GetValue(_schema)!,
                r.GetValue(rls) ? TenantIsolation.RowLevelSecurity : TenantIsolation.Runtime, r.GetValue(partitionEvents));
            await Out.WriteLineAsync(r.GetValue(grants) is { } role ? migrator.GrantsScript(role) : migrator.Script(r.GetValue(from)));
            return 0;
        }));

        var apply = new Option<bool>("--apply") { Description = "Create missing and drop stale indexes (default: only show the plan)." };
        var index = new Command("index", "SFQL expression indexes for [Queryable] fields (doc 14 §7.2).");
        var sync = new Command("sync", "Compare (or with --apply, sync) the indexes with the service's catalog.");
        sync.Options.Add(apply);
        sync.SetAction((r, ct) => Guard(async () =>
        {
            IServiceProvider provider = Service("db index sync");
            PostgreSqlStore store = provider.GetService<PostgreSqlStore>() ?? throw new CliException("db index sync needs a service on PostgreSQL storage.");
            QueryCatalog catalog = QueryCatalog.Build(provider.GetRequiredService<RuntimeModel>());
            QueryIndexPlan plan = r.GetValue(apply)
                ? await PostgreSqlIndexSync.ApplyAsync(store.DataSource, catalog, store.Options.Schema, concurrently: true, ct)
                : await PostgreSqlIndexSync.PlanAsync(store.DataSource, catalog, store.Options.Schema, ct);
            foreach (QueryIndex create in plan.Create)
            {
                await Out.WriteLineAsync($"{(r.GetValue(apply) ? "created" : "create")}  {create.Name}  {create.Source}");
            }

            foreach (string drop in plan.Drop)
            {
                await Out.WriteLineAsync($"{(r.GetValue(apply) ? "dropped" : "drop")}    {drop}");
            }

            await Out.WriteLineAsync($"{plan.Unchanged.Count} unchanged");
            return 0;
        }));
        index.Subcommands.Add(sync);

        var db = new Command("db", "Database schema, indexes and partitions.");
        db.Subcommands.Add(migrate);
        db.Subcommands.Add(script);
        db.Subcommands.Add(index);
        db.Subcommands.Add(Partitions());
        return db;
    }

    // Monthly events partitions (PartitionEvents, doc 05 §9).
    private Command Partitions()
    {
        var list = Store(new Command("list", "List the events partitions, oldest first."));
        list.SetAction((r, ct) => Guard(async () =>
        {
            await using PartitionStore store = PartitionStoreFor(r);
            foreach (EventPartition partition in await store.Store.ListEventPartitionsAsync(ct))
            {
                string from = partition.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "(start)";
                string state = partition.DetachedAt is { } at ? "detached " + at.ToString("O", CultureInfo.InvariantCulture) : "attached";
                await Out.WriteLineAsync($"{partition.Name,-16} {from,-10} .. {partition.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),-10}  {state}");
            }

            return 0;
        }));

        var ensure = Store(new Command("ensure", "Create the monthly partitions ahead now (the service's maintenance job also does this)."));
        ensure.SetAction((r, ct) => Guard(async () =>
        {
            await using PartitionStore store = PartitionStoreFor(r);
            int created = await store.Store.EnsureEventPartitionsAsync(ct);
            await Out.WriteLineAsync($"Created {created} partition(s).");
            return 0;
        }));

        var name = new Argument<string>("name") { Description = "The partition, for example events_2026_01 or events_initial." };
        var detach = Store(new Command("detach", "Detach an old partition whose streams are all archived or deleted; the table keeps its rows for archiving."));
        detach.Arguments.Add(name);
        detach.SetAction((r, ct) => Guard(async () =>
        {
            await using PartitionStore store = PartitionStoreFor(r);
            string partition = r.GetValue(name)!;
            await store.Store.DetachEventPartitionAsync(partition, ct);
            await Out.WriteLineAsync($"Detached {partition}; archive it (for example with pg_dump -t) and drop it when ready.");
            return 0;
        }));

        var partitions = new Command("partitions", "Monthly events partitions (PartitionEvents, doc 05 §9).");
        partitions.Subcommands.Add(list);
        partitions.Subcommands.Add(ensure);
        partitions.Subcommands.Add(detach);
        return partitions;
    }

    private PartitionStore PartitionStoreFor(ParseResult r)
    {
        if (services?.GetService<PostgreSqlStore>() is { } running)
        {
            return running.Options.PartitionEvents
                ? new PartitionStore(running, owned: false)
                : throw new CliException("The service's events table is not partitioned: set PostgreSqlStorageOptions.PartitionEvents and run 'db migrate'.");
        }

        string connection = r.GetValue(_connection) ?? Environment.GetEnvironmentVariable("SOURCEFLOW_CONNECTION")
            ?? throw new CliException("Pass --connection or set SOURCEFLOW_CONNECTION (or run inside the service).");
        return new PartitionStore(new PostgreSqlStore(new PostgreSqlStorageOptions { ConnectionString = connection, Schema = r.GetValue(_schema)!, PartitionEvents = true }), owned: true);
    }

    private sealed class PartitionStore(PostgreSqlStore store, bool owned) : IAsyncDisposable
    {
        public PostgreSqlStore Store { get; } = store;

        public ValueTask DisposeAsync()
        {
            return owned ? Store.DisposeAsync() : ValueTask.CompletedTask;
        }
    }

    // ================================================================ dlq

    private Command Dlq()
    {
        var consumer = new Option<string?>("--consumer") { Description = "Only this consumer." };
        var reason = new Option<string?>("--reason") { Description = "Only this reason." };
        var type = new Option<string?>("--type") { Description = "Only this message type." };
        var status = new Option<string>("--status") { Description = "parked (default), replayed, discarded or all.", DefaultValueFactory = _ => "parked" };
        var limit = new Option<int>("--limit") { Description = "The most rows.", DefaultValueFactory = _ => 100 };
        var list = Store(new Command("list", "List dead letters, newest first."), consumer, reason, type, status, limit);
        list.SetAction((r, ct) => Guard(async () =>
        {
            await using Ports ports = await PortsAsync(r, ct);
            DeadLetterStatus? filter = r.GetValue(status) switch
            {
                "all" => null,
                "replayed" => DeadLetterStatus.Replayed,
                "discarded" => DeadLetterStatus.Discarded,
                "parked" => DeadLetterStatus.Parked,
                var other => throw new CliException($"Unknown status '{other}'."),
            };
            IReadOnlyList<DeadLetter> letters = await ports.Storage.DeadLetters.QueryAsync(new DeadLetterQuery(r.GetValue(consumer), r.GetValue(reason), filter, r.GetValue(limit), r.GetValue(type)), ct);
            await Out.WriteLineAsync("id                                    parked                     consumer                  reason              type");
            foreach (DeadLetter d in letters)
            {
                await Out.WriteLineAsync($"{d.Id}  {d.ParkedAt:yyyy-MM-dd HH:mm:ss}Z  {d.Consumer,-24}  {d.Reason,-18}  {d.Envelope.Type}");
            }

            await Out.WriteLineAsync($"{letters.Count} dead letter(s)");
            return 0;
        }));

        var id = new Argument<Guid>("id") { Description = "The dead letter id." };
        var payload = new Option<bool>("--payload") { Description = "Include the raw payload (it may contain personal data)." };
        var show = Store(new Command("show", "Show a dead letter: envelope, destination and error."), payload);
        show.Arguments.Add(id);
        show.SetAction((r, ct) => Guard(async () =>
        {
            await using Ports ports = await PortsAsync(r, ct);
            DeadLetter d = await ports.Storage.DeadLetters.GetAsync(r.GetValue(id), ct) ?? throw new CliException("No such dead letter.");
            var text = new StringBuilder()
                .AppendLine(CultureInfo.InvariantCulture, $"id:           {d.Id}")
                .AppendLine(CultureInfo.InvariantCulture, $"status:       {d.Status}{(d.StatusNote is null ? string.Empty : " (" + d.StatusNote + ")")}")
                .AppendLine(CultureInfo.InvariantCulture, $"consumer:     {d.Consumer} ({d.Source})")
                .AppendLine(CultureInfo.InvariantCulture, $"destination:  {d.Destination ?? "-"}{(d.DestinationIsTopic ? " (topic)" : string.Empty)}")
                .AppendLine(CultureInfo.InvariantCulture, $"reason:       {d.Reason}")
                .AppendLine(CultureInfo.InvariantCulture, $"error:        {d.ErrorType}: {d.ErrorMessage}")
                .AppendLine(CultureInfo.InvariantCulture, $"attempts:     {d.Attempts}, first failed {d.FirstFailedAt:O}, parked {d.ParkedAt:O}")
                .AppendLine(CultureInfo.InvariantCulture, $"message:      {d.Envelope.Type} v{d.Envelope.TypeVersion} {d.Envelope.MessageId}")
                .AppendLine(CultureInfo.InvariantCulture, $"correlation:  {d.Envelope.CorrelationId.Value}")
                .AppendLine(CultureInfo.InvariantCulture, $"tenant:       {d.Envelope.TenantId}");
            if (r.GetValue(payload))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"payload:      {Encoding.UTF8.GetString(d.Envelope.Body.Span)}");
            }

            await Out.WriteAsync(text.ToString());
            return 0;
        }));

        var note = new Option<string?>("--note") { Description = "An audit note." };
        var outboxPartitions = new Option<int>("--outbox-partitions") { Description = "The service's OutboxPartitions (global tool only).", DefaultValueFactory = _ => 16 };
        var replay = Store(new Command("replay", "Re-send a parked message to its destination; its id is kept, so the inbox still protects."), note, outboxPartitions);
        replay.Arguments.Add(id);
        replay.SetAction((r, ct) => Guard(async () =>
        {
            if (services?.GetService<IDeadLetterManager>() is { } manager)
            {
                await manager.ReplayAsync(r.GetValue(id), Audited(r.GetValue(note)), ct);
            }
            else
            {
                await using Ports ports = await PortsAsync(r, ct);
                await ReplayThroughOutboxAsync(ports.Storage, r.GetValue(id), Audited(r.GetValue(note)), r.GetValue(outboxPartitions), ct);
            }

            await Out.WriteLineAsync("Replayed " + r.GetValue(id));
            return 0;
        }));

        var reasonRequired = new Option<string>("--reason") { Description = "Why it is discarded (audited).", Required = true };
        var discard = Store(new Command("discard", "Discard a parked message with an audited reason."), reasonRequired);
        discard.Arguments.Add(id);
        discard.SetAction((r, ct) => Guard(async () =>
        {
            await using Ports ports = await PortsAsync(r, ct);
            _ = await ports.Storage.DeadLetters.GetAsync(r.GetValue(id), ct) ?? throw new CliException("No such dead letter.");
            await ports.Storage.DeadLetters.SetStatusAsync(r.GetValue(id), DeadLetterStatus.Discarded, Audited(r.GetValue(reasonRequired)), ct);
            await Out.WriteLineAsync("Discarded " + r.GetValue(id));
            return 0;
        }));

        var before = new Option<DateTimeOffset>("--before") { Description = "Delete replayed and discarded dead letters resolved before this time.", Required = true };
        var purge = Store(new Command("purge", "Delete resolved dead letters; parked ones are kept."), before);
        purge.SetAction((r, ct) => Guard(async () =>
        {
            await using Ports ports = await PortsAsync(r, ct);
            IStoreRetention retention = ports.Storage.Retention ?? throw new CliException("This storage has no retention support.");
            int total = 0;
            int deleted;
            do
            {
                deleted = await retention.PurgeDeadLettersAsync(r.GetValue(before), 1000, ct);
                total += deleted;
            }
            while (deleted > 0);

            await Out.WriteLineAsync($"Purged {total} dead letter(s)");
            return 0;
        }));

        var dlq = new Command("dlq", "Dead letters (doc 04 §9.3).");
        foreach (Command c in new[] { list, show, replay, discard, purge })
        {
            dlq.Subcommands.Add(c);
        }

        return dlq;
    }

    // Global tool: the outbox relay of the running service sends it (no transport in the CLI).
    private static async Task ReplayThroughOutboxAsync(StoragePorts storage, Guid id, string? note, int outboxPartitions, CancellationToken ct)
    {
        DeadLetter d = await storage.DeadLetters.GetAsync(id, ct) ?? throw new CliException("No such dead letter.");
        if (d.Status != DeadLetterStatus.Parked)
        {
            throw new CliException($"The dead letter is {d.Status}; only parked messages can be replayed.");
        }

        string destination = d.Destination ?? throw new CliException("This dead letter came from a subscription and has no destination; fix the cause and rebuild or skip instead.");
        await using (IStorageSession session = await storage.Sessions.BeginAsync(d.Envelope.TenantId, ct))
        {
            int partition = OutboxPartitioning.PartitionOf(d.Envelope.EffectivePartitionKey, outboxPartitions);
            session.EnqueueOutbox(new OutboxMessage(d.Envelope, destination, d.DestinationIsTopic ? OutboxKind.Publish : OutboxKind.Send, DateTimeOffset.MinValue, partition));
            await session.CommitAsync(ct);
        }

        await storage.DeadLetters.SetStatusAsync(id, DeadLetterStatus.Replayed, note, ct);
    }

    // ================================================================ projection

    private Command Projection()
    {
        var subscription = new Argument<string>("subscription") { Description = "The subscription, for example projection:account-summary@1." };
        var partition = new Option<int>("--partition") { Description = "The partition.", DefaultValueFactory = _ => 0 };
        var partitions = new Option<int>("--partitions") { Description = "The subscription's partition count (SubscriptionPartitions).", DefaultValueFactory = _ => 1 };
        var reason = new Option<string>("--reason") { Description = "Why (audited).", Required = true };

        var skip = Store(new Command("skip", "Skip the next event of a stopped partition (audited in dead letters as discarded)."), partition, partitions, reason);
        skip.Arguments.Add(subscription);
        skip.SetAction((r, ct) => Guard(async () =>
        {
            GlobalPosition skipped;
            if (services?.GetService<ISubscriptionAdmin>() is { } admin)
            {
                skipped = await admin.SkipAsync(r.GetValue(subscription)!, r.GetValue(partition), Audited(r.GetValue(reason)), ct);
            }
            else
            {
                await using Ports ports = await PortsAsync(r, ct);
                skipped = await SkipAsync(ports.Storage, r.GetValue(subscription)!, r.GetValue(partition), r.GetValue(partitions), Audited(r.GetValue(reason)), ct);
            }

            await Out.WriteLineAsync($"Skipped the event at {skipped}");
            return 0;
        }));

        var fromPartitions = new Option<int>("--from") { Description = "The current partition count.", Required = true };
        var toPartitions = new Option<int>("--to") { Description = "The new partition count (a power of two).", Required = true };
        var idempotent = new Option<bool>("--idempotent") { Description = "Confirm the projection is version-guarded (required for non-document projections)." };
        var repartition = Store(new Command("repartition", "Change a subscription's partition count: every new partition starts at the minimum old checkpoint (doc 06 §3)."), fromPartitions, toPartitions, idempotent);
        repartition.Arguments.Add(subscription);
        repartition.SetAction((r, ct) => Guard(async () =>
        {
            string name = r.GetValue(subscription)!;
            RequireIdempotent(name, r.GetValue(idempotent));
            await using Ports ports = await PortsAsync(r, ct);
            GlobalPosition minimum = await RepartitionAsync(ports.Storage, name, r.GetValue(fromPartitions), r.GetValue(toPartitions), ct);
            await Out.WriteLineAsync($"{name}: {r.GetValue(toPartitions)} partitions from {minimum}. Set SubscriptionPartitions = {r.GetValue(toPartitions)} and restart.");
            return 0;
        }));

        var collection = new Argument<string>("collection") { Description = "The read-model generation, for example account-summary@1." };
        var inPlace = new Option<bool>("--in-place") { Description = "Drop the documents and replay into the same generation (stop other nodes' subscriptions first) instead of building a new generation beside it." };
        var rebuild = Store(new Command("rebuild", "Rebuild a projection blue/green: the service builds a new generation from the start and switches reads to it once it has caught up (doc 06 §6)."), partitions, inPlace);
        rebuild.Arguments.Add(collection);
        rebuild.SetAction((r, ct) => Guard(async () =>
        {
            string name = r.GetValue(collection)!;
            await using Ports ports = await PortsAsync(r, ct);
            if (!r.GetValue(inPlace))
            {
                (string serving, string building) = await RequestGenerationAsync(ports.Storage, name, ct);
                await Out.WriteLineAsync($"{ReadModelGeneration.Parse(building).Name}: building {building} beside {serving}; reads switch to it once it has caught up.");
                return 0;
            }

            IDocumentAdmin admin = ports.Storage.DocumentAdmin ?? throw new CliException("This storage can't drop collections.");
            List<LeaseToken> leases = await AcquireAllAsync(ports.Storage, "projection:" + name, r.GetValue(partitions), ct);
            try
            {
                int dropped = await admin.DropCollectionAsync(name, ct);
                foreach (LeaseToken lease in leases)
                {
                    await using IStorageSession session = await ports.Storage.Sessions.BeginAsync(MessageEnvelope.DefaultTenant, ct);
                    session.SaveCheckpoint("projection:" + name, int.Parse(lease.Name[(lease.Name.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture), GlobalPosition.Start, lease);
                    await session.CommitAsync(ct);
                }

                await Out.WriteLineAsync($"{name}: dropped {dropped} document(s); the subscription replays from the start.");
            }
            finally
            {
                await ReleaseAsync(ports.Storage, leases, ct);
            }

            return 0;
        }));

        var drop = Store(new Command("drop", "Delete a retired read-model generation's documents."));
        drop.Arguments.Add(collection);
        drop.SetAction((r, ct) => Guard(async () =>
        {
            string name = r.GetValue(collection)!;
            await using Ports ports = await PortsAsync(r, ct);
            IDocumentAdmin admin = ports.Storage.DocumentAdmin ?? throw new CliException("This storage can't drop collections.");
            if (ports.Storage.Aliases is { } aliases && ReadModelGeneration.TryParse(name, out ReadModelGeneration? generation)
                && (await aliases.ListAliasesAsync(ct)).FirstOrDefault(a => a.Alias == generation.Value.Name) is { } alias)
            {
                if (alias.Target == name || alias.Building == name)
                {
                    throw new CliException($"{name} {(alias.Target == name ? "serves reads" : "is being built")}; only a retired generation can be dropped.");
                }

                if (alias.Previous == name)
                {
                    await aliases.ClearPreviousAsync(alias.Alias, name, ct);
                }
            }

            int dropped = await admin.DropCollectionAsync(name, ct);
            await Out.WriteLineAsync($"Dropped {dropped} document(s) of {name}");
            return 0;
        }));

        var format = new Option<string>("--format") { Description = "text or json.", DefaultValueFactory = _ => "text" };
        format.AcceptOnlyFromAmong("text", "json");
        var generations = Store(new Command("generations", "List the read-model aliases: the generation serving reads, the one building and the retired one (doc 06 §6)."), format);
        generations.SetAction((r, ct) => Guard(async () =>
        {
            await using Ports ports = await PortsAsync(r, ct);
            ICollectionAliases aliases = ports.Storage.Aliases ?? throw new CliException("This storage keeps no read-model aliases.");
            IReadOnlyList<CollectionAlias> rows = await aliases.ListAliasesAsync(ct);
            if (r.GetValue(format) == "json")
            {
                var array = new JsonArray();
                foreach (CollectionAlias a in rows)
                {
                    array.Add(new JsonObject
                    {
                        ["projection"] = a.Alias,
                        ["serving"] = a.Target,
                        ["building"] = a.Building,
                        ["previous"] = a.Previous,
                        ["switchedAt"] = a.SwitchedAt.ToString("O", CultureInfo.InvariantCulture),
                    });
                }

                await Out.WriteLineAsync(array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }

            foreach (CollectionAlias a in rows)
            {
                await Out.WriteLineAsync($"{a.Alias}  serving {a.Target}  building {a.Building ?? "-"}  previous {a.Previous ?? "-"}  switched {a.SwitchedAt:O}");
            }

            if (rows.Count == 0)
            {
                await Out.WriteLineAsync("No read-model aliases yet.");
            }

            return 0;
        }));

        var aliasName = new Argument<string>("projection") { Description = "The projection name, for example account-summary." };
        var target = new Argument<string>("generation") { Description = "The generation to serve reads, for example account-summary@1." };
        var switchCommand = Store(new Command("switch", "Point a projection's reads at another generation (a rollback while the previous generation is kept)."), reason);
        switchCommand.Arguments.Add(aliasName);
        switchCommand.Arguments.Add(target);
        switchCommand.SetAction((r, ct) => Guard(async () =>
        {
            string projectionName = r.GetValue(aliasName)!;
            string to = r.GetValue(target)!;
            if (!ReadModelGeneration.TryParse(to, out ReadModelGeneration? generation) || generation.Value.Name != projectionName)
            {
                throw new CliException($"{to} is not a generation of {projectionName}.");
            }

            await using Ports ports = await PortsAsync(r, ct);
            ICollectionAliases aliases = ports.Storage.Aliases ?? throw new CliException("This storage keeps no read-model aliases.");
            CollectionAlias current = (await aliases.ListAliasesAsync(ct)).FirstOrDefault(a => a.Alias == projectionName)
                ?? throw new CliException($"{projectionName} has no alias yet.");
            if (current.Target == to)
            {
                throw new CliException($"{to} already serves reads.");
            }

            if (!await aliases.SwitchAsync(projectionName, current.Target, to, ct))
            {
                throw new CliException($"{projectionName} switched concurrently; list the generations and retry.");
            }

            await Out.WriteLineAsync($"{projectionName}: reads switched from {current.Target} to {to} ({r.GetValue(reason)}). Nodes running an older version resume it; newer versions build again.");
            return 0;
        }));

        var projection = new Command("projection", "Projections and subscriptions (doc 06).");
        foreach (Command c in new[] { skip, repartition, rebuild, drop, generations, switchCommand })
        {
            projection.Subcommands.Add(c);
        }

        return projection;
    }

    // Requests the next generation of the serving one: name@v.n+1 (doc 06 §6). The service builds it and switches reads.
    private static async Task<(string Serving, string Building)> RequestGenerationAsync(StoragePorts storage, string name, CancellationToken ct)
    {
        ICollectionAliases aliases = storage.Aliases ?? throw new CliException("This storage keeps no read-model aliases; use --in-place.");
        if (!ReadModelGeneration.TryParse(name, out ReadModelGeneration? requested))
        {
            throw new CliException($"'{name}' is not a read-model generation (name@version).");
        }

        CollectionAlias? alias = (await aliases.ListAliasesAsync(ct)).FirstOrDefault(a => a.Alias == requested.Value.Name);
        ReadModelGeneration serving = alias is null ? requested.Value : ReadModelGeneration.Parse(alias.Target);
        if (serving.Version != requested.Value.Version)
        {
            throw new CliException($"{serving} serves reads of {serving.Name}, not version {requested.Value.Version}; rebuild the serving version.");
        }

        ReadModelGeneration latest = serving;
        if (alias?.Building is { } building && ReadModelGeneration.TryParse(building, out ReadModelGeneration? pending) && pending.Value.IsNewerThan(latest))
        {
            latest = pending.Value;
        }

        ReadModelGeneration next = latest.Next();
        await aliases.RequestAsync(serving.Name, serving.Collection, next.Collection, ct);
        return (serving.Collection, next.Collection);
    }

    private void RequireIdempotent(string subscription, bool confirmed)
    {
        if (confirmed || services is null)
        {
            if (!confirmed)
            {
                throw new CliException("Repartitioning re-applies events; confirm the projection is version-guarded with --idempotent.");
            }

            return;
        }

        SourceFlowRuntime runtime = services.GetRequiredService<SourceFlowRuntime>();
        ProjectionDescriptor? projection = runtime.AsyncProjections.FirstOrDefault(p => "projection:" + p.Collection == subscription);
        if (projection is not DocumentProjectionDescriptor)
        {
            throw new CliException($"{subscription} isn't a document projection with the automatic version guard (DC-2); pass --idempotent if it uses IdempotentUpsert.");
        }
    }

    private static async Task<GlobalPosition> SkipAsync(StoragePorts storage, string subscription, int partition, int partitions, string reason, CancellationToken ct)
    {
        LeaseToken lease = await storage.Leases.TryAcquireAsync($"sub:{subscription}:{partition}", CliOwner, TimeSpan.FromMinutes(1), ct)
            ?? throw new CliException($"A node holds {subscription} partition {partition}; run the skip inside that service or stop it.");
        try
        {
            GlobalPosition checkpoint = await storage.Checkpoints.GetAsync(subscription, partition, ct);
            LogBatch next = await storage.Log.ReadAsync(checkpoint, Buckets.Range(partition, partitions), 1, ct);
            StoredEvent stored = next.Events.Count > 0 ? next.Events[0] : throw new CliException("The partition has no event to skip.");
            DateTimeOffset now = TimeProvider.System.GetUtcNow();
            var id = Guid.CreateVersion7(now);
            await storage.DeadLetters.ParkAsync(new DeadLetter
            {
                Id = id,
                Consumer = subscription,
                Source = "subscription",
                Envelope = stored.ToEnvelope(),
                Reason = "Skipped",
                FirstFailedAt = now,
                ParkedAt = now,
            }, ct);
            await storage.DeadLetters.SetStatusAsync(id, DeadLetterStatus.Discarded, "skipped: " + reason, ct);   // reason is Audited by the caller
            await using IStorageSession session = await storage.Sessions.BeginAsync(stored.TenantId, ct);
            session.SaveCheckpoint(subscription, partition, stored.Position, lease);
            await session.CommitAsync(ct);
            return stored.Position;
        }
        finally
        {
            await storage.Leases.ReleaseAsync(lease, ct);
        }
    }

    private static async Task<GlobalPosition> RepartitionAsync(StoragePorts storage, string subscription, int from, int to, CancellationToken ct)
    {
        if (to < 1 || (to & (to - 1)) != 0 || from < 1)
        {
            throw new CliException("Partition counts must be positive powers of two.");
        }

        List<LeaseToken> leases = await AcquireAllAsync(storage, subscription, Math.Max(from, to), ct);
        try
        {
            GlobalPosition minimum = GlobalPosition.Start;
            bool first = true;
            for (int p = 0; p < from; p++)
            {
                GlobalPosition checkpoint = await storage.Checkpoints.GetAsync(subscription, p, ct);
                if (first || checkpoint.CompareTo(minimum) < 0)
                {
                    minimum = checkpoint;
                    first = false;
                }
            }

            foreach (LeaseToken lease in leases.Take(to))
            {
                await using IStorageSession session = await storage.Sessions.BeginAsync(MessageEnvelope.DefaultTenant, ct);
                session.SaveCheckpoint(subscription, int.Parse(lease.Name[(lease.Name.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture), minimum, lease);
                await session.CommitAsync(ct);
            }

            return minimum;
        }
        finally
        {
            await ReleaseAsync(storage, leases, ct);
        }
    }

    private static async Task<List<LeaseToken>> AcquireAllAsync(StoragePorts storage, string subscription, int partitions, CancellationToken ct)
    {
        var leases = new List<LeaseToken>();
        for (int p = 0; p < partitions; p++)
        {
            LeaseToken? lease = await storage.Leases.TryAcquireAsync($"sub:{subscription}:{p}", CliOwner, TimeSpan.FromMinutes(5), ct);
            if (lease is null)
            {
                await ReleaseAsync(storage, leases, ct);
                throw new CliException($"A running node holds {subscription} partition {p}; stop the subscription first.");
            }

            leases.Add(lease);
        }

        return leases;
    }

    private static async Task ReleaseAsync(StoragePorts storage, List<LeaseToken> leases, CancellationToken ct)
    {
        foreach (LeaseToken lease in leases)
        {
            await storage.Leases.ReleaseAsync(lease, ct);
        }
    }

    // ================================================================ topology (service)

    private Command Topology()
    {
        var format = new Option<string>("--format") { Description = "json (default) or text.", DefaultValueFactory = _ => "json" };
        var output = new Option<FileInfo?>("--output") { Description = "Write to this file instead of standard output." };
        var export = new Command("export", "Write the service's messaging topology (logical, and the transport's physical resources) for IaC (doc 07 §5, deploy/topology).");
        export.Options.Add(format);
        export.Options.Add(output);
        export.SetAction((r, ct) => Guard(async () =>
        {
            SourceFlowRuntime runtime = Service("topology export").GetRequiredService<SourceFlowRuntime>();
            TopologyDocument document = runtime.TopologyDocument();
            string text = r.GetValue(format) switch
            {
                "json" => TopologyExport.ToJson(document),
                "text" => TopologyText(document),
                var other => throw new ArgumentException($"Unknown format '{other}': use json or text."),
            };

            if (r.GetValue(output) is { } file)
            {
                await File.WriteAllTextAsync(file.FullName, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
                await Out.WriteLineAsync($"Wrote the topology of {document.Service} to {file.FullName}.");
            }
            else
            {
                await Out.WriteAsync(text);
            }

            return 0;
        }));

        var validate = new Command("validate", "Check that the transport has the service's endpoint, topics and subscriptions (ProvisioningMode.Validate).");
        validate.SetAction((_, ct) => Guard(async () =>
        {
            SourceFlowRuntime runtime = Service("topology validate").GetRequiredService<SourceFlowRuntime>();
            TransportTopology topology = runtime.Topology();
            try
            {
                await runtime.Transport.ProvisionAsync(topology, ProvisioningMode.Validate, ct);
            }
            catch (Exception e) when (e is InvalidOperationException or TopologyValidationException)
            {
                await Err.WriteLineAsync("Topology mismatch: " + e.Message);
                return 1;
            }

            await Out.WriteLineAsync("The topology is in place.");
            return 0;
        }));

        var command = new Command("topology", "Messaging topology (doc 07).");
        command.Subcommands.Add(export);
        command.Subcommands.Add(validate);
        return command;
    }

    private static string TopologyText(TopologyDocument document)
    {
        TransportTopology topology = document.Topology;
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"endpoint  {topology.Endpoint}{(topology.OrderedEndpoint ? " (ordered)" : string.Empty)}, dead letter after {topology.MaxDeliveries} deliveries\n");
        foreach (string topic in topology.PublishedTopics.Order(StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"publish   {topic}{(topology.OrderedTopics.Contains(topic) ? " (ordered)" : string.Empty)}\n");
        }

        foreach (string topic in topology.SubscribedTopics.Order(StringComparer.Ordinal))
        {
            string? filter = topology.Filters.GetValueOrDefault(topic);
            text.Append(CultureInfo.InvariantCulture, $"subscribe {topic}{(filter is null ? string.Empty : " where " + filter)}\n");
        }

        foreach ((string endpoint, IReadOnlyList<string> commands) in document.SentCommands.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"send      {string.Join(", ", commands.Order(StringComparer.Ordinal))} -> {endpoint}\n");
        }

        if (document.Physical is { } physical)
        {
            text.Append(CultureInfo.InvariantCulture, $"resources ({physical.Transport})\n");
            foreach (PhysicalResource resource in physical.Resources)
            {
                text.Append(CultureInfo.InvariantCulture, $"  {TopologyExport.KindName(resource.Kind),-12} {resource.Name}{(resource.Owned ? string.Empty : " (owned by the publisher)")}\n");
            }
        }

        return text.ToString();
    }

    // ================================================================ query (service)

    private Command QueryCommand()
    {
        var text = new Argument<string>("query") { Description = "The SFQL (or with --sql, SQL-syntax) query." };
        var sql = new Option<bool>("--sql") { Description = "The query is in the SQL syntax." };
        var parameters = new Option<string[]>("--param") { Description = "A parameter, name=value (repeatable)." };
        var validateOnly = new Option<bool>("--validate") { Description = "Only parse, bind and check." };
        var explain = new Option<bool>("--explain") { Description = "Show the plan and the parameterised statement." };
        var format = new Option<string>("--format") { Description = "table (default), json or csv.", DefaultValueFactory = _ => "table" };
        var pageSize = new Option<int>("--page-size") { Description = "Rows per page.", DefaultValueFactory = _ => 100 };
        var command = new Command("query", "Run a query against the service's own data (doc 14).");
        command.Arguments.Add(text);
        foreach (Option o in new Option[] { sql, parameters, validateOnly, explain, format, pageSize })
        {
            command.Options.Add(o);
        }

        command.SetAction((r, ct) => Guard(async () =>
        {
            IQueryService queries = Service("query").GetRequiredService<IQueryService>();
            var options = new QueryOptions { Language = r.GetValue(sql) ? QueryLanguage.Sql : QueryLanguage.Sfql, PageSize = r.GetValue(pageSize) };
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (string pair in r.GetValue(parameters) ?? [])
            {
                int equals = pair.IndexOf('=', StringComparison.Ordinal);
                if (equals <= 0)
                {
                    throw new CliException($"--param '{pair}' must be name=value.");
                }

                values[pair[..equals].TrimStart('@')] = pair[(equals + 1)..];
            }

            string query = r.GetValue(text)!;
            try
            {
                if (r.GetValue(validateOnly))
                {
                    QueryValidation validation = await queries.ValidateAsync(query, values, options, ct);
                    await WriteDiagnosticsAsync(validation.Diagnostics);
                    await Out.WriteLineAsync(validation.IsValid ? "valid: " + string.Join(", ", validation.Columns.Select(c => $"{c.Name} {c.Type.ToString().ToLowerInvariant()}")) : "not valid");
                    return validation.IsValid ? 0 : 1;
                }

                if (r.GetValue(explain))
                {
                    QueryPlan plan = await queries.ExplainAsync(query, values, options, ct);
                    await Out.WriteLineAsync($"engine:     {plan.Engine}");
                    await Out.WriteLineAsync($"columns:    {string.Join(", ", plan.Columns.Select(c => $"{c.Name} {c.Type.ToString().ToLowerInvariant()}"))}");
                    await Out.WriteLineAsync($"sources:    {string.Join(", ", plan.Sources.Select(s => $"{s.Source} ({s.Level})"))}");
                    await Out.WriteLineAsync($"parameters: {plan.ParameterCount}");
                    await Out.WriteLineAsync($"hash:       {plan.QueryHash}");
                    if (plan.Statement is { } statement)
                    {
                        await Out.WriteLineAsync("statement:");
                        await Out.WriteLineAsync(statement);
                    }

                    return 0;
                }

                await WriteRowsAsync(queries.QueryAsync(query, values, options, ct), r.GetValue(format)!, ct);
                return 0;
            }
            catch (QueryException e)
            {
                await WriteDiagnosticsAsync(e.Diagnostics);
                return 1;
            }
        }));
        return command;
    }

    private async Task WriteDiagnosticsAsync(IReadOnlyList<QueryDiagnostic> diagnostics)
    {
        foreach (QueryDiagnostic d in diagnostics)
        {
            await Err.WriteLineAsync($"{d.Code} ({d.Line}:{d.Column}): {d.Message}");
        }
    }

    private async Task WriteRowsAsync(IAsyncEnumerable<QueryRow> rows, string format, CancellationToken ct)
    {
        IReadOnlyList<QueryColumn>? columns = null;
        var table = new List<string[]>();
        await foreach (QueryRow row in rows.WithCancellation(ct))
        {
            columns ??= row.Columns;
            string[] cells = [.. row.Values.Select(Cell)];
            switch (format)
            {
                case "json":
                    await Out.WriteLineAsync(JsonSerializer.Serialize(
                        columns.Select((c, i) => (c.Name, cells[i])).ToDictionary(p => p.Name, p => p.Item2), CliJson.Default.DictionaryStringString));
                    break;
                case "csv":
                    if (table.Count == 0)
                    {
                        await Out.WriteLineAsync(string.Join(',', columns.Select(c => Csv(c.Name))));
                        table.Add([]);
                    }

                    await Out.WriteLineAsync(string.Join(',', cells.Select(Csv)));
                    break;
                default:
                    table.Add(cells);
                    break;
            }
        }

        if (format is "json" or "csv")
        {
            return;
        }

        if (columns is null)
        {
            await Out.WriteLineAsync("(no rows)");
            return;
        }

        int[] widths = [.. columns.Select((c, i) => Math.Min(60, Math.Max(c.Name.Length, table.Count == 0 ? 0 : table.Max(t => t[i].Length))))];
        await Out.WriteLineAsync(string.Join("  ", columns.Select((c, i) => c.Name.PadRight(widths[i]))));
        await Out.WriteLineAsync(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (string[] cells in table)
        {
            await Out.WriteLineAsync(string.Join("  ", cells.Select((c, i) => (c.Length > widths[i] ? c[..(widths[i] - 1)] + "…" : c).PadRight(widths[i]))));
        }

        await Out.WriteLineAsync($"({table.Count} row{(table.Count == 1 ? string.Empty : "s")})");
    }

    private static string Cell(object? value)
    {
        return value switch
        {
            null => "null",
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            DateTimeOffset t => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture),
            JsonElement e => e.GetRawText(),
            string[] tags => string.Join(' ', tags),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }

    // S-4: formula neutralisation, then RFC 4180 quoting.
    private static string Csv(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    // ================================================================ observability (global)

    private Command ObservabilityCommand()
    {
        var output = new Option<DirectoryInfo>("--output") { Description = "The directory to write the dashboard and alert rules to.", Required = true };
        var export = new Command("export", $"Write the Grafana dashboard ({Observability.DashboardFile}) and Prometheus alert rules ({Observability.AlertsFile}) generated from the instrument catalogue (doc 08 §3, §6).");
        export.Options.Add(output);
        export.SetAction((r, ct) => Guard(async () =>
        {
            foreach (string file in await Observability.WriteAsync(r.GetValue(output)!.FullName, ct))
            {
                await Out.WriteLineAsync($"Wrote {file}.");
            }

            return 0;
        }));

        var command = new Command("observability", "Dashboards and alert rules (doc 08).");
        command.Subcommands.Add(export);
        return command;
    }

    // ================================================================ schema (service)

    private Command Schema()
    {
        var format = new Option<string>("--format") { Description = "asyncapi, jsonschema or lock (default).", DefaultValueFactory = _ => "lock" };
        var outFile = new Option<FileInfo?>("--out") { Description = "Write to a file instead of standard output." };
        var version = new Option<string>("--version") { Description = "The AsyncAPI document version.", DefaultValueFactory = _ => "1.0.0" };
        var export = new Command("export", "Export the service's schemas: a lock file, JSON Schemas or an AsyncAPI 3.0 document (doc 14 §2.2).");
        export.Options.Add(format);
        export.Options.Add(outFile);
        export.Options.Add(version);
        export.SetAction((r, ct) => Guard(async () =>
        {
            SourceFlowRuntime runtime = Service("schema export").GetRequiredService<SourceFlowRuntime>();
            SchemaCatalog catalog = SchemaCatalog.Build(runtime.Model);
            string document = r.GetValue(format) switch
            {
                "asyncapi" => AsyncApiExporter.Export(runtime.Model, runtime.Routing, catalog, runtime.ServiceName, r.GetValue(version)!),
                "jsonschema" => "[" + string.Join(",", catalog.Entries.Select(e => e.Canonical)) + "]",
                "lock" => catalog.ToLockFile(),
                var other => throw new CliException($"Unknown format '{other}'."),
            };
            await WriteAsync(r.GetValue(outFile), document, ct);
            return 0;
        }));

        var baseline = new Option<FileInfo>("--baseline") { Description = "The published lock file (schemas.lock.json).", Required = true };
        var check = new Command("check", "Check the service's schemas against a baseline lock file; exit 1 on a breaking change.");
        check.Options.Add(baseline);
        check.SetAction((r, ct) => Guard(async () =>
        {
            SourceFlowRuntime runtime = Service("schema check").GetRequiredService<SourceFlowRuntime>();
            FileInfo file = r.GetValue(baseline)!;
            if (!file.Exists)
            {
                throw new CliException($"The baseline {file.FullName} doesn't exist; create it with 'schema export --format lock --out {file.Name}'.");
            }

            SchemaCatalog published = SchemaCatalog.FromLockFile(await File.ReadAllTextAsync(file.FullName, ct));
            IReadOnlyList<CompatibilityFinding> findings = SchemaCompatibility.Check(published, SchemaCatalog.Build(runtime.Model));
            foreach (CompatibilityFinding f in findings)
            {
                TextWriter target = f.Severity == CompatibilitySeverity.Breaking ? Err : Out;
                await target.WriteLineAsync($"{f.Rule} {f.Severity.ToString().ToLowerInvariant(),-8} {f.Type} v{f.Version}{(f.Field is null ? string.Empty : "." + f.Field)}: {f.Message}");
            }

            int breaking = findings.Count(f => f.Severity == CompatibilitySeverity.Breaking);
            await Out.WriteLineAsync(breaking == 0 ? $"Compatible ({findings.Count} allowed change(s))." : $"{breaking} breaking change(s).");
            return breaking == 0 ? 0 : 1;
        }));

        var schema = new Command("schema", "Schema catalog (doc 14 §2).");
        schema.Subcommands.Add(export);
        schema.Subcommands.Add(check);
        return schema;
    }

    private async Task WriteAsync(FileInfo? file, string content, CancellationToken ct)
    {
        if (file is null)
        {
            await Out.WriteLineAsync(content);
            return;
        }

        await File.WriteAllTextAsync(file.FullName, content, ct);
        await Out.WriteLineAsync("Wrote " + file.FullName);
    }

    // ================================================================ plumbing

    // Repudiation (doc 10 §1): dead-letter replays, discards and skips record the operator who ran the command.
    private static string Audited(string? note)
    {
        string who = "by " + Environment.UserName + "@" + Environment.MachineName;
        return string.IsNullOrWhiteSpace(note) ? who : who + ": " + note;
    }

    private Command Store(Command command, params Option[] options)
    {
        command.Options.Add(_connection);
        command.Options.Add(_schema);
        command.Options.Add(_systemConnection);
        foreach (Option option in options)
        {
            command.Options.Add(option);
        }

        return command;
    }

    private IServiceProvider Service(string command)
    {
        return services ?? throw new CliException($"'{command}' needs the service's model: run it inside the service, for example 'dotnet run --project <service> -- sourceflow {command}'.");
    }

    private async Task<PostgreSqlSchemaMigrator> MigratorAsync(ParseResult r, CancellationToken ct)
    {
        await Task.CompletedTask;
        TenantIsolation isolation = r.GetValue<bool>("--rls") ? TenantIsolation.RowLevelSecurity : TenantIsolation.Runtime;
        bool partition = r.GetValue<bool>("--partition-events");
        if (services?.GetService<PostgreSqlStore>() is { } store)
        {
            return new PostgreSqlSchemaMigrator(store.DataSource, store.Options.Schema, isolation, partition || store.Options.PartitionEvents);
        }

        string connection = r.GetValue(_connection) ?? Environment.GetEnvironmentVariable("SOURCEFLOW_CONNECTION")
            ?? throw new CliException("Pass --connection or set SOURCEFLOW_CONNECTION.");
        return new PostgreSqlSchemaMigrator(Npgsql.NpgsqlDataSource.Create(connection), r.GetValue(_schema)!, isolation, partition);
    }

    private async Task<Ports> PortsAsync(ParseResult r, CancellationToken ct)
    {
        await Task.CompletedTask;
        if (services is not null)
        {
            return new Ports(services.GetRequiredService<SourceFlowRuntime>().Storage, null);
        }

        string connection = r.GetValue(_connection) ?? Environment.GetEnvironmentVariable("SOURCEFLOW_CONNECTION")
            ?? throw new CliException("Pass --connection or set SOURCEFLOW_CONNECTION (or run inside the service).");
        string? system = r.GetValue(_systemConnection);
        var store = new PostgreSqlStore(new PostgreSqlStorageOptions
        {
            ConnectionString = connection,
            Schema = r.GetValue(_schema)!,
            SystemConnectionString = system,
            TenantIsolation = system is null ? TenantIsolation.Runtime : TenantIsolation.RowLevelSecurity,
        });
        return new Ports(store.ToPorts(), store);
    }

    private async Task<int> Guard(Func<Task<int>> action)
    {
        try
        {
            return await action();
        }
        catch (CliException e)
        {
            await Err.WriteLineAsync(e.Message);
            return 1;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or KeyNotFoundException or System.Data.Common.DbException or EventPartitionException)
        {
            await Err.WriteLineAsync($"{e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    private sealed class Ports(StoragePorts storage, PostgreSqlStore? owned) : IAsyncDisposable
    {
        public StoragePorts Storage { get; } = storage;

        public ValueTask DisposeAsync()
        {
            return owned?.DisposeAsync() ?? ValueTask.CompletedTask;
        }
    }
}

internal sealed class CliException(string message) : Exception(message);

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class CliJson : System.Text.Json.Serialization.JsonSerializerContext;

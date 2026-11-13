using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Messaging;
using SourceFlow.Query;
using SourceFlow.Runtime;
using SourceFlow.Storage;

namespace SourceFlow.Migration;

/// <summary>Configures the v2 data migration (<c>sf.MigrateFromV2(…)</c>, doc 12 §3).</summary>
public sealed class V2MigrationBuilder
{
    internal V2Source? Source { get; private set; }

    internal List<V2ImportRegistration> Imports { get; } = [];

    internal HashSet<string> Ignored { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal HashSet<string> NotRebuildableTypes { get; } = new(StringComparer.Ordinal);

    /// <summary>The import rate the plan assumes for its duration estimate, in streams per second.</summary>
    public double EstimatedStreamsPerSecond { get; set; } = 200;

    /// <summary>The v2 database.</summary>
    public V2MigrationBuilder From(V2Source source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
        return this;
    }

    /// <summary>Imports one v2 entity type; the generic arguments are inferred from the import.</summary>
    public V2MigrationBuilder Import<TV2Entity, TAggregate, TId>(IV2Import<TV2Entity, TAggregate, TId> import)
        where TV2Entity : IV2Entity<TV2Entity>
        where TAggregate : class, IAggregateRuntime<TAggregate, TId>
        where TId : struct, IStronglyTypedId<TId>
    {
        ArgumentNullException.ThrowIfNull(import);
        if (Imports.Any(i => string.Equals(i.Table, TV2Entity.TableName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"The v2 table '{TV2Entity.TableName}' already has an import.", nameof(import));
        }

        Imports.Add(new V2ImportRegistration<TV2Entity, TAggregate, TId>(import));
        return this;
    }

    /// <summary>
    /// Tables of the v2 schema that hold no entities (v2 view models, idempotency records); the plan does not ask for an
    /// import for them.
    /// </summary>
    public V2MigrationBuilder Ignore(params string[] tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        Ignored.UnionWith(tables);
        return this;
    }

    /// <summary>
    /// v2 command types (assembly-qualified names, version optional) that v2 could not rebuild (GAP-04: no public
    /// parameterless constructor, or renamed); the plan reports their commands as lost history.
    /// </summary>
    public V2MigrationBuilder NotRebuildable(params string[] commandTypes)
    {
        ArgumentNullException.ThrowIfNull(commandTypes);
        foreach (string type in commandTypes)
        {
            NotRebuildableTypes.Add(V2TypeNames.Normalize(type));
        }

        return this;
    }
}

/// <summary>
/// The v2 data migration (doc 12 §3): <see cref="PlanAsync"/>, <see cref="ScaffoldAsync"/>, <see cref="ImportAsync"/>,
/// <see cref="ArchiveAsync"/> and <see cref="VerifyAsync"/>, behind <c>dotnet sourceflow migrate v2 …</c>. v2 stored
/// commands and current state; the migration imports the <b>current state as genesis events</b>, one session per stream
/// with <see cref="ExpectedVersion.NoStream"/>, so it is idempotent and resumable, and archives the v2 command history.
/// </summary>
public sealed class V2Migration
{
    /// <summary>The <c>migratedFrom</c> event header.</summary>
    public const string MigratedFromHeader = "migratedFrom";

    /// <summary>The <c>v2CommandCount</c> event header.</summary>
    public const string CommandCountHeader = "v2CommandCount";

    /// <summary>The <c>v2LastSequenceNo</c> event header.</summary>
    public const string LastSequenceNoHeader = "v2LastSequenceNo";

    /// <summary>The <c>v2Id</c> event header.</summary>
    public const string V2IdHeader = "v2Id";

    /// <summary>The actor of imported events.</summary>
    public const string Actor = "sourceflow-migrate-v2";

    private readonly V2MigrationBuilder _settings;
    private readonly SourceFlowRuntime? _runtime;
    private readonly IServiceProvider? _services;

    internal V2Migration(V2MigrationBuilder settings, SourceFlowRuntime? runtime, IServiceProvider? services)
    {
        _settings = settings;
        Source = settings.Source ?? throw new InvalidOperationException("MigrateFromV2 needs the v2 database: call From(new V2Source(…)).");
        _runtime = runtime;
        _services = services;
    }

    /// <summary>The v2 database.</summary>
    public V2Source Source { get; }

    /// <summary>The registered imports' v2 tables.</summary>
    public IReadOnlyList<string> ImportedTables => [.. _settings.Imports.Select(i => i.Table)];

    /// <summary>
    /// A migration outside a service (the global tool): <see cref="PlanAsync"/>, <see cref="ScaffoldAsync"/> and
    /// <see cref="ArchiveAsync"/> work; importing and verifying need the service's model.
    /// </summary>
    public static V2Migration ForSource(V2Source source, Action<V2MigrationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var settings = new V2MigrationBuilder().From(source);
        configure?.Invoke(settings);
        return new V2Migration(settings, null, null);
    }

    // ================================================================ plan

    /// <summary>
    /// Step 1: counts entities and commands per type, reports commands lost to GAP-04 and GAP-20, estimates the import
    /// time, and lists v2 tables that have no import (and are not ignored).
    /// </summary>
    public async Task<V2Plan> PlanAsync(CancellationToken cancellationToken = default)
    {
        var commandTypes = new List<V2PlanCommandType>();
        string sql = $"SELECT {Q("CommandType")}, COUNT(*), SUM(CASE WHEN {Q("EntityId")} = 0 THEN 1 ELSE 0 END), "
            + $"SUM(CASE WHEN {Q("PayloadData")} IS NULL OR {Q("PayloadData")} = '' THEN 1 ELSE 0 END) FROM {Source.Commands} GROUP BY {Q("CommandType")}";
        await foreach (DbDataReader row in QueryAsync(sql, cancellationToken).ConfigureAwait(false))
        {
            string type = row.IsDBNull(0) ? string.Empty : row.GetString(0);
            long count = Long(row, 1);
            long idZero = Long(row, 2);
            long empty = Long(row, 3);
            bool lost = type.Length == 0 || _settings.NotRebuildableTypes.Contains(V2TypeNames.Normalize(type));
            commandTypes.Add(new V2PlanCommandType(type, count, idZero, lost ? count : empty));
        }

        var entities = new List<V2PlanEntity>();
        foreach (V2ImportRegistration import in _settings.Imports)
        {
            long rows = await Source.ScalarAsync($"SELECT COUNT(*) FROM {Source.Table(import.Table)}", cancellationToken).ConfigureAwait(false);
            long commands = 0;
            string owned = $"SELECT c.{Q("CommandType")}, COUNT(*) FROM {Source.Commands} c WHERE EXISTS (SELECT 1 FROM {Source.Table(import.Table)} t WHERE t.{Q("Id")} = c.{Q("EntityId")}) GROUP BY c.{Q("CommandType")}";
            await foreach (DbDataReader row in QueryAsync(owned, cancellationToken).ConfigureAwait(false))
            {
                if (import.OwnsCommand(row.IsDBNull(0) ? string.Empty : row.GetString(0)))
                {
                    commands += Long(row, 1);
                }
            }

            entities.Add(new V2PlanEntity(import.Table, import.Aggregate, import.ImportType, rows, commands));
        }

        IReadOnlyList<string> tables = await Source.TablesAsync(cancellationToken).ConfigureAwait(false);
        string[] withoutImport = [.. tables.Where(t => !_settings.Ignored.Contains(t) && !_settings.Imports.Any(i => string.Equals(i.Table, t, StringComparison.OrdinalIgnoreCase)))];
        long streams = entities.Sum(e => e.Rows);
        TimeSpan estimate = TimeSpan.FromSeconds(Math.Ceiling(streams / Math.Max(1, _settings.EstimatedStreamsPerSecond)));
        return new V2Plan(entities, commandTypes, commandTypes.Sum(c => c.Commands), commandTypes.Sum(c => c.EntityIdZero), commandTypes.Sum(c => c.NotRebuildable), withoutImport, estimate);
    }

    // ================================================================ scaffold

    /// <summary>
    /// Generates, per v2 table, the row type (<see cref="IV2Entity{TSelf}"/>, from the table's columns) and an
    /// <see cref="IV2Import{TV2Entity, TAggregate, TId}"/> stub. A v3 aggregate whose state type is named like the table
    /// is filled in; otherwise the stub names placeholders to replace.
    /// </summary>
    /// <param name="tables">The tables; null for every table without an import that is not ignored.</param>
    /// <param name="namespaceName">The namespace of the generated code.</param>
    /// <param name="cancellationToken">Cancels the scaffold.</param>
    public async Task<IReadOnlyList<V2ScaffoldFile>> ScaffoldAsync(IEnumerable<string>? tables = null, string namespaceName = "V2Migration", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        IReadOnlyList<string> selected = tables?.ToList() ?? (await PlanTablesAsync(cancellationToken).ConfigureAwait(false));
        var files = new List<V2ScaffoldFile>();
        foreach (string table in selected)
        {
            IReadOnlyList<V2Column> columns = await Source.ColumnsAsync(table, cancellationToken).ConfigureAwait(false);
            if (columns.Count == 0)
            {
                throw new InvalidOperationException($"The v2 table '{table}' doesn't exist or has no columns.");
            }

            AggregateDescriptor? aggregate = _runtime?.Model.Aggregates.Values.FirstOrDefault(a =>
                string.Equals(a.StateType.Name, table, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Name, table, StringComparison.OrdinalIgnoreCase));
            files.Add(new V2ScaffoldFile(Identifier(table) + "Import.cs", V2Scaffold.Generate(namespaceName, table, columns, aggregate)));
        }

        return files;
    }

    private async Task<IReadOnlyList<string>> PlanTablesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> tables = await Source.TablesAsync(cancellationToken).ConfigureAwait(false);
        return [.. tables.Where(t => !_settings.Ignored.Contains(t) && !_settings.Imports.Any(i => string.Equals(i.Table, t, StringComparison.OrdinalIgnoreCase)))];
    }

    // ================================================================ import

    /// <summary>
    /// Step 3: imports every v2 entity as genesis events, one storage session per stream with
    /// <see cref="ExpectedVersion.NoStream"/>. Streams that already exist are skipped, so a re-run after an interruption
    /// resumes where it stopped and a repeated run changes nothing. Events carry the headers <c>migratedFrom</c>,
    /// <c>v2CommandCount</c>, <c>v2LastSequenceNo</c> and <c>v2Id</c>, deterministic ids, and are encrypted and
    /// projected inline like any append; they are not published.
    /// </summary>
    public async Task<V2ImportResult> ImportAsync(V2ImportOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new V2ImportOptions();
        if (options.BatchSize is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "BatchSize must be between 1 and 100000.");
        }

        SourceFlowRuntime runtime = RequireRuntime("import");
        string tenant = options.TenantId ?? runtime.Options.DefaultTenant;
        var tables = new List<V2ImportTableResult>();
        long budget = options.Limit ?? long.MaxValue;
        bool complete = true;
        foreach (V2ImportRegistration import in _settings.Imports)
        {
            long imported = 0;
            long skipped = 0;
            int after = int.MinValue;
            while (complete)
            {
                IReadOnlyList<object> rows = await import.PageAsync(Source, after, options.BatchSize, cancellationToken).ConfigureAwait(false);
                if (rows.Count == 0)
                {
                    break;
                }

                Dictionary<int, (int Count, int Last)> stats = await CommandStatsAsync(import, import.IdOf(rows[0]), import.IdOf(rows[^1]), cancellationToken).ConfigureAwait(false);
                foreach (object row in rows)
                {
                    if (budget == 0)
                    {
                        complete = false;
                        break;
                    }

                    if (await import.ImportAsync(this, runtime, row, stats, tenant, cancellationToken).ConfigureAwait(false))
                    {
                        imported++;
                        budget--;
                    }
                    else
                    {
                        skipped++;
                    }
                }

                after = import.IdOf(rows[^1]);
            }

            tables.Add(new V2ImportTableResult(import.Table, import.Aggregate, imported, skipped));
            if (!complete)
            {
                break;
            }
        }

        return new V2ImportResult(tables, complete);
    }

    private async Task<Dictionary<int, (int Count, int Last)>> CommandStatsAsync(V2ImportRegistration import, int from, int to, CancellationToken cancellationToken)
    {
        var stats = new Dictionary<int, (int Count, int Last)>();
        string sql = $"SELECT {Q("EntityId")}, {Q("CommandType")}, COUNT(*), MAX({Q("SequenceNo")}) FROM {Source.Commands} "
            + $"WHERE {Q("EntityId")} >= @from AND {Q("EntityId")} <= @to GROUP BY {Q("EntityId")}, {Q("CommandType")}";
        await foreach (DbDataReader row in QueryAsync(sql, cancellationToken, ("@from", from), ("@to", to)).ConfigureAwait(false))
        {
            if (!import.OwnsCommand(row.IsDBNull(1) ? string.Empty : row.GetString(1)))
            {
                continue;
            }

            int entity = Convert.ToInt32(row.GetValue(0), CultureInfo.InvariantCulture);
            (int count, int last) = stats.GetValueOrDefault(entity);
            stats[entity] = (count + (int)Long(row, 2), Math.Max(last, (int)Long(row, 3)));
        }

        return stats;
    }

    internal async ValueTask<bool> AppendGenesisAsync(SourceFlowRuntime runtime, string aggregateName, StreamId stream, IEvent[] events, V2ImportContext context, string table, CancellationToken cancellationToken)
    {
        if (events.Length == 0)
        {
            throw new InvalidOperationException($"The import of v2 table '{table}' returned no events for id {context.V2Id}; a stream needs at least one genesis event.");
        }

        await using IStorageSession session = await runtime.Storage.Sessions.BeginAsync(context.TenantId, cancellationToken).ConfigureAwait(false);
        if ((await session.ReadStreamAsync(stream, new StreamVersion(1), 1, cancellationToken).ConfigureAwait(false)).Events.Count > 0)
        {
            return false;   // imported by an earlier run
        }

        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MigratedFromHeader] = "v2",
            [CommandCountHeader] = context.CommandCount.ToString(CultureInfo.InvariantCulture),
            [LastSequenceNoHeader] = context.LastSequenceNo.ToString(CultureInfo.InvariantCulture),
            [V2IdHeader] = context.V2Id.ToString(CultureInfo.InvariantCulture),
        };
        DateTimeOffset now = context.ImportedAt;
        var toAppend = new EventToAppend[events.Length];
        var envelopes = new MessageEnvelope[events.Length];
        CorrelationId correlation = CorrelationId.From(MessageIds.FromKey("sourceflow-v2-import", context.TenantId + "\n" + stream.Value));
        for (int i = 0; i < events.Length; i++)
        {
            MessageId id = MessageIds.FromKey("sourceflow-v2-import", context.TenantId + "\n" + stream.Value + "\n" + (i + 1).ToString(CultureInfo.InvariantCulture));
            MessageEnvelope envelope = runtime.CreateEnvelope(events[i], messageId: id, correlation: correlation, subject: stream.Value, tenant: context.TenantId, headers: headers) with
            {
                Time = now,
                StreamVersion = i + 1,
                Actor = Actor,
            };
            envelopes[i] = envelope;
            toAppend[i] = new EventToAppend(envelope.MessageId, envelope.Type, envelope.TypeVersion, envelope.Body, new EventMetadata
            {
                CorrelationId = envelope.CorrelationId,
                Source = envelope.Source,
                Actor = envelope.Actor,
                TraceParent = envelope.TraceParent,
                TraceState = envelope.TraceState,
                Headers = envelope.Headers,
            }, now, []);
        }

        if (runtime.PersonalData is { } personal && runtime.Storage.PersonalData is { } subjects)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < events.Length; i++)
            {
                if (personal.Protects(events[i].GetType()))
                {
                    (ReadOnlyMemory<byte> body, IReadOnlyCollection<string> subjectsUsed) = await personal.ProtectAsync(events[i].GetType(), context.TenantId, stream.Value, toAppend[i].Data, cancellationToken).ConfigureAwait(false);
                    toAppend[i] = toAppend[i] with { Data = body };
                    used.UnionWith(subjectsUsed);
                }
            }

            foreach (string subject in used)
            {
                await subjects.RecordSubjectStreamsAsync(context.TenantId, subject, [stream], cancellationToken).ConfigureAwait(false);
            }
        }

        session.Append(stream, aggregateName, ExpectedVersion.NoStream, toAppend);
        try
        {
            await using var projections = new ProjectionScope(runtime, session, _services ?? EmptyServices.Instance);
            for (int i = 0; i < events.Length && runtime.InlineProjections.Count > 0; i++)
            {
                var eventContext = new EventContext(envelopes[i].MessageId, stream, new StreamVersion(i + 1), GlobalPosition.Start, now,
                    envelopes[i].CorrelationId, null, context.TenantId == MessageEnvelope.DefaultTenant ? null : context.TenantId);
                foreach (ProjectionDescriptor projection in runtime.InlineProjections)
                {
                    await projections.ApplyAsync(projection, events[i], eventContext, cancellationToken).ConfigureAwait(false);
                }
            }

            await projections.CompleteAsync(cancellationToken).ConfigureAwait(false);
            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ConcurrencyException)
        {
            return false;   // another run imported it first
        }
    }

    // ================================================================ archive

    /// <summary>
    /// Step 4: copies the v2 command table to <c>sourceflow_v2_archive.commands</c> (<see cref="V2Source.ArchiveSchema"/>) in the v2 database, a read-only audit
    /// copy (updates and deletes are refused by a trigger). Re-running copies only rows not yet archived.
    /// </summary>
    public async Task<V2ArchiveResult> ArchiveAsync(CancellationToken cancellationToken = default)
    {
        string archive = Source.Archive;
        string schema = Source.Quote(Source.ArchiveSchema);
        string id = Q("Id");
        if (Source.Dialect == V2Dialect.PostgreSql)
        {
            await Source.ExecuteAsync($"""
                CREATE SCHEMA IF NOT EXISTS {schema};
                DO $sf$ BEGIN
                  IF to_regclass('{archive.Replace("'", "''", StringComparison.Ordinal)}') IS NULL THEN
                    CREATE TABLE {archive} AS SELECT * FROM {Source.Commands} WITH NO DATA;
                    ALTER TABLE {archive} ADD PRIMARY KEY ({id});
                  END IF;
                END $sf$;
                CREATE OR REPLACE FUNCTION {schema}.read_only() RETURNS trigger LANGUAGE plpgsql AS $sf$
                BEGIN RAISE EXCEPTION 'the v2 command archive is a read-only audit copy'; END $sf$;
                DROP TRIGGER IF EXISTS read_only ON {archive};
                CREATE TRIGGER read_only BEFORE UPDATE OR DELETE OR TRUNCATE ON {archive} FOR EACH STATEMENT EXECUTE FUNCTION {schema}.read_only();
                """, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Source.ExecuteAsync($"""
                IF SCHEMA_ID(N'{Source.ArchiveSchema.Replace("'", "''", StringComparison.Ordinal)}') IS NULL EXEC(N'CREATE SCHEMA {schema}');
                IF OBJECT_ID(N'{archive}', N'U') IS NULL
                BEGIN
                  SELECT * INTO {archive} FROM {Source.Commands} WHERE 1 = 0;
                  ALTER TABLE {archive} ADD CONSTRAINT {Source.Quote("PK_" + Source.ArchiveSchema + "_commands")} PRIMARY KEY ({id});
                END
                """, cancellationToken).ConfigureAwait(false);
            await Source.ExecuteAsync($"""
                CREATE OR ALTER TRIGGER {schema}.[commands_read_only] ON {archive} INSTEAD OF UPDATE, DELETE AS
                BEGIN THROW 51000, N'The v2 command archive is a read-only audit copy.', 1; END
                """, cancellationToken).ConfigureAwait(false);
        }

        long before = await Source.ScalarAsync($"SELECT COUNT(*) FROM {archive}", cancellationToken).ConfigureAwait(false);
        string missing = $"WHERE NOT EXISTS (SELECT 1 FROM {archive} a WHERE a.{id} = c.{id})";
        if (Source.Dialect == V2Dialect.PostgreSql)
        {
            await Source.ExecuteAsync($"INSERT INTO {archive} SELECT c.* FROM {Source.Commands} c {missing}", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            IReadOnlyList<V2Column> columns = await Source.ColumnsAsync(Source.CommandTable, cancellationToken).ConfigureAwait(false);
            string list = string.Join(", ", columns.Select(c => Q(c.Name)));
            string select = string.Join(", ", columns.Select(c => "c." + Q(c.Name)));
            await Source.ExecuteAsync($"""
                IF OBJECTPROPERTY(OBJECT_ID(N'{archive}'), 'TableHasIdentity') = 1 SET IDENTITY_INSERT {archive} ON;
                INSERT INTO {archive} ({list}) SELECT {select} FROM {Source.Commands} c {missing};
                IF OBJECTPROPERTY(OBJECT_ID(N'{archive}'), 'TableHasIdentity') = 1 SET IDENTITY_INSERT {archive} OFF;
                """, cancellationToken).ConfigureAwait(false);
        }

        long after = await Source.ScalarAsync($"SELECT COUNT(*) FROM {archive}", cancellationToken).ConfigureAwait(false);
        long source = await Source.ScalarAsync($"SELECT COUNT(*) FROM {Source.Commands}", cancellationToken).ConfigureAwait(false);
        return new V2ArchiveResult(after - before, after, source);
    }

    // ================================================================ verify

    /// <summary>
    /// Step 6: compares the rebuilt v3 state of every v2 entity with its v2 row through
    /// <see cref="IV2Import{TV2Entity, TAggregate, TId}.Compare"/>. A missing stream is a mismatch.
    /// </summary>
    public async Task<V2VerifyReport> VerifyAsync(string? tenantId = null, int batchSize = 1000, CancellationToken cancellationToken = default)
    {
        SourceFlowRuntime runtime = RequireRuntime("verify");
        IAggregateReader reader = (_services ?? throw new InvalidOperationException("'verify' needs the service.")).GetRequiredService<IAggregateReader>();
        string tenant = tenantId ?? runtime.Options.DefaultTenant;
        var mismatches = new List<V2Mismatch>();
        long checkedCount = 0;
        foreach (V2ImportRegistration import in _settings.Imports)
        {
            int after = int.MinValue;
            while (true)
            {
                IReadOnlyList<object> rows = await import.PageAsync(Source, after, batchSize, cancellationToken).ConfigureAwait(false);
                if (rows.Count == 0)
                {
                    break;
                }

                foreach (object row in rows)
                {
                    checkedCount++;
                    if (await import.CompareAsync(reader, row, tenant, cancellationToken).ConfigureAwait(false) is { } mismatch)
                    {
                        mismatches.Add(mismatch);
                    }
                }

                after = import.IdOf(rows[^1]);
            }
        }

        return new V2VerifyReport(checkedCount, mismatches);
    }

    // ================================================================ plumbing

    private SourceFlowRuntime RequireRuntime(string step)
    {
        return _runtime ?? throw new InvalidOperationException($"'migrate v2 {step}' needs the service's model and imports: run it inside the service with sf.MigrateFromV2(…).");
    }

    private string Q(string identifier)
    {
        return Source.Quote(identifier);
    }

    private static long Long(DbDataReader row, int ordinal)
    {
        return row.IsDBNull(ordinal) ? 0 : Convert.ToInt64(row.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private async IAsyncEnumerable<DbDataReader> QueryAsync(string sql, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        DbConnection connection = await Source.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            DbCommand command = V2Source.Command(connection, sql, parameters);
            await using (command.ConfigureAwait(false))
            {
                DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        yield return reader;
                    }
                }
            }
        }
    }

    internal static string Identifier(string name)
    {
        var builder = new StringBuilder(name.Length);
        bool upper = true;
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
            else
            {
                upper = true;
            }
        }

        if (builder.Length == 0 || char.IsDigit(builder[0]))
        {
            builder.Insert(0, '_');
        }

        return builder.ToString();
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();

        public object? GetService(Type serviceType)
        {
            return null;
        }
    }
}

/// <summary>Options of <see cref="V2Migration.ImportAsync"/>.</summary>
public sealed record V2ImportOptions
{
    /// <summary>v2 rows read per query (<c>--batch</c>).</summary>
    public int BatchSize { get; init; } = 1000;

    /// <summary>Import at most this many new streams in this run, for waves (<c>--limit</c>); null for all.</summary>
    public long? Limit { get; init; }

    /// <summary>The v3 tenant; null for the default tenant.</summary>
    public string? TenantId { get; init; }
}

/// <summary>The result of an import run.</summary>
/// <param name="Tables">Per v2 table.</param>
/// <param name="Complete">False when <see cref="V2ImportOptions.Limit"/> stopped the run early; run again to continue.</param>
public sealed record V2ImportResult(IReadOnlyList<V2ImportTableResult> Tables, bool Complete)
{
    /// <summary>Streams created by this run.</summary>
    public long Imported => Tables.Sum(t => t.Imported);

    /// <summary>Streams that already existed (an earlier run imported them).</summary>
    public long Skipped => Tables.Sum(t => t.Skipped);
}

/// <summary>The import of one v2 table.</summary>
public sealed record V2ImportTableResult(string Table, string Aggregate, long Imported, long Skipped);

/// <summary>The migration plan (<c>migrate v2 plan</c>).</summary>
/// <param name="Entities">Per imported table: rows (streams) and their commands.</param>
/// <param name="CommandTypes">Per v2 command type.</param>
/// <param name="Commands">Every v2 command.</param>
/// <param name="EntityIdZero">Commands logged under entity id 0 (GAP-20): their history can't be attributed to an entity.</param>
/// <param name="NotRebuildable">Commands v2 could not rebuild (GAP-04): empty payloads and types declared not rebuildable.</param>
/// <param name="TablesWithoutImport">v2 tables with neither an import nor <c>Ignore</c>.</param>
/// <param name="EstimatedDuration">The estimated import time.</param>
public sealed record V2Plan(
    IReadOnlyList<V2PlanEntity> Entities,
    IReadOnlyList<V2PlanCommandType> CommandTypes,
    long Commands,
    long EntityIdZero,
    long NotRebuildable,
    IReadOnlyList<string> TablesWithoutImport,
    TimeSpan EstimatedDuration)
{
    /// <summary>True when every v2 table has an import or is ignored.</summary>
    public bool Ready => TablesWithoutImport.Count == 0;
}

/// <summary>An imported v2 table in the plan.</summary>
public sealed record V2PlanEntity(string Table, string Aggregate, string Import, long Rows, long Commands);

/// <summary>A v2 command type in the plan.</summary>
public sealed record V2PlanCommandType(string CommandType, long Commands, long EntityIdZero, long NotRebuildable);

/// <summary>The result of <c>migrate v2 archive</c>.</summary>
/// <param name="Copied">Rows copied by this run.</param>
/// <param name="Archived">Rows in the archive.</param>
/// <param name="Source">Rows in the v2 command table.</param>
public sealed record V2ArchiveResult(long Copied, long Archived, long Source);

/// <summary>The result of <c>migrate v2 verify</c>.</summary>
/// <param name="Checked">v2 entities compared.</param>
/// <param name="Mismatches">Entities whose v3 state differs, or that have no stream.</param>
public sealed record V2VerifyReport(long Checked, IReadOnlyList<V2Mismatch> Mismatches)
{
    /// <summary>True when every entity matches.</summary>
    public bool Passed => Mismatches.Count == 0;
}

/// <summary>A v2 entity whose v3 state differs.</summary>
public sealed record V2Mismatch(string Table, int V2Id, string Stream, IReadOnlyList<string> Differences);

/// <summary>A generated source file.</summary>
public sealed record V2ScaffoldFile(string FileName, string Content);

/// <summary>v2 assembly-qualified type names.</summary>
public static partial class V2TypeNames
{
    /// <summary>
    /// The name without assembly version, culture and public key token (also inside generic arguments), with single
    /// spaces after commas: <c>MyApp.Commands.CreateAccount, MyApp</c>.
    /// </summary>
    public static string Normalize(string assemblyQualifiedName)
    {
        ArgumentNullException.ThrowIfNull(assemblyQualifiedName);
        string stripped = AssemblyDetails().Replace(assemblyQualifiedName, string.Empty);
        return Commas().Replace(stripped.Trim(), ", ");
    }

    [GeneratedRegex(@",\s*(Version|Culture|PublicKeyToken)=[^,\]]*", RegexOptions.CultureInvariant)]
    private static partial Regex AssemblyDetails();

    [GeneratedRegex(@"\s*,\s*", RegexOptions.CultureInvariant)]
    private static partial Regex Commas();
}

internal abstract class V2ImportRegistration
{
    public abstract string Table { get; }

    public abstract string Aggregate { get; }

    public abstract string ImportType { get; }

    public abstract bool OwnsCommand(string commandType);

    public abstract int IdOf(object row);

    public abstract Task<IReadOnlyList<object>> PageAsync(V2Source source, int after, int size, CancellationToken cancellationToken);

    public abstract ValueTask<bool> ImportAsync(V2Migration migration, SourceFlowRuntime runtime, object row, Dictionary<int, (int Count, int Last)> stats, string tenant, CancellationToken cancellationToken);

    public abstract ValueTask<V2Mismatch?> CompareAsync(IAggregateReader reader, object row, string tenant, CancellationToken cancellationToken);
}

internal sealed class V2ImportRegistration<TV2Entity, TAggregate, TId>(IV2Import<TV2Entity, TAggregate, TId> import) : V2ImportRegistration
    where TV2Entity : IV2Entity<TV2Entity>
    where TAggregate : class, IAggregateRuntime<TAggregate, TId>
    where TId : struct, IStronglyTypedId<TId>
{
    public override string Table => TV2Entity.TableName;

    public override string Aggregate => TAggregate.AggregateName;

    public override string ImportType => import.GetType().Name;

    public override bool OwnsCommand(string commandType)
    {
        return import.OwnsCommand(commandType);
    }

    public override int IdOf(object row)
    {
        return ((TV2Entity)row).Id;
    }

    public override async Task<IReadOnlyList<object>> PageAsync(V2Source source, int after, int size, CancellationToken cancellationToken)
    {
        var rows = new List<object>(size);
        DbConnection connection = await source.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            DbCommand command = V2Source.Command(connection, source.Page(source.Table(Table), size), ("@after", after));
            await using (command.ConfigureAwait(false))
            {
                DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    var row = new V2Row(reader);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        rows.Add(TV2Entity.Read(row));
                    }
                }
            }
        }

        return rows;
    }

    public override async ValueTask<bool> ImportAsync(V2Migration migration, SourceFlowRuntime runtime, object row, Dictionary<int, (int Count, int Last)> stats, string tenant, CancellationToken cancellationToken)
    {
        var entity = (TV2Entity)row;
        StreamId stream = StreamId.For(TAggregate.AggregateName, import.MapId(entity));
        (int count, int last) = stats.GetValueOrDefault(entity.Id);
        var context = new V2ImportContext
        {
            ImportedAt = runtime.Time.GetUtcNow(),
            V2Id = entity.Id,
            CommandCount = count,
            LastSequenceNo = last,
            TenantId = tenant,
        };
        return await migration.AppendGenesisAsync(runtime, TAggregate.AggregateName, stream, [.. import.ToEvents(entity, context)], context, Table, cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask<V2Mismatch?> CompareAsync(IAggregateReader reader, object row, string tenant, CancellationToken cancellationToken)
    {
        var entity = (TV2Entity)row;
        TId id = import.MapId(entity);
        StreamId stream = StreamId.For(TAggregate.AggregateName, id);
        AggregateSnapshot<TAggregate> state = await reader.LoadAsync<TAggregate, TId>(id, tenantId: tenant, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (state.Version == StreamVersion.None)
        {
            return new V2Mismatch(Table, entity.Id, stream.Value, ["the v3 stream has no events (not imported)"]);
        }

        string[] differences = [.. import.Compare(entity, state.State)];
        return differences.Length == 0 ? null : new V2Mismatch(Table, entity.Id, stream.Value, differences);
    }
}

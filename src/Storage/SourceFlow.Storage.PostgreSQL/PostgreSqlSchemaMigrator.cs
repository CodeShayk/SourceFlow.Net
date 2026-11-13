using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace SourceFlow.Storage.PostgreSQL;

/// <summary>A versioned migration script embedded in the package (doc 05 §11).</summary>
/// <param name="Version">The script version (V001 → 1).</param>
/// <param name="Name">The script name, for example <c>initial</c>.</param>
/// <param name="Sql">The SQL with the <c>{schema}</c> placeholder.</param>
public sealed record MigrationScript(int Version, string Name, string Sql)
{
    /// <summary>SHA-256 of the script with normalised line endings; an applied script must never change.</summary>
    public string Checksum => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Sql.ReplaceLineEndings("\n"))));
}

/// <summary>A migration could not be applied, or the database schema does not match this package.</summary>
public sealed class SchemaMigrationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SchemaMigrationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Applies the embedded SQL scripts in version order, recording each in <c>{schema}.schema_history</c>. A transaction
/// advisory lock serialises concurrent migrators; every pending script runs in one transaction, so a failure applies
/// nothing. Applied scripts are checksum-verified (doc 05 §11; fixes EF-06: no <c>IF NOT EXISTS</c> on the application
/// tables and no swallowed errors).
/// </summary>
public sealed class PostgreSqlSchemaMigrator
{
    private const string ResourcePrefix = "SourceFlow.Storage.PostgreSQL.Schema.";
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;
    private readonly TenantIsolation? _isolation;
    private readonly bool _partitionEvents;

    /// <summary>Creates a migrator for the embedded scripts.</summary>
    /// <param name="dataSource">A connection with DDL rights (the schema owner).</param>
    /// <param name="schema">The schema name.</param>
    /// <param name="isolation">
    /// The row-level security state to apply to the tenant tables and to verify (ADR-022); null leaves it unchanged.
    /// </param>
    /// <param name="partitionEvents">
    /// Converts the events table to monthly partitions, once, keeping its rows (doc 05 §9), and verifies that it is
    /// partitioned. An already partitioned table is accepted either way.
    /// </param>
    public PostgreSqlSchemaMigrator(NpgsqlDataSource dataSource, string schema = "sourceflow", TenantIsolation? isolation = null, bool partitionEvents = false)
        : this(dataSource, schema, EmbeddedScripts, isolation, partitionEvents)
    {
    }

    internal PostgreSqlSchemaMigrator(NpgsqlDataSource dataSource, string schema, IReadOnlyList<MigrationScript> scripts, TenantIsolation? isolation = null, bool partitionEvents = false)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        new PostgreSqlStorageOptions { ConnectionString = "-", Schema = schema }.Validate();
        _schema = schema;
        _isolation = isolation;
        _partitionEvents = partitionEvents;
        Scripts = [.. scripts.OrderBy(s => s.Version)];
    }

    /// <summary>The tables protected by the <c>tenant_isolation</c> policy (V002).</summary>
    public static IReadOnlyList<string> TenantTables { get; } = ["streams", "events", "snapshots", "documents", "process_instances", "subject_streams"];

    /// <summary>The scripts embedded in this package, ordered by version.</summary>
    public static IReadOnlyList<MigrationScript> EmbeddedScripts { get; } = LoadEmbedded();

    /// <summary>The scripts this migrator applies.</summary>
    public IReadOnlyList<MigrationScript> Scripts { get; }

    /// <summary>The latest script version.</summary>
    public int LatestVersion => Scripts.Count == 0 ? 0 : Scripts[^1].Version;

    /// <summary>Applies every pending script; returns the versions applied (empty when up to date).</summary>
    /// <exception cref="SchemaMigrationException">An applied script was modified.</exception>
    public async Task<IReadOnlyList<int>> MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, LockSql(), cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, BootstrapSql(), cancellationToken).ConfigureAwait(false);

        Dictionary<int, string> applied = await ReadHistoryAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        VerifyChecksums(applied);
        var done = new List<int>();
        foreach (MigrationScript script in Scripts.Where(s => !applied.ContainsKey(s.Version)))
        {
            try
            {
                await ExecuteAsync(connection, transaction, Render(script.Sql), cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception)
            {
                throw new SchemaMigrationException($"Migration V{script.Version:D3}__{script.Name} failed: {exception.MessageText}");
            }

            await using var record = new NpgsqlCommand($"INSERT INTO {Quoted}.schema_history (version, name, checksum) VALUES (@v, @n, @c)", connection, transaction);
            record.Parameters.Add(new NpgsqlParameter<int>("v", script.Version));
            record.Parameters.Add(new NpgsqlParameter<string>("n", script.Name));
            record.Parameters.Add(new NpgsqlParameter<string>("c", script.Checksum));
            await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            done.Add(script.Version);
        }

        if (_partitionEvents)
        {
            try
            {
                await ExecuteAsync(connection, transaction, PartitionSql(), cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception)
            {
                throw new SchemaMigrationException($"Partitioning the events table of schema '{_schema}' failed: {exception.MessageText}");
            }
        }

        // ALTER TABLE takes an ACCESS EXCLUSIVE lock, which waits behind every open reader of the table: alter only the
        // tables whose row-level security differs, so a node starting next to busy ones does not queue behind them.
        if (_isolation is { } wanted)
        {
            IReadOnlyList<string> differ = await MismatchedIsolationAsync(connection, transaction, wanted == TenantIsolation.RowLevelSecurity, cancellationToken).ConfigureAwait(false);
            if (IsolationSql(differ) is { } isolation)
            {
                await ExecuteAsync(connection, transaction, isolation, cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return done;
    }

    /// <summary>
    /// Verifies that the schema is installed and at least at <see cref="LatestVersion"/>, without changing it. A newer
    /// schema is accepted (rolling upgrades: migrations are additive).
    /// </summary>
    /// <exception cref="SchemaMigrationException">The schema is missing, out of date or modified.</exception>
    public async Task VerifyAsync(CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var exists = new NpgsqlCommand("SELECT to_regclass(@t) IS NOT NULL", connection))
        {
            exists.Parameters.Add(new NpgsqlParameter<string>("t", _schema + ".schema_history"));
            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                throw new SchemaMigrationException($"The SourceFlow schema '{_schema}' is not installed. Run 'dotnet sourceflow db migrate', apply 'db script' output, or enable AutoMigrate.");
            }
        }

        Dictionary<int, string> applied = await ReadHistoryAsync(connection, null, cancellationToken).ConfigureAwait(false);
        VerifyChecksums(applied);
        int[] missing = [.. Scripts.Where(s => !applied.ContainsKey(s.Version)).Select(s => s.Version)];
        if (missing.Length > 0)
        {
            throw new SchemaMigrationException($"The SourceFlow schema '{_schema}' is missing migrations {string.Join(", ", missing.Select(v => "V" + v.ToString("D3", CultureInfo.InvariantCulture)))}. Run 'dotnet sourceflow db migrate'.");
        }

        if (_isolation is { } isolation)
        {
            await VerifyIsolationAsync(connection, isolation == TenantIsolation.RowLevelSecurity, cancellationToken).ConfigureAwait(false);
        }

        if (_partitionEvents)
        {
            await using var partitioned = new NpgsqlCommand(PostgreSqlPartitioning.IsPartitioned, connection);
            partitioned.Parameters.Add(new NpgsqlParameter<string>("schema", _schema));
            if (await partitioned.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                throw new SchemaMigrationException($"PartitionEvents is set, but the events table of schema '{_schema}' is not partitioned. Run 'dotnet sourceflow db migrate' with PartitionEvents.");
            }
        }
    }

    // A mismatch silently breaks tenancy: RLS on without the tenant setting shows nothing; RLS off loses the guard.
    private async Task VerifyIsolationAsync(NpgsqlConnection connection, bool expected, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> mismatched = await MismatchedIsolationAsync(connection, null, expected, cancellationToken).ConfigureAwait(false);
        if (mismatched.Count > 0)
        {
            string state = expected ? "enabled and forced" : "disabled";
            string mode = expected ? "RowLevelSecurity" : "Runtime";
            throw new SchemaMigrationException(
                $"Row-level security must be {state} for TenantIsolation.{mode}, but is not on {string.Join(", ", mismatched.Order(StringComparer.Ordinal))} in schema '{_schema}'. Run 'dotnet sourceflow db migrate' with the same TenantIsolation.");
        }
    }

    // The tenant tables whose row-level security is not in the expected state (enabled and forced, or neither).
    private async Task<IReadOnlyList<string>> MismatchedIsolationAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, bool expected, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace "
            + "WHERE n.nspname = @schema AND c.relname = ANY(@tables) AND (c.relrowsecurity, c.relforcerowsecurity) <> (@expected, @expected)",
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<string>("schema", _schema));
        command.Parameters.Add(new NpgsqlParameter<string[]>("tables", [.. TenantTables]));
        command.Parameters.Add(new NpgsqlParameter<bool>("expected", expected));
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var mismatched = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            mismatched.Add(reader.GetString(0));
        }

        return mismatched;
    }

    /// <summary>The applied versions, ascending (empty when the schema is not installed).</summary>
    public async Task<IReadOnlyList<int>> GetAppliedVersionsAsync(CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var exists = new NpgsqlCommand("SELECT to_regclass(@t) IS NOT NULL", connection);
        exists.Parameters.Add(new NpgsqlParameter<string>("t", _schema + ".schema_history"));
        if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            return [];
        }

        return [.. (await ReadHistoryAsync(connection, null, cancellationToken).ConfigureAwait(false)).Keys.Order()];
    }

    /// <summary>
    /// The SQL that brings a schema at <paramref name="fromVersion"/> to the latest version, for DBAs (<c>db script</c>).
    /// It takes the same advisory lock and records the same history rows as <see cref="MigrateAsync"/>.
    /// </summary>
    public string Script(int fromVersion = 0)
    {
        var sql = new StringBuilder();
        sql.Append("BEGIN;\n").Append(LockSql()).Append(";\n").Append(BootstrapSql()).Append('\n');
        foreach (MigrationScript script in Scripts.Where(s => s.Version > fromVersion))
        {
            sql.Append(CultureInfo.InvariantCulture, $"\n-- V{script.Version:D3}__{script.Name}\n");
            sql.Append(Render(script.Sql).TrimEnd()).Append('\n');
            sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO {Quoted}.schema_history (version, name, checksum) VALUES ({script.Version}, '{script.Name}', '{script.Checksum}');\n");
        }

        if (IsolationSql(TenantTables) is { } isolation)
        {
            sql.Append("\n-- tenant isolation (ADR-022)\n").Append(isolation);
        }

        if (_partitionEvents)
        {
            sql.Append("\n-- partitioned events (doc 05 §9): converts once, then creates the partitions ahead\n").Append(PartitionSql());
        }

        return sql.Append("COMMIT;\n").ToString();
    }

    /// <summary>
    /// Least-privilege grants for the runtime role (doc 10 §1, <c>db script --grants</c>): DML on the SourceFlow tables, but
    /// <c>events</c> (and every events partition) is append-only, so the runtime can't edit or delete history. The
    /// migration role (the owner) keeps the DDL. Run it after every migration that adds tables.
    /// </summary>
    /// <param name="role">The runtime role.</param>
    /// <remarks>
    /// With <c>PartitionEvents</c>, the runtime role can't create partitions: run <c>db partitions ensure</c> as the owner
    /// on a schedule (the default privileges below make the new partitions append-only for the role as well).
    /// </remarks>
    public string GrantsScript(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        string r = "\"" + role.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        string roleLiteral = role.Replace("'", "''", StringComparison.Ordinal);
        return $"""
            -- Least-privilege grants for the SourceFlow runtime role {r} on {Quoted} (doc 10 §1). Re-run after migrations.
            BEGIN;
            GRANT USAGE ON SCHEMA {Quoted} TO {r};
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {Quoted} TO {r};
            GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA {Quoted} TO {r};
            GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA {Quoted} TO {r};
            -- Events are append-only: no UPDATE, DELETE or TRUNCATE on the table or any of its partitions.
            DO $sf$
            DECLARE t regclass;
            BEGIN
              FOR t IN SELECT '{Quoted.Replace("'", "''", StringComparison.Ordinal)}.events'::regclass
                       UNION SELECT inhrelid::regclass FROM pg_inherits WHERE inhparent = '{Quoted.Replace("'", "''", StringComparison.Ordinal)}.events'::regclass
              LOOP
                EXECUTE format('REVOKE UPDATE, DELETE, TRUNCATE ON %s FROM %I', t, '{roleLiteral}');
              END LOOP;
            END $sf$;
            -- Tables the owner creates later (events partitions) are append-only for the role too.
            ALTER DEFAULT PRIVILEGES IN SCHEMA {Quoted} GRANT SELECT, INSERT ON TABLES TO {r};
            COMMIT;
            """;
    }

    // FORCE makes the policies apply to the table owner too; only superusers and BYPASSRLS roles skip them.
    private string? IsolationSql(IReadOnlyList<string> tables)
    {
        if (_isolation is not { } isolation || tables.Count == 0)
        {
            return null;
        }

        string action = isolation == TenantIsolation.RowLevelSecurity
            ? "ENABLE ROW LEVEL SECURITY, FORCE ROW LEVEL SECURITY"
            : "DISABLE ROW LEVEL SECURITY, NO FORCE ROW LEVEL SECURITY";
        return string.Concat(tables.Select(t => $"ALTER TABLE {Quoted}.{t} {action};\n"));
    }

    private string Quoted => "\"" + _schema + "\"";

    private string PartitionSql()
    {
        return Render(PostgreSqlPartitioning.Convert).TrimEnd() + "\n"
            + $"SELECT {Quoted}.sf_ensure_event_partitions(now(), {PostgreSqlPartitioning.MigratorMonthsAhead});\n";
    }

    private static IReadOnlyList<MigrationScript> LoadEmbedded()
    {
        Assembly assembly = typeof(PostgreSqlSchemaMigrator).Assembly;
        var scripts = new List<MigrationScript>();
        foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal)))
        {
            // V001__initial.sql
            string file = resource[ResourcePrefix.Length..^4];
            int separator = file.IndexOf("__", StringComparison.Ordinal);
            if (file[0] != 'V' || separator < 2 || !int.TryParse(file.AsSpan(1, separator - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int version))
            {
                throw new InvalidOperationException($"Embedded migration '{resource}' is not named V{{nnn}}__{{name}}.sql.");
            }

            using Stream stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            scripts.Add(new MigrationScript(version, file[(separator + 2)..], reader.ReadToEnd()));
        }

        return [.. scripts.OrderBy(s => s.Version)];
    }

    private string Render(string sql)
    {
        return sql.Replace("{schema}", Quoted, StringComparison.Ordinal);
    }

    private string LockSql()
    {
        // One advisory lock per schema; transaction-scoped, so it is released by COMMIT or ROLLBACK.
        return $"SELECT pg_advisory_xact_lock(hashtextextended('sourceflow.migrate:{_schema}', 0))";
    }

    private string BootstrapSql()
    {
        return $"""
            CREATE SCHEMA IF NOT EXISTS {Quoted};
            CREATE TABLE IF NOT EXISTS {Quoted}.schema_history (
                version    int         PRIMARY KEY,
                name       text        NOT NULL,
                checksum   text        NOT NULL,
                applied_at timestamptz NOT NULL DEFAULT now());
            """;
    }

    private void VerifyChecksums(Dictionary<int, string> applied)
    {
        foreach (MigrationScript script in Scripts)
        {
            if (applied.TryGetValue(script.Version, out string? checksum) && checksum != script.Checksum)
            {
                throw new SchemaMigrationException($"Migration V{script.Version:D3}__{script.Name} was modified after it was applied to schema '{_schema}' (checksum {checksum} ≠ {script.Checksum}). Applied migrations are immutable; add a new script instead.");
            }
        }
    }

    private async Task<Dictionary<int, string>> ReadHistoryAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT version, checksum FROM {Quoted}.schema_history", connection, transaction);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var applied = new Dictionary<int, string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            applied[reader.GetInt32(0)] = reader.GetString(1);
        }

        return applied;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

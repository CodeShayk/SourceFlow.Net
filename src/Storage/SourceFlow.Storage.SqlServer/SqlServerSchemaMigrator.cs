using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SourceFlow.Storage.SqlServer;

/// <summary>A versioned migration script embedded in the package (doc 05 §11).</summary>
/// <param name="Version">The script version (V001 → 1).</param>
/// <param name="Name">The script name, for example <c>initial</c>.</param>
/// <param name="Sql">The T-SQL with the <c>{schema}</c> placeholder; <c>GO</c> lines separate batches.</param>
public sealed record SqlServerMigrationScript(int Version, string Name, string Sql)
{
    /// <summary>SHA-256 of the script with normalised line endings; an applied script must never change.</summary>
    public string Checksum => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Sql.ReplaceLineEndings("\n"))));
}

/// <summary>A migration could not be applied, or the database schema does not match this package.</summary>
public sealed class SqlServerSchemaException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SqlServerSchemaException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Applies the embedded T-SQL scripts in version order, recording each in <c>{schema}.schema_history</c>. An exclusive
/// application lock (<c>sp_getapplock</c>, transaction-owned) serialises concurrent migrators; every pending script runs in
/// one transaction, so a failure applies nothing. Applied scripts are checksum-verified (doc 05 §11).
/// </summary>
public sealed partial class SqlServerSchemaMigrator
{
    private const string ResourcePrefix = "SourceFlow.Storage.SqlServer.Schema.";
    private readonly string _connectionString;
    private readonly string _schema;

    /// <summary>Creates a migrator for the embedded scripts.</summary>
    /// <param name="connectionString">A connection with DDL rights (the schema owner).</param>
    /// <param name="schema">The schema name.</param>
    public SqlServerSchemaMigrator(string connectionString, string schema = "sourceflow")
        : this(connectionString, schema, EmbeddedScripts)
    {
    }

    internal SqlServerSchemaMigrator(string connectionString, string schema, IReadOnlyList<SqlServerMigrationScript> scripts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        new SqlServerStorageOptions { ConnectionString = connectionString, Schema = schema }.Validate();
        _connectionString = connectionString;
        _schema = schema;
        Scripts = [.. scripts.OrderBy(s => s.Version)];
    }

    /// <summary>The scripts embedded in this package, ordered by version.</summary>
    public static IReadOnlyList<SqlServerMigrationScript> EmbeddedScripts { get; } = LoadEmbedded();

    /// <summary>The scripts this migrator applies.</summary>
    public IReadOnlyList<SqlServerMigrationScript> Scripts { get; }

    /// <summary>The latest script version.</summary>
    public int LatestVersion => Scripts.Count == 0 ? 0 : Scripts[^1].Version;

    private string Quoted => "[" + _schema + "]";

    /// <summary>Applies every pending script; returns the versions applied (empty when up to date).</summary>
    /// <exception cref="SqlServerSchemaException">An applied script was modified, or a script failed.</exception>
    public async Task<IReadOnlyList<int>> MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, LockSql(), cancellationToken).ConfigureAwait(false);
        foreach (string batch in BootstrapBatches())
        {
            await ExecuteAsync(connection, transaction, batch, cancellationToken).ConfigureAwait(false);
        }

        Dictionary<int, string> applied = await ReadHistoryAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        VerifyChecksums(applied);
        var done = new List<int>();
        foreach (SqlServerMigrationScript script in Scripts.Where(s => !applied.ContainsKey(s.Version)))
        {
            try
            {
                foreach (string batch in Batches(Render(script.Sql)))
                {
                    await ExecuteAsync(connection, transaction, batch, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (SqlException exception)
            {
                throw new SqlServerSchemaException($"Migration V{script.Version:D3}__{script.Name} failed: {exception.Message}");
            }

            await using var record = new SqlCommand($"INSERT INTO {Quoted}.schema_history (version, name, checksum) VALUES (@v, @n, @c)", connection, transaction);
            record.Parameters.AddWithValue("@v", script.Version);
            record.Parameters.AddWithValue("@n", script.Name);
            record.Parameters.AddWithValue("@c", script.Checksum);
            await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            done.Add(script.Version);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return done;
    }

    /// <summary>
    /// Verifies that the schema is installed and at least at <see cref="LatestVersion"/>, without changing it. A newer
    /// schema is accepted (rolling upgrades: migrations are additive).
    /// </summary>
    /// <exception cref="SqlServerSchemaException">The schema is missing, out of date or modified.</exception>
    public async Task VerifyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (!await HistoryExistsAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            throw new SqlServerSchemaException($"The SourceFlow schema '{_schema}' is not installed. Run the migrator, apply its script, or enable AutoMigrate.");
        }

        Dictionary<int, string> applied = await ReadHistoryAsync(connection, null, cancellationToken).ConfigureAwait(false);
        VerifyChecksums(applied);
        int[] missing = [.. Scripts.Where(s => !applied.ContainsKey(s.Version)).Select(s => s.Version)];
        if (missing.Length > 0)
        {
            throw new SqlServerSchemaException($"The SourceFlow schema '{_schema}' is missing migrations {string.Join(", ", missing.Select(v => "V" + v.ToString("D3", CultureInfo.InvariantCulture)))}. Run the migrator.");
        }
    }

    /// <summary>The applied versions, ascending (empty when the schema is not installed).</summary>
    public async Task<IReadOnlyList<int>> GetAppliedVersionsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await HistoryExistsAsync(connection, cancellationToken).ConfigureAwait(false)
            ? [.. (await ReadHistoryAsync(connection, null, cancellationToken).ConfigureAwait(false)).Keys.Order()]
            : [];
    }

    /// <summary>
    /// The T-SQL that brings a schema at <paramref name="fromVersion"/> to the latest version, for DBAs: one transaction
    /// with the same application lock and history rows as <see cref="MigrateAsync"/>, batches separated by <c>GO</c>.
    /// </summary>
    public string Script(int fromVersion = 0)
    {
        var sql = new StringBuilder();
        sql.Append("SET XACT_ABORT ON;\nBEGIN TRANSACTION;\n").Append(LockSql()).Append(";\n");
        foreach (string batch in BootstrapBatches())
        {
            sql.Append(batch).Append("\nGO\n");
        }

        foreach (SqlServerMigrationScript script in Scripts.Where(s => s.Version > fromVersion))
        {
            sql.Append(CultureInfo.InvariantCulture, $"\n-- V{script.Version:D3}__{script.Name}\n");
            foreach (string batch in Batches(Render(script.Sql)))
            {
                sql.Append(batch.TrimEnd()).Append("\nGO\n");
            }

            sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO {Quoted}.schema_history (version, name, checksum) VALUES ({script.Version}, N'{script.Name}', N'{script.Checksum}');\nGO\n");
        }

        return sql.Append("COMMIT TRANSACTION;\n").ToString();
    }

    private static IReadOnlyList<SqlServerMigrationScript> LoadEmbedded()
    {
        Assembly assembly = typeof(SqlServerSchemaMigrator).Assembly;
        var scripts = new List<SqlServerMigrationScript>();
        foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal)))
        {
            string file = resource[ResourcePrefix.Length..^4];
            int separator = file.IndexOf("__", StringComparison.Ordinal);
            if (file[0] != 'V' || separator < 2 || !int.TryParse(file.AsSpan(1, separator - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int version))
            {
                throw new InvalidOperationException($"Embedded migration '{resource}' is not named V{{nnn}}__{{name}}.sql.");
            }

            using Stream stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            scripts.Add(new SqlServerMigrationScript(version, file[(separator + 2)..], reader.ReadToEnd()));
        }

        return [.. scripts.OrderBy(s => s.Version)];
    }

    // Batches are separated by lines holding only GO (CREATE FUNCTION and friends must start a batch).
    private static IEnumerable<string> Batches(string sql)
    {
        return GoSeparator().Split(sql.ReplaceLineEndings("\n")).Where(b => !string.IsNullOrWhiteSpace(b));
    }

    private string Render(string sql)
    {
        return sql.Replace("{schema}", Quoted, StringComparison.Ordinal);
    }

    private string LockSql()
    {
        return $"EXEC sp_getapplock @Resource = N'sourceflow.migrate:{_schema}', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = -1";
    }

    private IEnumerable<string> BootstrapBatches()
    {
        yield return $"IF SCHEMA_ID(N'{_schema}') IS NULL EXEC(N'CREATE SCHEMA {Quoted}');";
        yield return $"""
            IF OBJECT_ID(N'{Quoted}.schema_history', N'U') IS NULL
            CREATE TABLE {Quoted}.schema_history (
                version    int               NOT NULL CONSTRAINT pk_schema_history PRIMARY KEY,
                name       nvarchar(200)     NOT NULL,
                checksum   nvarchar(64)      NOT NULL,
                applied_at datetimeoffset(7) NOT NULL CONSTRAINT df_schema_history_applied DEFAULT SYSDATETIMEOFFSET());
            """;
    }

    private async Task<bool> HistoryExistsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var exists = new SqlCommand("SELECT CASE WHEN OBJECT_ID(@t, N'U') IS NULL THEN 0 ELSE 1 END", connection);
        exists.Parameters.AddWithValue("@t", Quoted + ".schema_history");
        return (int)(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 1;
    }

    private void VerifyChecksums(Dictionary<int, string> applied)
    {
        foreach (SqlServerMigrationScript script in Scripts)
        {
            if (applied.TryGetValue(script.Version, out string? checksum) && checksum != script.Checksum)
            {
                throw new SqlServerSchemaException($"Migration V{script.Version:D3}__{script.Name} was modified after it was applied to schema '{_schema}' (checksum {checksum} ≠ {script.Checksum}). Applied migrations are immutable; add a new script instead.");
            }
        }
    }

    private async Task<Dictionary<int, string>> ReadHistoryAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand($"SELECT version, checksum FROM {Quoted}.schema_history", connection, transaction);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var applied = new Dictionary<int, string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            applied[reader.GetInt32(0)] = reader.GetString(1);
        }

        return applied;
    }

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GoSeparator();
}

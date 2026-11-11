using System.Security.Cryptography;
using System.Text;
using Npgsql;
using SourceFlow.Query;

namespace SourceFlow.Storage.PostgreSQL.Query;

/// <summary>One managed index (doc 14 §7.2).</summary>
/// <param name="Name">The index name: <c>sfq_</c> + a hash of the definition, so a changed definition is a new index.</param>
/// <param name="Table">The table (<c>events</c> or <c>documents</c>).</param>
/// <param name="Source">What it serves, for example <c>bank.account.opened.customerId</c>.</param>
/// <param name="Sql">The <c>CREATE INDEX</c> statement.</param>
public sealed record QueryIndex(string Name, string Table, string Source, string Sql);

/// <summary>What an index sync did or would do.</summary>
/// <param name="Create">Indexes to create (missing).</param>
/// <param name="Drop">Managed indexes no longer in the catalog.</param>
/// <param name="Unchanged">Indexes already present.</param>
public sealed record QueryIndexPlan(IReadOnlyList<QueryIndex> Create, IReadOnlyList<string> Drop, IReadOnlyList<string> Unchanged);

/// <summary>
/// Index sync for SFQL (doc 14 §7.2; <c>dotnet sourceflow db index sync</c>): one expression index per <c>[Queryable]</c>
/// field, a partial index per event type over <c>events</c> and per read-model generation over <c>documents</c>. The
/// expression is exactly the one the SFQL compiler emits, so PostgreSQL matches it; indexes follow the schema and are never
/// written by hand. Timestamp fields are skipped: the text-to-timestamptz cast isn't immutable, so it can't be indexed.
/// </summary>
public static class PostgreSqlIndexSync
{
    /// <summary>The prefix of managed index names; sync drops managed indexes the catalog no longer needs.</summary>
    public const string Prefix = "sfq_";

    /// <summary>The indexes the catalog needs.</summary>
    public static IReadOnlyList<QueryIndex> Indexes(QueryCatalog catalog, string schema)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        var indexes = new List<QueryIndex>();
        foreach (QueryEventType type in catalog.EventTypes)
        {
            foreach ((IReadOnlyList<object> path, QueryField field) in Queryable(type.Latest.Fields, []))
            {
                string expression = PostgreSqlQueryCompiler.Extract("events", path, field.Type).Replace("events.data", "data", StringComparison.Ordinal);
                indexes.Add(Create(schema, "events", $"{type.Name}.{string.Join('.', path)}", $"tenant_id, ({expression})", $"event_type = {Literal(type.Name)}"));
            }
        }

        foreach (QueryReadModel model in catalog.ReadModels)
        {
            foreach ((IReadOnlyList<object> path, QueryField field) in Queryable(model.Fields, []))
            {
                string expression = PostgreSqlQueryCompiler.Extract("documents", path, field.Type).Replace("documents.data", "data", StringComparison.Ordinal);
                indexes.Add(Create(schema, "documents", $"{model.Collection}.{string.Join('.', path)}", $"tenant_id, ({expression})", $"collection = {Literal(model.Collection)}"));
            }
        }

        return [.. indexes.OrderBy(i => i.Source, StringComparer.Ordinal)];
    }

    /// <summary>Compares the catalog's indexes with the database's managed indexes.</summary>
    public static async ValueTask<QueryIndexPlan> PlanAsync(NpgsqlDataSource dataSource, QueryCatalog catalog, string schema, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        IReadOnlyList<QueryIndex> wanted = Indexes(catalog, schema);
        HashSet<string> existing = await ExistingAsync(dataSource, schema, cancellationToken).ConfigureAwait(false);
        return new QueryIndexPlan(
            [.. wanted.Where(i => !existing.Contains(i.Name))],
            [.. existing.Where(name => wanted.All(i => i.Name != name)).Order(StringComparer.Ordinal)],
            [.. wanted.Where(i => existing.Contains(i.Name)).Select(i => i.Name)]);
    }

    /// <summary>
    /// Creates missing indexes and drops stale managed ones. <paramref name="concurrently"/> (the default) builds without
    /// blocking writes; each statement then runs on its own, outside a transaction.
    /// </summary>
    public static async ValueTask<QueryIndexPlan> ApplyAsync(NpgsqlDataSource dataSource, QueryCatalog catalog, string schema, bool concurrently = true, CancellationToken cancellationToken = default)
    {
        QueryIndexPlan plan = await PlanAsync(dataSource, catalog, schema, cancellationToken).ConfigureAwait(false);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        foreach (string name in plan.Drop)
        {
            await using var drop = new NpgsqlCommand($"DROP INDEX {(concurrently ? "CONCURRENTLY " : string.Empty)}IF EXISTS {schema}.{name}", connection);
            await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (QueryIndex index in plan.Create)
        {
            string sql = concurrently ? index.Sql.Replace("CREATE INDEX ", "CREATE INDEX CONCURRENTLY ", StringComparison.Ordinal) : index.Sql;
            await using var create = new NpgsqlCommand(sql, connection);
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return plan;
    }

    private static QueryIndex Create(string schema, string table, string source, string columns, string predicate)
    {
        string definition = $"{table}|{columns}|{predicate}";
        string name = Prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(definition)))[..20];
        return new QueryIndex(name, table, source, $"CREATE INDEX IF NOT EXISTS {name} ON {schema}.{table} ({columns}) WHERE {predicate}");
    }

    private static IEnumerable<(IReadOnlyList<object> Path, QueryField Field)> Queryable(IReadOnlyList<QueryField> fields, IReadOnlyList<object> prefix)
    {
        foreach (QueryField field in fields)
        {
            IReadOnlyList<object> path = [.. prefix, field.Name];
            if (field.Queryable && !field.PersonalData && field.Type is not (QueryType.Timestamp or QueryType.Object or QueryType.Array or QueryType.Any))
            {
                yield return (path, field);
            }

            foreach ((IReadOnlyList<object> Path, QueryField Field) nested in Queryable(field.Fields, path))
            {
                yield return nested;
            }
        }
    }

    private static async ValueTask<HashSet<string>> ExistingAsync(NpgsqlDataSource dataSource, string schema, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand("SELECT indexname FROM pg_indexes WHERE schemaname = @schema AND starts_with(indexname, 'sfq_')");
        command.Parameters.Add(new NpgsqlParameter<string>("schema", schema));
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    // Catalog names (types, collections) in DDL: quoted SQL string constants; DDL takes no parameters.
    private static string Literal(string value)
    {
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}

using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using SourceFlow.Hosting;
using SourceFlow.Storage.PostgreSQL;
using SourceFlow.Testing.Conformance;

namespace SourceFlow.Storage.Tests.PostgreSql;

/// <summary>SCK-M01..M06: schema management (doc 05 §11, WP-2.1).</summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Provider", "PostgreSQL")]
[Trait("Requires", "Docker")]
public sealed class PostgreSqlMigrationTests(PostgreSqlServer server) : PostgreSqlTest(server)
{
    private static readonly string[] Tables =
    [
        "checkpoints", "collection_aliases", "data_keys", "dead_letters", "documents", "events", "inbox", "leases", "nodes",
        "outbox", "process_instances", "schema_history", "schemas", "snapshots", "streams", "subject_streams", "subscription_partitions",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "SCK-M01")]
    [Trait("Covers", "GAP-04")]
    public async Task FreshInstall_CreatesEveryTable_AndRecordsHistory()
    {
        string schema = PostgreSqlServer.NewSchema();
        var migrator = new PostgreSqlSchemaMigrator(Server.DataSource, schema);

        IReadOnlyList<int> applied = await migrator.MigrateAsync(Ct);

        applied.ShouldBe([1, 2, 3, 4, 5, 6, 7, 8, 9]);
        (await migrator.GetAppliedVersionsAsync(Ct)).ShouldBe([1, 2, 3, 4, 5, 6, 7, 8, 9]);
        (await TablesAsync(schema)).ShouldBe(Tables);
        await migrator.VerifyAsync(Ct);
    }

    [Fact]
    [Trait("Id", "SCK-M02")]
    public async Task Upgrade_AppliesOnlyPendingScripts_AndIsIdempotent()
    {
        string schema = PostgreSqlServer.NewSchema();
        MigrationScript v1 = PostgreSqlSchemaMigrator.EmbeddedScripts[0];
        var v2 = new MigrationScript(2, "extra", "CREATE TABLE {schema}.extra (id int PRIMARY KEY);");
        await new PostgreSqlSchemaMigrator(Server.DataSource, schema, [v1]).MigrateAsync(Ct);
        var upgraded = new PostgreSqlSchemaMigrator(Server.DataSource, schema, [v1, v2]);

        (await upgraded.MigrateAsync(Ct)).ShouldBe([2]);
        (await upgraded.MigrateAsync(Ct)).ShouldBeEmpty();
        (await upgraded.GetAppliedVersionsAsync(Ct)).ShouldBe([1, 2]);
        (await TablesAsync(schema)).ShouldContain("extra");
    }

    [Fact]
    [Trait("Id", "SCK-M02")]
    public async Task ModifiedAppliedScript_IsRejected_AndVerifyReportsMissingVersions()
    {
        string schema = PostgreSqlServer.NewSchema();
        MigrationScript v1 = PostgreSqlSchemaMigrator.EmbeddedScripts[0];
        await new PostgreSqlSchemaMigrator(Server.DataSource, schema, [v1]).MigrateAsync(Ct);

        var tampered = new PostgreSqlSchemaMigrator(Server.DataSource, schema, [v1 with { Sql = v1.Sql + "\n-- edited" }]);
        var newer = new PostgreSqlSchemaMigrator(Server.DataSource, schema, [v1, new MigrationScript(2, "extra", "SELECT 1;")]);

        (await Should.ThrowAsync<SchemaMigrationException>(() => tampered.MigrateAsync(Ct))).Message.ShouldContain("modified");
        (await Should.ThrowAsync<SchemaMigrationException>(() => newer.VerifyAsync(Ct))).Message.ShouldContain("V002");
        (await Should.ThrowAsync<SchemaMigrationException>(() => new PostgreSqlSchemaMigrator(Server.DataSource, PostgreSqlServer.NewSchema()).VerifyAsync(Ct)))
            .Message.ShouldContain("not installed");
    }

    [Fact]
    [Trait("Id", "SCK-M03")]
    public async Task ConcurrentMigrators_Serialise_ExactlyOneApplies()
    {
        string schema = PostgreSqlServer.NewSchema();

        IReadOnlyList<int>[] results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => new PostgreSqlSchemaMigrator(Server.DataSource, schema).MigrateAsync(Ct))));

        results.Sum(r => r.Count).ShouldBe(PostgreSqlSchemaMigrator.EmbeddedScripts.Count);
        results.Count(r => r.Count > 0).ShouldBe(1);
        (await TablesAsync(schema)).ShouldBe(Tables);
    }

    [Fact]
    [Trait("Id", "SCK-M04")]
    public async Task AutoMigrate_IsOffOutsideDevelopment_AndOnInDevelopment()
    {
        string schema = PostgreSqlServer.NewSchema();

        using IHost production = BuildHost(Environments.Production, schema);
        (await Should.ThrowAsync<SchemaMigrationException>(() => production.StartAsync(Ct))).Message.ShouldContain("not installed");
        (await TablesAsync(schema)).ShouldBeEmpty();

        using IHost development = BuildHost(Environments.Development, schema);
        await development.StartAsync(Ct);
        (await TablesAsync(schema)).ShouldBe(Tables);
        await development.StopAsync(Ct);

        using IHost productionAfterMigrate = BuildHost(Environments.Production, schema);
        await productionAfterMigrate.StartAsync(Ct);
        await productionAfterMigrate.StopAsync(Ct);
    }

    [Fact]
    [Trait("Id", "SCK-M05")]
    public async Task RuntimeRole_WithDmlOnly_PassesTheCoreConformanceCases()
    {
        string role = "r_" + Guid.CreateVersion7().ToString("N");
        await Server.ExecuteAsync($"CREATE ROLE \"{role}\" LOGIN PASSWORD 'runtime'");
        string connectionString = new NpgsqlConnectionStringBuilder(Server.ConnectionString) { Username = role, Password = "runtime" }.ConnectionString;
        string[] cases = ["SCK-02", "SCK-05", "SCK-21", "SCK-30", "SCK-35", "SCK-38", "SCK-47", "SCK-49", "SCK-51", "SCK-58"];

        foreach (ConformanceCase<IStorageFixture> @case in StorageConformance.Cases.Where(c => cases.Contains(c.Id)))
        {
            // A fresh schema per case keeps the cases independent; the role is granted on each.
            string caseSchema = PostgreSqlServer.NewSchema();
            await new PostgreSqlSchemaMigrator(Server.DataSource, caseSchema).MigrateAsync(Ct);
            await Server.ExecuteAsync($"""
                GRANT USAGE ON SCHEMA "{caseSchema}" TO "{role}";
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA "{caseSchema}" TO "{role}";
                """);
            await using var fixture = new PostgreSqlStorageFixture(new PostgreSqlStore(new PostgreSqlStorageOptions { ConnectionString = connectionString, Schema = caseSchema }));
            await @case.Run(fixture, Ct);
        }

        // ... and it cannot change the schema.
        await using NpgsqlDataSource runtime = NpgsqlDataSource.Create(connectionString);
        await Should.ThrowAsync<PostgresException>(() => new PostgreSqlSchemaMigrator(runtime, PostgreSqlServer.NewSchema()).MigrateAsync(Ct));
    }

    [Fact]
    [Trait("Id", "SCK-M06")]
    public async Task DbScript_ProducesTheSameSchemaAsMigrate()
    {
        string migrated = PostgreSqlServer.NewSchema();
        string scripted = PostgreSqlServer.NewSchema();
        await new PostgreSqlSchemaMigrator(Server.DataSource, migrated).MigrateAsync(Ct);

        var scripter = new PostgreSqlSchemaMigrator(Server.DataSource, scripted);
        await Server.ExecuteAsync(scripter.Script());

        (await ColumnsAsync(scripted)).ShouldBe(await ColumnsAsync(migrated));
        (await scripter.GetAppliedVersionsAsync(Ct)).ShouldBe([1, 2, 3, 4, 5, 6, 7, 8, 9]);
        await scripter.VerifyAsync(Ct);
        (await scripter.MigrateAsync(Ct)).ShouldBeEmpty();
    }

    private IHost BuildHost(string environment, string schema)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment, DisableDefaults = true });
        builder.Services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.UsePostgreSql(o =>
            {
                o.DataSource = Server.DataSource;
                o.Schema = schema;
            });
            sf.UseInMemoryTransport();
        });
        return builder.Build();
    }

    private async Task<string[]> TablesAsync(string schema)
    {
        await using NpgsqlCommand command = Server.DataSource.CreateCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = $1 ORDER BY table_name");
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = schema });
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Ct);
        var tables = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            tables.Add(reader.GetString(0));
        }

        return [.. tables];
    }

    private async Task<string[]> ColumnsAsync(string schema)
    {
        await using NpgsqlCommand command = Server.DataSource.CreateCommand(
            "SELECT table_name || '.' || column_name || ':' || data_type || ':' || is_nullable FROM information_schema.columns WHERE table_schema = $1 ORDER BY table_name, ordinal_position");
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = schema });
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Ct);
        var columns = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            columns.Add(reader.GetString(0));
        }

        return [.. columns];
    }
}

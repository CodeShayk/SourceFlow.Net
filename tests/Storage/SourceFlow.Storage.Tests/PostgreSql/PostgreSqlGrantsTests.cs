using System.Text;
using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using SourceFlow.Hosting;
using SourceFlow.Storage.PostgreSQL;
using SourceFlow.Tool;

namespace SourceFlow.Storage.Tests.PostgreSql;

/// <summary>
/// SEC-REV-01 (WP-5.5 security review, doc 10 §1 "events edited in the database"): with the grants from
/// <c>db script --grants</c>, the runtime role runs the service but can't update, delete or truncate events, on a plain
/// or a partitioned events table, including partitions the owner creates later. QRY-65 (WP-C.12, CR-23): with the grants from
/// <c>db script --query-role</c>, SFQL queries run as a role that can only read.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "EndToEnd")]
[Trait("Provider", "PostgreSQL")]
[Trait("Requires", "Docker")]
public sealed class PostgreSqlGrantsTests(PostgreSqlServer server) : PostgreSqlTest(server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [Trait("Id", "SEC-REV-01")]
    [Trait("Covers", "doc 10 1")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeRole_RunsTheService_ButEventsAreAppendOnly(bool partitioned)
    {
        string schema = PostgreSqlServer.NewSchema("grants");
        string role = "sf_rt_" + Guid.CreateVersion7().ToString("N")[^12..];
        var migrator = new PostgreSqlSchemaMigrator(Server.DataSource, schema, partitionEvents: partitioned);
        await migrator.MigrateAsync(Ct);
        await Server.ExecuteAsync($"CREATE ROLE \"{role}\" LOGIN PASSWORD '{role}'");

        // The script the CLI prints is the one applied.
        using var output = new StringWriter();
        (await SourceFlowCli.RunAsync(["db", "script", "--schema", schema, "--grants", role], output: output, cancellationToken: Ct)).ShouldBe(0);
        output.ToString().ShouldContain("REVOKE UPDATE, DELETE, TRUNCATE");
        await Server.ExecuteAsync(migrator.GrantsScript(role));

        // The service runs as the runtime role: commands, inline projection, outbox.
        using IHost host = await StartAsync(schema, Server.ConnectionStringFor(role), partitioned);
        ICommandSender commands = host.Services.GetRequiredService<ICommandSender>();
        AccountId id = AccountId.New();
        (await commands.SendAsync(new OpenAccount(id, "Ada", 10m), null, Ct)).Status.ShouldBe(CommandStatus.Accepted);
        (await commands.SendAsync(new Deposit(id, 5m), null, Ct)).Status.ShouldBe(CommandStatus.Accepted);
        await host.StopAsync(Ct);

        // History can't be edited, deleted or truncated through the table or a partition.
        await using var runtime = NpgsqlDataSource.Create(Server.ConnectionStringFor(role));
        await DeniedAsync(runtime, $"UPDATE \"{schema}\".events SET event_type = 'tampered'");
        await DeniedAsync(runtime, $"DELETE FROM \"{schema}\".events");
        await DeniedAsync(runtime, $"TRUNCATE \"{schema}\".events");
        if (partitioned)
        {
            await DeniedAsync(runtime, $"UPDATE \"{schema}\".events_initial SET event_type = 'tampered'");

            // A partition the owner creates later is append-only for the role too.
            await Server.ExecuteAsync($"SELECT \"{schema}\".sf_ensure_event_partitions(now() + interval '6 months', 1)");
            string later = (string)(await ScalarAsync(Server.DataSource, $"SELECT max(name) FROM \"{schema}\".event_partitions WHERE name <> 'events_initial'"))!;
            await DeniedAsync(runtime, $"DELETE FROM \"{schema}\".{later}");
            (await ScalarAsync(runtime, $"SELECT count(*) FROM \"{schema}\".{later}")).ShouldBe(0L);
        }

        (await ScalarAsync(runtime, $"SELECT count(*) FROM \"{schema}\".events WHERE event_type <> 'tampered'")).ShouldBe(2L);
    }

    [Fact]
    [Trait("Id", "QRY-65")]
    [Trait("Covers", "doc 14 7.1;Q-R2")]
    public async Task QueryRole_ReadsThroughSfql_ButCantWrite()
    {
        string schema = PostgreSqlServer.NewSchema("qrole");
        string suffix = Guid.CreateVersion7().ToString("N")[^12..];
        string runtimeRole = "sf_rt_" + suffix;
        string queryRole = "sf_q_" + suffix;
        var migrator = new PostgreSqlSchemaMigrator(Server.DataSource, schema);
        await migrator.MigrateAsync(Ct);
        await Server.ExecuteAsync($"CREATE ROLE \"{runtimeRole}\" LOGIN PASSWORD '{runtimeRole}'");
        await Server.ExecuteAsync($"CREATE ROLE \"{queryRole}\" LOGIN PASSWORD '{queryRole}'");

        using var output = new StringWriter();
        (await SourceFlowCli.RunAsync(["db", "script", "--schema", schema, "--query-role", queryRole], output: output, cancellationToken: Ct)).ShouldBe(0);
        output.ToString().ShouldContain($"GRANT SELECT ON ALL TABLES IN SCHEMA \"{schema}\" TO \"{queryRole}\"");
        await Server.ExecuteAsync(migrator.GrantsScript(runtimeRole));
        await Server.ExecuteAsync(migrator.QueryGrantsScript(queryRole));

        // The service writes as the runtime role and queries as the query role.
        using IHost host = await StartAsync(schema, Server.ConnectionStringFor(runtimeRole), partitioned: false, Server.ConnectionStringFor(queryRole));
        AccountId id = AccountId.New();
        (await host.Services.GetRequiredService<ICommandSender>().SendAsync(new OpenAccount(id, "Ada", 10m), null, Ct)).Status.ShouldBe(CommandStatus.Accepted);
        SourceFlow.Query.QueryPage page = await host.Services.GetRequiredService<SourceFlow.Query.IQueryService>()
            .QueryPageAsync("from readmodel \"account-summary\" | project holder", cancellationToken: Ct);
        page.Rows.Select(r => (string?)r[0]).ShouldBe(["Ada"]);
        await host.StopAsync(Ct);

        // The query role can read, and nothing else.
        await using var query = NpgsqlDataSource.Create(Server.ConnectionStringFor(queryRole));
        (await ScalarAsync(query, $"SELECT count(*) FROM \"{schema}\".events")).ShouldBe(1L);
        await DeniedAsync(query, $"INSERT INTO \"{schema}\".documents (tenant_id, collection, id, version, data) VALUES ('-', 'x', 'y', 1, '{{}}')");
        await DeniedAsync(query, $"UPDATE \"{schema}\".documents SET version = version + 1");
        await DeniedAsync(query, $"DELETE FROM \"{schema}\".events");
        await DeniedAsync(query, $"DELETE FROM \"{schema}\".outbox");
    }

    private async Task<IHost> StartAsync(string schema, string connectionString, bool partitioned, string? queryConnectionString = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development, DisableDefaults = true });
        builder.Services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.Projections(p => p.Add<AccountSummaryProjection>().Inline());
            sf.UsePostgreSql(o =>
            {
                o.ConnectionString = connectionString;
                o.Schema = schema;
                o.AutoMigrate = false;
                o.PartitionEvents = partitioned;
                o.QueryConnectionString = queryConnectionString;
            });
        });
        IHost host = builder.Build();
        await host.StartAsync(Ct);
        return host;
    }

    private static async Task DeniedAsync(NpgsqlDataSource dataSource, string sql)
    {
        PostgresException denied = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await using NpgsqlCommand command = dataSource.CreateCommand(sql);
            await command.ExecuteNonQueryAsync(Ct);
        });
        denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, new StringBuilder(sql).Append(": ").Append(denied.MessageText).ToString());
    }

    private static async Task<object?> ScalarAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        return await command.ExecuteScalarAsync(Ct);
    }
}

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
/// or a partitioned events table, including partitions the owner creates later.
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

    private async Task<IHost> StartAsync(string schema, string connectionString, bool partitioned)
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

using Bank.Accounts;
using Bank.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Runtime;
using SourceFlow.Storage.EntityFrameworkCore;
using SourceFlow.Storage.PostgreSQL;
using SourceFlow.Storage.Tests.PostgreSql;

namespace SourceFlow.Storage.Tests.EntityFrameworkCore;

/// <summary>UT-EF-11..13: EF Core read models with a schema per generation, rebuilt blue/green on PostgreSQL (doc 06 §6, ADR-032).</summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Provider", "EfCore")]
[Trait("Requires", "Docker")]
public sealed class EfGenerationTests(PostgreSqlServer server) : PostgreSqlTest(server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-EF-11")]
    [Trait("Covers", "doc 06 6")]
    public void DefaultSchema_IsALowerCaseIdentifierPerGeneration()
    {
        EfGenerations.DefaultSchema(new ReadModelGeneration("Account-Activity", 2, 1)).ShouldBe("account_activity_v2");
        EfGenerations.DefaultSchema(new ReadModelGeneration("account.activity", 1, 3)).ShouldBe("account_activity_v1_3");
    }

    [Fact]
    [Trait("Id", "UT-EF-12")]
    [Trait("Id", "UT-EF-13")]
    [Trait("Covers", "doc 06 6;ADR-032")]
    public async Task SchemaPerGeneration_FirstBuildCreatesItsSchema_ARebuildFillsANewOne_AndTheRetiredSchemaIsDropped()
    {
        string prefix = "rm" + Guid.CreateVersion7().ToString("N")[..12];
        var generations = new EfGenerationOptions { Schema = g => prefix + "_" + EfGenerations.DefaultSchema(g) };
        string first = prefix + "_account_activity_v1";
        string rebuilt = prefix + "_account_activity_v1_2";
        using IHost host = await StartAsync(generations);
        ICommandSender commands = host.Services.GetRequiredService<ICommandSender>();
        PostgreSqlStore store = host.Services.GetRequiredService<PostgreSqlStore>();
        var ada = AccountId.New();
        await commands.SendAsync(new OpenAccount(ada, "Ada", 10m), null, Ct);
        await commands.SendAsync(new Deposit(ada, 5m), null, Ct);

        // The first generation creates its schema from the EF model and claims the alias.
        await EventuallyAsync(async () => (await ActivityAsync(host, ada)) is { Balance: 15m }, "the first generation's row");
        await EventuallyAsync(async () => (await store.ListAliasesAsync(Ct)).Any(a => a.Alias == "account-activity" && a.Target == "account-activity@1"), "the alias claim");
        (await SchemaExistsAsync(first)).ShouldBeTrue();

        // A rebuild (dotnet sourceflow projection rebuild) builds @1.2 in its own schema while @1 keeps serving.
        await store.RequestAsync("account-activity", "account-activity@1", "account-activity@1.2", Ct);
        await EventuallyAsync(async () => (await store.ListAliasesAsync(Ct)).Any(a => a.Alias == "account-activity" && a.Target == "account-activity@1.2"), "the switch to @1.2");
        (await SchemaExistsAsync(rebuilt)).ShouldBeTrue();
        IReadModelResolver resolver = host.Services.GetRequiredService<IReadModelResolver>();
        (await resolver.ResolveAsync("account-activity", Ct)).ShouldBe(new ReadModelGeneration("account-activity", 1, 2));
        (await ActivityAsync(host, ada)).ShouldNotBeNull().Balance.ShouldBe(15m);   // read through the resolver: the rebuilt schema

        // RetainPreviousFor is zero here: the retired generation's schema is dropped at the next retention check.
        // Retention drops the schema, then forgets the previous generation: wait for both, not for the first alone.
        await EventuallyAsync(
            async () => !await SchemaExistsAsync(first) && (await store.ListAliasesAsync(Ct)).Single(a => a.Alias == "account-activity").Previous is null,
            "the retired schema to be dropped and forgotten");
    }

    private async Task<IHost> StartAsync(EfGenerationOptions generations)
    {
        string schema = PostgreSqlServer.NewSchema();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development, DisableDefaults = true });
        builder.Services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<ContractsModule>();
            sf.AddModule<AccountsModule>();
            sf.AddModule<ReportingModule>();
            sf.Projections(p => p.Add<AccountActivityProjection>().Async());
            sf.Configure(o =>
            {
                o.PollInterval = TimeSpan.FromMilliseconds(50);
                o.RetainPreviousFor = TimeSpan.Zero;
                o.RetentionInterval = TimeSpan.FromSeconds(1);
            });
            sf.UsePostgreSql(o =>
            {
                o.DataSource = Server.DataSource;
                o.Schema = schema;
            });
            sf.UseEntityFrameworkProjections<ReportingDbContext>(ReportingDbContext.Configure, (o, s) => new ReportingDbContext(o, s), generations);
            sf.UseInMemoryTransport();
        });
        IHost host = builder.Build();
        await host.StartAsync(Ct);
        return host;
    }

    // Reads the generation serving reads, as an application would: resolve it, then open a context for its schema.
    private async Task<AccountActivity?> ActivityAsync(IHost host, AccountId id)
    {
        ReadModelGeneration generation = await host.Services.GetRequiredService<IReadModelResolver>().ResolveAsync("account-activity", Ct);
        EfSessionContextFactory<ReportingDbContext> factory = host.Services.GetRequiredService<EfSessionContextFactory<ReportingDbContext>>();
        await using NpgsqlConnection connection = await Server.DataSource.OpenConnectionAsync(Ct);
        await using ReportingDbContext db = factory.CreateForGeneration(connection, generation);
        try
        {
            return await db.Activity.AsNoTracking().SingleOrDefaultAsync(a => a.AccountId == id.Value, Ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return null;   // the generation's schema is not created yet
        }
    }

    private async Task<bool> SchemaExistsAsync(string schema)
    {
        await using NpgsqlCommand command = Server.DataSource.CreateCommand("SELECT to_regnamespace(@s) IS NOT NULL");
        command.Parameters.AddWithValue("s", schema);
        return await command.ExecuteScalarAsync(Ct) is true;
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(30);
        while (!await condition())
        {
            if (TimeProvider.System.GetUtcNow() > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}.");
            }

            await RealClock.WaitAsync(TimeSpan.FromMilliseconds(50));
        }
    }
}

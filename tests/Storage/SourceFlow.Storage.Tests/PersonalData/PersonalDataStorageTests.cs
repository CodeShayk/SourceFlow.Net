using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SourceFlow.Hosting;
using SourceFlow.Query;
using SourceFlow.Runtime;
using SourceFlow.Security;
using SourceFlow.Storage.PostgreSQL;
using SourceFlow.Storage.SqlServer;
using SourceFlow.Storage.Tests.PostgreSql;
using SourceFlow.Storage.Tests.SqlServer;
using SourceFlow.Testing.Conformance;

namespace SourceFlow.Storage.Tests.PersonalData;

/// <summary>
/// Crypto-shredding on PostgreSQL (doc 10 §4, WP-5.2): E2E-29 (erase → events read <c>[erased]</c>), E2E-30 (the document
/// is scrubbed and the snapshot deleted), E2E-31 (an SFQL filter on personal data is rejected), and the key store contract.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "EndToEnd")]
[Trait("Provider", "PostgreSQL")]
[Trait("Requires", "Docker")]
public sealed class PostgreSqlPersonalDataTests(PostgreSqlServer server) : PostgreSqlTest(server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-PII-19")]
    [Trait("Covers", "doc 10 4")]
    public async Task KeyStore_Conformance()
    {
        PostgreSqlStore store = await Server.CreateStoreAsync();
        await PersonalDataKeyStoreConformance.RunAsync(store, TimeProvider.System.GetUtcNow(), Ct);
    }

    [Fact]
    [Trait("Id", "E2E-29")]
    [Trait("Id", "E2E-30")]
    [Trait("Id", "E2E-31")]
    [Trait("Covers", "doc 10 4;doc 14 8")]
    public async Task Erase_OnPostgreSql_EventsReadErased_DocumentScrubbed_SnapshotDeleted_PiiFiltersRejected()
    {
        using var keys = new LocalKeyProvider();
        string schema = PostgreSqlServer.NewSchema("pii");
        using IHost host = await PersonalDataScenario.StartAsync(keys, sf => sf.UsePostgreSql(o =>
        {
            o.DataSource = Server.DataSource;
            o.Schema = schema;
        }));
        await PersonalDataScenario.RunAsync(host);
    }
}

/// <summary>The same scenario and key store contract on SQL Server (WP-5.2).</summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "EndToEnd")]
[Trait("Provider", "SqlServer")]
[Trait("Requires", "Docker")]
public sealed class SqlServerPersonalDataTests(SqlServerServer server) : SqlServerTest(server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-PII-20")]
    [Trait("Covers", "doc 10 4")]
    public async Task KeyStore_Conformance()
    {
        SqlServerStore store = await Server.CreateStoreAsync();
        await PersonalDataKeyStoreConformance.RunAsync(store, TimeProvider.System.GetUtcNow(), Ct);
    }

    [Fact]
    [Trait("Id", "E2E-29")]
    [Trait("Covers", "doc 10 4")]
    public async Task Erase_OnSqlServer()
    {
        using var keys = new LocalKeyProvider();
        string schema = SqlServerServer.NewSchema("pii");
        using IHost host = await PersonalDataScenario.StartAsync(keys, sf => sf.UseSqlServer(Server.ConnectionString, o => o.Schema = schema));
        await PersonalDataScenario.RunAsync(host);
    }
}

internal static class PersonalDataScenario
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<IHost> StartAsync(LocalKeyProvider keys, Action<SourceFlowBuilder> storage)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development, DisableDefaults = true });
        builder.Services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "clinic";
            sf.AddModule<ClinicModule>();
            sf.Projections(p => p.Add<ClientCardsProjection>().Inline());
            sf.Security(s => s.UseKeyProvider(keys).EncryptPersonalData());
            storage(sf);
            sf.UseInMemoryTransport();
        });
        IHost host = builder.Build();
        await host.StartAsync(Ct);
        return host;
    }

    public static async Task RunAsync(IHost host)
    {
        SourceFlowRuntime runtime = host.Services.GetRequiredService<SourceFlowRuntime>();
        ICommandSender commands = host.Services.GetRequiredService<ICommandSender>();
        var erased = ClientId.New();
        var kept = ClientId.New();
        await commands.SendAsync(new RegisterClient(erased, "cust-erase", "Ada Lovelace", 10m), null, Ct);
        await commands.SendAsync(new RegisterClient(kept, "cust-keep", "Grace Hopper", 20m), null, Ct);
        StreamId stream = StreamId.For(Client.AggregateName, erased);

        // At rest, the name is ciphertext.
        StoredEvent raw = (await runtime.Storage.Events.ReadStreamAsync("-", stream, new StreamVersion(1), 1, Ct)).Events[0];
        Encoding.UTF8.GetString(raw.Data.Span).ShouldNotContain("Lovelace");
        await EventuallyAsync(async () => await runtime.Storage.Snapshots.GetAsync("-", stream, Ct) is not null, "the snapshot (SnapshotEvery = 1)");

        ErasureResult result = await host.Services.GetRequiredService<IPersonalDataEraser>().EraseSubjectAsync("cust-erase", cancellationToken: Ct);

        // E2E-29: events read [erased]; the other subject is untouched.
        result.Streams.ShouldBe(1);
        IAggregateReader reader = host.Services.GetRequiredService<IAggregateReader>();
        (await reader.LoadAsync<Client, ClientId>(erased, cancellationToken: Ct)).State.Name.ShouldBe(PersonalDataProtector.Erased);
        (await reader.LoadAsync<Client, ClientId>(kept, cancellationToken: Ct)).State.Name.ShouldBe("Grace Hopper");

        // E2E-30: the document is scrubbed, the snapshot deleted.
        ProjectionDescriptor projection = runtime.Model.Projections.Single(p => p.ProjectionType == typeof(ClientCardsProjection));
        StoredDocument card = (await runtime.Storage.Documents.GetAsync("-", projection.Collection, erased.ToStreamSegment(), Ct))!;
        Encoding.UTF8.GetString(card.Data.Span).ShouldContain("\"name\":\"[erased]\"");
        Encoding.UTF8.GetString(card.Data.Span).ShouldContain("\"balance\":10");
        (await runtime.Storage.Snapshots.GetAsync("-", stream, Ct)).ShouldBeNull();
        result.DocumentsScrubbed.ShouldBe(1);

        // E2E-31: SFQL rejects filters on personal data.
        QueryException rejected = await Should.ThrowAsync<QueryException>(async () =>
            await host.Services.GetRequiredService<IQueryService>().QueryPageAsync("from readmodel \"clinic-client-cards\" | where name = 'Ada Lovelace'", cancellationToken: Ct));
        rejected.Code.ShouldBe("SFQ0301");

        await host.StopAsync(Ct);
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(20);
        while (!await condition())
        {
            if (TimeProvider.System.GetUtcNow() > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, Ct);
        }
    }
}

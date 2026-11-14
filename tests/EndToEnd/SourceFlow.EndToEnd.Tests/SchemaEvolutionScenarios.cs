#pragma warning disable SF0001 // the messages are registered by hand below, to model two deployments of one service
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SourceFlow.Hosting;
using SourceFlow.Storage.PostgreSQL;
using SourceFlow.Storage.Tests.PostgreSql;
using SourceFlow.Transport.InMemory;

namespace SourceFlow.EndToEnd.Tests;

/// <summary>E2E-34 (WP-C.8, CR-17): the rename-and-upcast procedure of doc 03 §2.1 deploys with schema registration enforced.</summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "EndToEnd")]
public sealed class SchemaEvolutionScenarios(PostgreSqlServer postgreSql)
{
    [Fact]
    [Trait("Id", "E2E-34")]
    [Trait("Covers", "ADR-005;ADR-029 2;doc 03 2.1")]
    public async Task RenamingTheClrType_AndAddingAnUpcastVersion_DeploysWithoutASchemaConflict()
    {
        await postgreSql.EnsureStartedAsync();
        string schema = PostgreSqlServer.NewSchema("evolve");

        await DeployAsync<FirstDeployment>(schema);    // registers v1 (title "MemberJoined")
        await DeployAsync<NextDeployment>(schema);     // v1 is now "MemberJoinedV1" and upcasts to v2

        await using PostgreSqlStore store = await postgreSql.CreateStoreAsync(configure: o => o.Schema = schema);
        IReadOnlyList<Catalog.SchemaEntry> registered = await ((Storage.ISchemaStore)store).ListAsync(TestContext.Current.CancellationToken);
        registered.Where(e => e.Type == "e2e.member.joined").Select(e => e.Version).ShouldBe([1, 2]);
    }

    // Starts and stops one deployment of the service on the shared schema; a schema conflict fails StartAsync.
    private async Task DeployAsync<TModule>(string schema)
        where TModule : ISourceFlowModule
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development, DisableDefaults = true });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "members";
            sf.AddModule<TModule>();
            sf.UsePostgreSql(o =>
            {
                o.DataSource = postgreSql.DataSource;
                o.Schema = schema;
            });
            sf.UseTransport(_ => new InMemoryBroker().CreateTransport());
            sf.Configure(o => o.SchemaRegistration = Runtime.SchemaRegistrationMode.Enforce);
        });

        using IHost host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    // Nested, so the generator leaves them out of this assembly's modules; the deployments below register them.
    // The first deployment: e2e.member.joined v1 under its original CLR name.
    public sealed record MemberJoined(string Name) : IEvent;

    // The next deployment (doc 03 §2.1): the CLR type renamed, a v2 added, and an upcaster from v1.
    public sealed record MemberJoinedV1(string Name) : IEvent;

    public sealed record MemberJoinedV2(string Name, string Club) : IEvent;

    public sealed class MemberJoinedUpcaster : IUpcaster<MemberJoinedV1, MemberJoinedV2>
    {
        public MemberJoinedV2 Upcast(MemberJoinedV1 old)
        {
            return new MemberJoinedV2(old.Name, "unknown");
        }
    }

    private sealed class FirstDeployment : ISourceFlowModule
    {
        public static string ModuleName => "members";

        public static void Register(ISourceFlowModuleBuilder builder)
        {
            builder.AddMessage<MemberJoined>("e2e.member.joined", 1);
            builder.AddJsonTypeInfoResolver(new DefaultJsonTypeInfoResolver());
        }
    }

    private sealed class NextDeployment : ISourceFlowModule
    {
        public static string ModuleName => "members";

        public static void Register(ISourceFlowModuleBuilder builder)
        {
            builder.AddMessage<MemberJoinedV1>("e2e.member.joined", 1);
            builder.AddMessage<MemberJoinedV2>("e2e.member.joined", 2);
            builder.AddUpcaster<MemberJoinedV1, MemberJoinedV2, MemberJoinedUpcaster>();
            builder.AddJsonTypeInfoResolver(new DefaultJsonTypeInfoResolver());
        }
    }
}

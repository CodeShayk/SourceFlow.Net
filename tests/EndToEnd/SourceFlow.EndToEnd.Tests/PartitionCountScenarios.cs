using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SourceFlow.Hosting;
using SourceFlow.Storage.PostgreSQL;
using SourceFlow.Storage.Tests.PostgreSql;
using SourceFlow.Testing.Conformance;
using SourceFlow.Transport.InMemory;

namespace SourceFlow.EndToEnd.Tests;

/// <summary>E2E-35 (WP-C.9, CR-18): a rolling deploy whose nodes disagree on the partition count handles each event once.</summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "EndToEnd")]
public sealed class PartitionCountScenarios(PostgreSqlServer postgreSql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "E2E-35")]
    [Trait("Covers", "doc 06 3;doc 06 4")]
    public async Task ARollingDeployWithAMixedPartitionCount_HandlesEachEventOnce()
    {
        await postgreSql.EnsureStartedAsync();
        string schema = PostgreSqlServer.NewSchema("parts");
        var broker = new InMemoryBroker();
        var observations = new TckObservations();

        using IHost old = await NodeAsync(schema, broker, observations, "old", partitions: 2);   // stores 2
        using IHost next = await NodeAsync(schema, broker, observations, "next", partitions: 4); // the new config, mid-deploy
        ICommandSender commands = old.Services.GetRequiredService<ICommandSender>();
        for (int i = 0; i < 20; i++)
        {
            await commands.SendAsync(new TckAdd(TckTallyId.New(), 1), null, Ct);
        }

        DateTimeOffset deadline = TimeProvider.System.GetUtcNow().AddSeconds(30);
        while (observations.Handled.Count < 20 && TimeProvider.System.GetUtcNow() < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, Ct);
        }

        await Task.Delay(TimeSpan.FromSeconds(1), TimeProvider.System, Ct);   // anything handled twice would show by now
        observations.Handled.Count.ShouldBe(20);
        observations.Handled.Select(h => h.EventId).Distinct().Count().ShouldBe(20, "each event once");
        (await StoredCountsAsync(schema)).ShouldAllBe(c => c == 2, "the new node's count was never stored; it refuses until a repartition");

        await old.StopAsync(Ct);
        await next.StopAsync(Ct);
    }

    private async Task<IHost> NodeAsync(string schema, InMemoryBroker broker, TckObservations observations, string node, int partitions)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development, DisableDefaults = true });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(observations);
        builder.Services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "tck";
            sf.AddModule<TckModule>();
            sf.UsePostgreSql(o =>
            {
                o.DataSource = postgreSql.DataSource;
                o.Schema = schema;
            });
            sf.UseTransport(_ => broker.CreateTransport());
            sf.Configure(o =>
            {
                o.NodeId = node;
                o.SubscriptionPartitions = partitions;
                o.PollInterval = TimeSpan.FromMilliseconds(50);
            });
        });

        IHost host = builder.Build();
        await host.StartAsync(Ct);
        if (partitions == 2)
        {
            // Let the old node run its subscriptions once, so it stores its count before the new node starts.
            DateTimeOffset deadline = TimeProvider.System.GetUtcNow().AddSeconds(10);
            while ((await StoredCountsAsync(schema)).Count == 0 && TimeProvider.System.GetUtcNow() < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, Ct);
            }
        }

        return host;
    }

    // Every stored subscription partition count in the schema (doc 06 §3, V009).
    private async Task<IReadOnlyList<int>> StoredCountsAsync(string schema)
    {
        await using Npgsql.NpgsqlCommand command = postgreSql.DataSource.CreateCommand($"SELECT partitions FROM \"{schema}\".subscription_partitions");
        var counts = new List<int>();
        await using Npgsql.NpgsqlDataReader reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            counts.Add(reader.GetInt32(0));
        }

        return counts;
    }
}

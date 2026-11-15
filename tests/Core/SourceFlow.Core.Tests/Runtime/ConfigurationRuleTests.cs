using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Hosting;
using SourceFlow.Runtime;
using SourceFlow.Security;
using SourceFlow.Storage.InMemory;
using SourceFlow.Testing;

namespace SourceFlow.Core.Tests.Runtime;

/// <summary>UT-HOST-21..25: configuration mistakes fail at startup, not later in production (CR-60).</summary>
[Trait("Category", "Unit")]
public sealed class ConfigurationRuleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-HOST-21")]
    [Trait("Covers", "doc 01 3;GAP-03")]
    public void A_second_storage_or_transport_is_refused()
    {
        Should.Throw<InvalidOperationException>(() => Build(sf => sf.UseInMemoryStorage().UseInMemoryStorage()))
            .Message.ShouldContain("Storage is configured twice");
        Should.Throw<InvalidOperationException>(() => Build(sf => sf.UseInMemoryStorage().UseInMemoryTransport().UseInMemoryTransport()))
            .Message.ShouldContain("transport is configured twice");
    }

    [Fact]
    [Trait("Id", "UT-HOST-22")]
    [Trait("Covers", "doc 01 3")]
    public async Task The_test_hosts_in_memory_defaults_are_replaced_once_by_the_services_own_slot()
    {
        var mine = new InMemoryStore();
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "counters";
            sf.AddModule<CoreTestModule>();
            sf.UseInMemoryStorage(mine);
        }, cancellationToken: Ct);

        host.Services.GetRequiredService<SourceFlowRuntime>().Storage.Events.ShouldBeSameAs(mine);
        Should.Throw<InvalidOperationException>(() => SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "counters";
            sf.UseInMemoryStorage().UseInMemoryStorage();
        }, cancellationToken: Ct).GetAwaiter().GetResult());
    }

    [Fact]
    [Trait("Id", "UT-HOST-23")]
    [Trait("Covers", "ADR-016")]
    public void A_second_key_provider_is_refused()
    {
        using var first = new LocalKeyProvider();
        using var second = new LocalKeyProvider();

        Should.Throw<InvalidOperationException>(() => Build(sf => sf.UseInMemoryStorage().Security(s => s.UseKeyProvider(first).UseKeyProvider(second))))
            .Message.ShouldContain("key provider is configured twice");
    }

    [Fact]
    [Trait("Id", "UT-HOST-24")]
    [Trait("Covers", "doc 06 7")]
    public void OnFailure_on_an_inline_projection_is_refused()
    {
        Should.Throw<InvalidOperationException>(() => Build(sf => sf.UseInMemoryStorage()
                .Projections(p => p.Add<DripsProjection>().Inline().OnFailure(SubscriptionFailurePolicy.Park))))
            .Message.ShouldContain("OnFailure applies to async projections only");
    }

    [Fact]
    [Trait("Id", "UT-HOST-25")]
    [Trait("Covers", "ADR-016")]
    public void A_key_provider_that_nothing_uses_is_reported()
    {
        using var keys = new LocalKeyProvider();
        using ServiceProvider unused = Build(sf => sf.UseInMemoryStorage().Security(s => s.UseKeyProvider(keys)));
        using ServiceProvider used = Build(sf => sf.UseInMemoryStorage().Security(s => s.UseKeyProvider(keys).Protect(p => p.ProtectAll())));
        using ServiceProvider none = Build(sf => sf.UseInMemoryStorage());

        unused.GetRequiredService<SourceFlowRuntime>().KeyProviderUnused.ShouldBeTrue();   // logged at start (5002)
        used.GetRequiredService<SourceFlowRuntime>().KeyProviderUnused.ShouldBeFalse();
        none.GetRequiredService<SourceFlowRuntime>().KeyProviderUnused.ShouldBeFalse();
    }

    [Fact]
    [Trait("Id", "UT-OUT-21")]
    [Trait("Covers", "doc 04 7")]
    public async Task The_service_records_its_outbox_partition_count_for_the_tools()
    {
        var store = new InMemoryStore();
        await using (SourceFlowTestHost first = await Counters(store, partitions: 8))
        {
            (await store.GetPartitionsAsync("$outbox", Ct)).ShouldBe(8);
        }

        await using SourceFlowTestHost changed = await Counters(store, partitions: 4);
        (await store.GetPartitionsAsync("$outbox", Ct)).ShouldBe(4);   // a redeploy with a new count updates it
    }

    private static Task<SourceFlowTestHost> Counters(InMemoryStore store, int partitions)
    {
        return SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "counters";
            sf.AddModule<CoreTestModule>();
            sf.Configure(o => o.OutboxPartitions = partitions);
        }, store: store, cancellationToken: Ct);
    }

    private static ServiceProvider Build(Action<SourceFlowBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "counters";
            sf.AddModule<CoreTestModule>();
            configure(sf);
        });
        return services.BuildServiceProvider();
    }
}

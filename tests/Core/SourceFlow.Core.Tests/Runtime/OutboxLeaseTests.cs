using Bank.Accounts;
using SourceFlow.Storage;
using SourceFlow.Storage.InMemory;
using SourceFlow.Testing;

namespace SourceFlow.Core.Tests.Runtime;

/// <summary>A graceful stop hands the outbox over at once (doc 04 §7; found by WF-E2E-10's redeploy, WP-S.6).</summary>
[Trait("Category", "Unit")]
public sealed class OutboxLeaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-OUT-08")]
    [Trait("Covers", "doc 04 7")]
    public async Task Stop_ReleasesTheOutboxLeases_SoTheNextNodeRelaysAtOnce()
    {
        var store = new InMemoryStore(TimeProvider.System);
        await using (SourceFlowTestHost first = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.Routing(r => r.Publish<AccountOpened>().AsIs().To("accounts.events"));
        }, store: store, cancellationToken: Ct))
        {
            // Idle with nothing due means a relay pass ran; a pass takes the partitions in order, so it holds outbox:0.
            // (Idle alone can come before the relay loop's first pass on a loaded machine.)
            await first.SendAsync(new OpenAccount(AccountId.New(), "Ada", 5m), cancellationToken: Ct);
            await first.WaitForIdleAsync();
            (await store.TryAcquireAsync("outbox:0", "next-node", TimeSpan.FromSeconds(30), Ct)).ShouldBeNull("the running relay holds its partitions");
        }

        for (int partition = 0; partition < 16; partition++)
        {
            (await store.TryAcquireAsync($"outbox:{partition}", "next-node", TimeSpan.FromSeconds(30), Ct)).ShouldNotBeNull($"outbox:{partition} was released on stop, not left to its TTL");
        }
    }
}

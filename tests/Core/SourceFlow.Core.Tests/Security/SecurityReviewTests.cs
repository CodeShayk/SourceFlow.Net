using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Runtime;
using SourceFlow.Storage;

namespace SourceFlow.Core.Tests.Security;

/// <summary>SEC-REV-02 (WP-5.5 security review, doc 10 §1 repudiation): dead-letter operations record who did them.</summary>
[Trait("Category", "Unit")]
public sealed class SecurityReviewTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "SEC-REV-02")]
    [Trait("Covers", "doc 10 1;doc 04 9.3")]
    public async Task DeadLetterDiscard_RecordsTheActor()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IActorAccessor>(new FixedActor("alice"));
        services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.UseInMemoryStorage();
        });
        await using ServiceProvider provider = services.BuildServiceProvider();
        SourceFlowRuntime runtime = provider.GetRequiredService<SourceFlowRuntime>();
        IDeadLetterManager manager = provider.GetRequiredService<IDeadLetterManager>();
        DateTimeOffset now = runtime.Time.GetUtcNow();
        Guid id = Guid.CreateVersion7(now);
        await runtime.Storage.DeadLetters.ParkAsync(new DeadLetter
        {
            Id = id,
            Consumer = "accounts",
            Source = "receive",
            Envelope = new MessageEnvelope { MessageId = MessageIds.New(runtime.Time), Type = "bank.account.open", TypeVersion = 1, Source = "x", Time = now, Body = "{}"u8.ToArray() },
            Destination = "accounts",
            Reason = "PermanentFailure",
            FirstFailedAt = now,
            ParkedAt = now,
        }, Ct);

        await manager.DiscardAsync(id, "obsolete", Ct);

        (await manager.GetAsync(id, Ct))!.StatusNote.ShouldBe("by alice: obsolete");
    }

    private sealed class FixedActor(string actor) : IActorAccessor
    {
        public string? Actor => actor;
    }
}

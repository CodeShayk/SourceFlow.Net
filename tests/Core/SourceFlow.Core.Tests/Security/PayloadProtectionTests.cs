using System.Security.Cryptography;
using System.Text;
using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Pipeline;
using SourceFlow.Security;
using SourceFlow.Storage;
using SourceFlow.Testing;
using SourceFlow.Transport;

namespace SourceFlow.Core.Tests.Security;

/// <summary>UT-SEC-01..20 (plan 05, WP-5.1): payload protection (doc 10 §3) with the local key provider.</summary>
[Trait("Category", "Unit")]
public sealed class PayloadProtectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ================================================================ the protector

    [Fact]
    [Trait("Id", "UT-SEC-01")]
    [Trait("Covers", "doc 10 3")]
    public async Task RoundTrip_RestoresTheBody_AndNamesSchemeProviderAndKey()
    {
        using var keys = new LocalKeyProvider();
        using PayloadProtector protector = Protector(keys);
        MessageEnvelope plain = Envelope("{\"holder\":\"Ada\"}");

        MessageEnvelope sealedEnvelope = await protector.ProtectAsync(plain, Ct);
        MessageEnvelope opened = await protector.UnprotectAsync(sealedEnvelope, Ct);

        sealedEnvelope.Protection.ShouldBe("sf-aes256gcm-v1;local;local-master/v1");
        opened.Body.ToArray().ShouldBe(plain.Body.ToArray());
        opened.Protection.ShouldBeNull();
        opened.ShouldBe(plain);
    }

    [Fact]
    [Trait("Id", "UT-SEC-02")]
    public async Task Ciphertext_HidesThePlaintext_AndNoncesDiffer()
    {
        using var keys = new LocalKeyProvider();
        using PayloadProtector protector = Protector(keys);
        MessageEnvelope plain = Envelope("{\"holder\":\"Ada Lovelace\"}");

        MessageEnvelope first = await protector.ProtectAsync(plain, Ct);
        MessageEnvelope second = await protector.ProtectAsync(plain, Ct);

        Encoding.UTF8.GetString(first.Body.Span).ShouldNotContain("Lovelace");
        first.Body.ToArray().ShouldNotBe(second.Body.ToArray(), "a fresh 96-bit nonce per message");
        (await protector.ProtectAsync(first, Ct)).ShouldBeSameAs(first, "protected envelopes are not protected twice");
    }

    [Theory]
    [Trait("Id", "UT-SEC-03")]
    [Trait("Covers", "doc 10 3")]
    [InlineData("id")]
    [InlineData("type")]
    [InlineData("source")]
    public async Task AssociatedData_IdTypeAndSource_AreAuthenticated(string altered)
    {
        using var keys = new LocalKeyProvider();
        using PayloadProtector protector = Protector(keys);
        MessageEnvelope sealedEnvelope = await protector.ProtectAsync(Envelope("{}"), Ct);
        MessageEnvelope moved = altered switch
        {
            "id" => sealedEnvelope with { MessageId = MessageIds.New(TimeProvider.System) },
            "type" => sealedEnvelope with { Type = "bank.account.closed" },
            _ => sealedEnvelope with { Source = "intruder" },
        };

        await Should.ThrowAsync<DecryptionFailedException>(async () => await protector.UnprotectAsync(moved, Ct));
    }

    [Fact]
    [Trait("Id", "UT-SEC-04")]
    public async Task TamperedOrTruncatedBodies_FailDecryption()
    {
        using var keys = new LocalKeyProvider();
        using PayloadProtector protector = Protector(keys);
        MessageEnvelope sealedEnvelope = await protector.ProtectAsync(Envelope("{\"amount\":10}"), Ct);
        byte[] flipped = sealedEnvelope.Body.ToArray();
        flipped[^1] ^= 0x01;
        byte[] badVersion = sealedEnvelope.Body.ToArray();
        badVersion[0] = 9;

        await Should.ThrowAsync<DecryptionFailedException>(async () => await protector.UnprotectAsync(sealedEnvelope with { Body = flipped }, Ct));
        await Should.ThrowAsync<DecryptionFailedException>(async () => await protector.UnprotectAsync(sealedEnvelope with { Body = sealedEnvelope.Body[..20] }, Ct));
        await Should.ThrowAsync<DecryptionFailedException>(async () => await protector.UnprotectAsync(sealedEnvelope with { Body = badVersion }, Ct));
        await Should.ThrowAsync<DecryptionFailedException>(async () => await protector.UnprotectAsync(sealedEnvelope with { Body = Array.Empty<byte>() }, Ct));
    }

    [Fact]
    [Trait("Id", "UT-SEC-05")]
    public async Task UnknownSchemeOrForeignProvider_FailsDecryption()
    {
        using var keys = new LocalKeyProvider();
        using var other = new LocalKeyProvider("other-master");
        using PayloadProtector protector = Protector(keys);
        using PayloadProtector foreign = Protector(other);
        MessageEnvelope sealedEnvelope = await protector.ProtectAsync(Envelope("{}"), Ct);

        await Should.ThrowAsync<DecryptionFailedException>(async () => await protector.UnprotectAsync(sealedEnvelope with { Protection = "rot13;local;x" }, Ct));
        await Should.ThrowAsync<DecryptionFailedException>(async () => await protector.UnprotectAsync(sealedEnvelope with { Protection = "sf-aes256gcm-v1;aws-kms;arn" }, Ct));
        (await Should.ThrowAsync<DecryptionFailedException>(async () => await foreign.UnprotectAsync(sealedEnvelope, Ct))).Message.ShouldContain("refused");
    }

    [Fact]
    [Trait("Id", "UT-SEC-06")]
    [Trait("Covers", "doc 10 3")]
    public async Task DataKey_IsReusedUpToMaxMessagesPerKey()
    {
        using var keys = new LocalKeyProvider();
        using PayloadProtector protector = Protector(keys, o => o.MaxMessagesPerKey = 3);

        for (int i = 0; i < 7; i++)
        {
            await protector.ProtectAsync(Envelope("{}"), Ct);
        }

        keys.Generated.ShouldBe(3, "7 messages at 3 per key");
    }

    [Fact]
    [Trait("Id", "UT-SEC-07")]
    [Trait("Covers", "doc 10 3")]
    public async Task DataKey_IsReusedUpToMaxKeyAge()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddYears(56));
        using var keys = new LocalKeyProvider();
        using PayloadProtector protector = Protector(keys, time: time);

        await protector.ProtectAsync(Envelope("{}"), Ct);
        time.Advance(TimeSpan.FromMinutes(4));
        await protector.ProtectAsync(Envelope("{}"), Ct);
        time.Advance(TimeSpan.FromMinutes(2));
        await protector.ProtectAsync(Envelope("{}"), Ct);

        keys.Generated.ShouldBe(2, "a new key after 5 minutes");
    }

    [Fact]
    [Trait("Id", "UT-SEC-08")]
    public async Task UnwrappedKeys_AreCached_OneKeyServiceCallPerKey()
    {
        using var keys = new LocalKeyProvider();
        using PayloadProtector sender = Protector(keys);
        using PayloadProtector receiver = Protector(keys);
        var sealedEnvelopes = new List<MessageEnvelope>();
        for (int i = 0; i < 50; i++)
        {
            sealedEnvelopes.Add(await sender.ProtectAsync(Envelope("{\"n\":" + i + "}"), Ct));
        }

        foreach (MessageEnvelope envelope in sealedEnvelopes)
        {
            await receiver.UnprotectAsync(envelope, Ct);
        }

        keys.Unwrapped.ShouldBe(1);
    }

    [Fact]
    [Trait("Id", "UT-SEC-09")]
    [Trait("Covers", "doc 10 3")]
    public async Task KeyRotation_IsTransparent()
    {
        using var keys = new LocalKeyProvider();
        using PayloadProtector sender = Protector(keys, o => o.MaxMessagesPerKey = 1);
        using PayloadProtector receiver = Protector(keys);
        MessageEnvelope before = await sender.ProtectAsync(Envelope("{\"v\":1}"), Ct);

        keys.Rotate();
        MessageEnvelope after = await sender.ProtectAsync(Envelope("{\"v\":2}"), Ct);

        before.Protection.ShouldEndWith("/v1");
        after.Protection.ShouldEndWith("/v2");
        Encoding.UTF8.GetString((await receiver.UnprotectAsync(before, Ct)).Body.Span).ShouldBe("{\"v\":1}");
        Encoding.UTF8.GetString((await receiver.UnprotectAsync(after, Ct)).Body.Span).ShouldBe("{\"v\":2}");
    }

    [Fact]
    [Trait("Id", "UT-SEC-10")]
    [Trait("Covers", "doc 10 3")]
    public async Task ReplacedAndDisposedKeys_AreZeroed()
    {
        using var inner = new LocalKeyProvider();
        var keys = new RecordingProvider(inner);
        PayloadProtector protector = Protector(keys, o => o.MaxMessagesPerKey = 1);
        for (int i = 0; i < 4; i++)
        {
            await protector.ProtectAsync(Envelope("{}"), Ct);
        }

        keys.Issued.Count.ShouldBe(4);
        keys.Issued[0].Plaintext.ShouldAllBe(b => b == 0, "two rotations old: zeroed");
        keys.Issued[1].Plaintext.ShouldAllBe(b => b == 0);
        keys.Issued[3].Plaintext.ShouldContain(b => b != 0, "the active key is in use");

        protector.Dispose();
        keys.Issued.ShouldAllBe(k => k.Plaintext.All(b => b == 0), "dispose zeroes every key");

        var data = new DataKey(RandomNumberGenerator.GetBytes(32), [1, 2, 3], "k");
        data.Dispose();
        data.Plaintext.ShouldAllBe(b => b == 0);
    }

    [Fact]
    [Trait("Id", "UT-SEC-11")]
    [Trait("Covers", "doc 10 3")]
    public async Task KeyServiceErrors_AreMapped_TransientRetriedPermanentParked()
    {
        using var keys = new LocalKeyProvider();
        using PayloadProtector sender = Protector(keys);
        MessageEnvelope sealedEnvelope = await sender.ProtectAsync(Envelope("{}"), Ct);
        using PayloadProtector receiver = Protector(keys);
        var classifier = new ErrorClassifier();

        keys.Failure = new KeyProviderException("throttled", isTransient: true);
        KeyProviderException transient = await Should.ThrowAsync<KeyProviderException>(async () => await receiver.UnprotectAsync(sealedEnvelope, Ct));
        keys.Failure = new KeyProviderException("access denied", isTransient: false);
        DecryptionFailedException permanent = await Should.ThrowAsync<DecryptionFailedException>(async () => await receiver.UnprotectAsync(sealedEnvelope, Ct));

        classifier.Classify(transient).ShouldBe(HandlingOutcome.Transient);
        classifier.Classify(permanent).ShouldBe(HandlingOutcome.Permanent);
        classifier.Classify(new KeyProviderException("denied", isTransient: false)).ShouldBe(HandlingOutcome.Permanent);
    }

    [Fact]
    [Trait("Id", "UT-SEC-12")]
    public void Options_AreValidated()
    {
        using var keys = new LocalKeyProvider();

        Should.Throw<InvalidOperationException>(() => new PayloadProtector(keys, new PayloadProtectionOptions())).Message.ShouldContain("protects nothing");
        Should.Throw<InvalidOperationException>(() => new PayloadProtector(keys, new PayloadProtectionOptions { MaxMessagesPerKey = 0 }.ProtectAll()));
        Should.Throw<InvalidOperationException>(() => new PayloadProtector(keys, new PayloadProtectionOptions { MaxKeyAge = TimeSpan.Zero }.ProtectAll()));
        Should.Throw<ArgumentException>(() => new DataKey(new byte[16], [1], "k"));
        new PayloadProtector(keys, new PayloadProtectionOptions().Protect("a")).Protects("b").ShouldBeFalse();
    }

    // ================================================================ in the runtime

    [Fact]
    [Trait("Id", "UT-SEC-13")]
    [Trait("Covers", "doc 10 3")]
    public async Task Outbox_StoresProtectedBodies_ForProtectedDestinationsOnly()
    {
        using var keys = new LocalKeyProvider();
        await using SourceFlowTestHost host = await HostAsync(keys, p => p.Protect("accounts.events"), relay: false, publishAlso: "audit.events");

        await host.SendAsync(new OpenAccount(AccountId.New(), "Ada Lovelace", 5m), cancellationToken: Ct);

        IReadOnlyList<OutboxRecord> rows = host.Store.OutboxSnapshot();
        OutboxRecord protectedRow = rows.Single(r => r.Message.Destination == "accounts.events");
        OutboxRecord plainRow = rows.Single(r => r.Message.Destination == "audit.events");
        protectedRow.Message.Envelope.Protection.ShouldStartWith(PayloadProtector.Scheme);
        Encoding.UTF8.GetString(protectedRow.Message.Envelope.Body.Span).ShouldNotContain("Lovelace");
        plainRow.Message.Envelope.Protection.ShouldBeNull();
        Encoding.UTF8.GetString(plainRow.Message.Envelope.Body.Span).ShouldContain("Lovelace");
    }

    [Fact]
    [Trait("Id", "UT-SEC-14")]
    [Trait("Covers", "doc 10 3")]
    public async Task Relay_PublishesTheProtectedBody_AndAReceiverCanOpenIt()
    {
        using var keys = new LocalKeyProvider();
        await using SourceFlowTestHost host = await HostAsync(keys, p => p.Protect("accounts.events"));
        host.Broker.DeclareTopic("accounts.events");
        host.Broker.Subscribe("accounts.events", "spy");
        await using ITransportReceiver spy = await host.Broker.CreateTransport().CreateReceiverAsync(new ReceiverOptions("spy", []), Ct);

        await host.SendAsync(new OpenAccount(AccountId.New(), "Ada Lovelace", 5m), cancellationToken: Ct);
        IReadOnlyList<ReceivedMessage> batch = [];
        for (int i = 0; i < 50 && batch.Count == 0; i++)
        {
            batch = await spy.ReceiveAsync(10, TimeSpan.FromMilliseconds(200), Ct);   // the relay publishes in the background
        }

        ReceivedMessage received = batch.ShouldHaveSingleItem();

        received.Envelope.Protection.ShouldNotBeNull();
        Encoding.UTF8.GetString(received.Envelope.Body.Span).ShouldNotContain("Lovelace");
        using PayloadProtector reader = Protector(keys);
        Encoding.UTF8.GetString((await reader.UnprotectAsync(received.Envelope, Ct)).Body.Span).ShouldContain("Lovelace");
    }

    [Fact]
    [Trait("Id", "UT-SEC-15")]
    [Trait("Covers", "doc 10 3")]
    public async Task ReceivedProtectedCommand_IsDecrypted_AndHandled()
    {
        using var keys = new LocalKeyProvider();
        await using SourceFlowTestHost host = await HostAsync(keys, p => p.Protect("accounts"));
        using PayloadProtector sender = Protector(keys);
        var id = AccountId.New();

        host.Broker.Send("accounts", await sender.ProtectAsync(host.Runtime.CreateEnvelope(new OpenAccount(id, "Ada", 5m)), Ct));
        await host.WaitForIdleAsync();

        (await host.EventsAsync(StreamId.For(BankAccount.AggregateName, id))).ShouldHaveSingleItem().ShouldBeOfType<AccountOpened>();
        (await host.DeadLetters.ListAsync(new DeadLetterQuery(), Ct)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Id", "UT-SEC-16")]
    [Trait("Covers", "doc 10 3")]
    public async Task TamperedMessage_IsParkedAsDecryptionFailed_NotRetried()
    {
        using var keys = new LocalKeyProvider();
        var counter = new CountingMiddleware();
        await using SourceFlowTestHost host = await HostAsync(keys, p => p.Protect("accounts"), configure: sf => sf.UseMiddleware(_ => counter, PipelineStage.Handle));
        using PayloadProtector sender = Protector(keys);
        MessageEnvelope sealedEnvelope = await sender.ProtectAsync(host.Runtime.CreateEnvelope(new OpenAccount(AccountId.New(), "Ada", 5m)), Ct);
        byte[] tampered = sealedEnvelope.Body.ToArray();
        tampered[^2] ^= 0x40;

        host.Broker.Send("accounts", sealedEnvelope with { Body = tampered });
        await host.WaitForIdleAsync();

        DeadLetter parked = (await host.DeadLetters.ListAsync(new DeadLetterQuery(), Ct)).ShouldHaveSingleItem();
        parked.Reason.ShouldBe("DecryptionFailed");
        parked.Attempts.ShouldBe(1, "permanent: no delayed retries");
        parked.Envelope.Protection.ShouldNotBeNull("the parked envelope stays protected");
        counter.Calls.ShouldBe(0);
    }

    [Fact]
    [Trait("Id", "UT-SEC-17")]
    public async Task KeyServiceOutage_OnReceive_IsRetried_ThenSucceeds()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddYears(56));
        using var keys = new LocalKeyProvider();
        using PayloadProtector sender = Protector(keys);
        await using SourceFlowTestHost host = await HostAsync(keys, p => p.Protect("accounts"), time: time, configure: sf => sf.Configure(o =>
        {
            o.DelayedRetries.Clear();
            o.DelayedRetries.Add(TimeSpan.FromSeconds(10));
        }));
        var id = AccountId.New();
        MessageEnvelope sealedEnvelope = await sender.ProtectAsync(host.Runtime.CreateEnvelope(new OpenAccount(id, "Ada", 5m)), Ct);

        keys.Failure = new KeyProviderException("KMS unavailable", isTransient: true);
        host.Broker.Send("accounts", sealedEnvelope);
        await host.WaitForIdleAsync();
        keys.Failure = null;
        time.Advance(TimeSpan.FromSeconds(11));
        await host.WaitForIdleAsync();

        (await host.EventsAsync(StreamId.For(BankAccount.AggregateName, id))).ShouldHaveSingleItem();
        (await host.DeadLetters.ListAsync(new DeadLetterQuery(), Ct)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Id", "UT-SEC-18")]
    public async Task KeyServiceOutage_OnSend_FailsTheCommand_AndCommitsNothing()
    {
        using var keys = new LocalKeyProvider();
        await using SourceFlowTestHost host = await HostAsync(keys, p => p.Protect("accounts.events"), relay: false);
        keys.Failure = new KeyProviderException("KMS unavailable", isTransient: true);
        var id = AccountId.New();

        await Should.ThrowAsync<KeyProviderException>(async () => await host.SendAsync(new OpenAccount(id, "Ada", 5m), cancellationToken: Ct));

        host.Store.OutboxSnapshot().ShouldBeEmpty();
        (await host.EventsAsync(StreamId.For(BankAccount.AggregateName, id))).ShouldBeEmpty("the event and its outbox row commit together, or not at all");
    }

    [Fact]
    [Trait("Id", "UT-SEC-19")]
    [Trait("Covers", "doc 10 3")]
    public async Task RequireOnReceive_ParksUnprotectedMessages()
    {
        using var keys = new LocalKeyProvider();
        await using SourceFlowTestHost host = await HostAsync(keys, p =>
        {
            p.Protect("accounts");
            p.RequireOnReceive = true;
        });

        host.Broker.Send("accounts", host.Runtime.CreateEnvelope(new OpenAccount(AccountId.New(), "Ada", 5m)));
        await host.WaitForIdleAsync();

        (await host.DeadLetters.ListAsync(new DeadLetterQuery(), Ct)).ShouldHaveSingleItem().Reason.ShouldBe("ProtectionRequired");
    }

    [Fact]
    [Trait("Id", "UT-SEC-20")]
    public async Task Protection_WithoutAKeyProvider_FailsAtStartup()
    {
        InvalidOperationException e = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await SourceFlowTestHost.StartAsync(sf =>
            {
                sf.ServiceName = "accounts";
                sf.AddModule<AccountsModule>();
                sf.Security(s => s.Protect(p => p.ProtectAll()));
            }, cancellationToken: Ct));

        e.Message.ShouldContain("key provider");
    }

    // ================================================================ helpers

    private static PayloadProtector Protector(IKeyProvider keys, Action<PayloadProtectionOptions>? configure = null, TimeProvider? time = null)
    {
        var options = new PayloadProtectionOptions().ProtectAll();
        configure?.Invoke(options);
        return new PayloadProtector(keys, options, time);
    }

    private static MessageEnvelope Envelope(string json)
    {
        return new MessageEnvelope
        {
            MessageId = MessageIds.New(TimeProvider.System),
            Type = "bank.account.opened",
            TypeVersion = 1,
            Source = "accounts",
            Time = TimeProvider.System.GetUtcNow(),
            Body = Encoding.UTF8.GetBytes(json),
        };
    }

    private static Task<SourceFlowTestHost> HostAsync(IKeyProvider keys, Action<PayloadProtectionOptions> protect, bool relay = true, string? publishAlso = null, TimeProvider? time = null, Action<SourceFlowBuilder>? configure = null)
    {
        return SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.Routing(r =>
            {
                r.Publish<AccountOpened>().AsIs().To("accounts.events");
                if (publishAlso is not null)
                {
                    r.Publish<AccountOpened>().AsIs().To(publishAlso);
                }
            });
            sf.Security(s => s.UseKeyProvider(keys).Protect(protect));
            if (!relay)
            {
                sf.Roles = SourceFlowRoles.Api;
            }

            configure?.Invoke(sf);
        }, time, cancellationToken: Ct);
    }

    private sealed class RecordingProvider(IKeyProvider inner) : IKeyProvider
    {
        public List<DataKey> Issued { get; } = [];

        public string Name => inner.Name;

        public async ValueTask<DataKey> GenerateDataKeyAsync(CancellationToken cancellationToken)
        {
            DataKey key = await inner.GenerateDataKeyAsync(cancellationToken);
            Issued.Add(key);
            return key;
        }

        public ValueTask<byte[]> UnwrapDataKeyAsync(string keyReference, ReadOnlyMemory<byte> wrapped, CancellationToken cancellationToken)
        {
            return inner.UnwrapDataKeyAsync(keyReference, wrapped, cancellationToken);
        }
    }

    private sealed class CountingMiddleware : IMessageMiddleware
    {
        public int Calls { get; private set; }

        public ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
        {
            Calls++;
            return next(context);
        }
    }
}

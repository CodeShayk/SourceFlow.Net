using System.Collections.Concurrent;
using System.Text;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Amazon.Runtime;
using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SourceFlow.Cloud.Aws;
using SourceFlow.Cloud.Tests.Aws;
using SourceFlow.Cloud.Tests.Azure;
using SourceFlow.Cloud.Tests.Gcp;
using SourceFlow.Messaging;
using SourceFlow.Runtime;
using SourceFlow.Security;
using SourceFlow.Storage;
using SourceFlow.Storage.Tests.PostgreSql;
using SourceFlow.Transport;

namespace SourceFlow.EndToEnd.Tests;

/// <summary>
/// Payload protection end to end (doc 10 §3, WP-5.1; plan 06 TCK-41..45 with protection on): Accounts publishes its
/// integration event protected, Notifications and Loyalty decrypt it and welcome each customer once; the broker only ever
/// carries ciphertext; a tampered message is parked as <c>DecryptionFailed</c>. In memory with the local key provider,
/// and on LocalStack with AWS KMS.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "EndToEnd")]
[Trait("Requires", "Docker")]
public sealed class ProtectionScenarios(PostgreSqlServer postgreSql, LocalStackServer localStack, PubSubEmulatorServer pubSub, ServiceBusEmulatorServer serviceBus)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Servers Servers => new(postgreSql, localStack, pubSub, serviceBus);

    [Fact]
    [Trait("Id", "TCK-41")]
    [Trait("Id", "TCK-43")]
    [Trait("Covers", "doc 10 3")]
    public async Task ProtectedIntegrationEvents_InMemory()
    {
        using var keys = new LocalKeyProvider();
        await RunAsync(Broker.InMemory, keys);
    }

    [Fact]
    [Trait("Id", "TCK-41")]
    [Trait("Id", "TCK-43")]
    [Trait("Cloud", "Aws")]
    [Trait("Requires", "LocalStack")]
    [Trait("Covers", "doc 10 3")]
    public async Task ProtectedIntegrationEvents_Aws_WithKms()
    {
        await localStack.EnsureStartedAsync();
        using var kms = new AmazonKeyManagementServiceClient(new BasicAWSCredentials("test", "test"),
            new AmazonKeyManagementServiceConfig { ServiceURL = localStack.Endpoint.ToString(), AuthenticationRegion = LocalStackServer.Region });
        string key = (await kms.CreateKeyAsync(new CreateKeyRequest { Description = "sourceflow-e2e" }, Ct)).KeyMetadata.Arn;
        using var keys = new AwsKmsKeyProvider(kms, key);
        await RunAsync(Broker.Aws, keys);
    }

    private async Task RunAsync(Broker broker, IKeyProvider keys)
    {
        var published = new ConcurrentQueue<MessageEnvelope>();
        SpyTransport? spy = null;
        await using CrossServiceSystem system = await CrossServiceSystem.StartAsync(Servers, broker,
            inner => spy = new SpyTransport(inner, published),
            sf => sf.Security(s => s.UseKeyProvider(keys).Protect(p => p.Protect(CrossServiceSystem.Topic))));
        ICommandSender accounts = system.Accounts.Services.GetRequiredService<ICommandSender>();
        AccountId[] opened = [.. Enumerable.Range(0, 3).Select(_ => AccountId.New())];

        foreach (AccountId id in opened)
        {
            (await accounts.SendAsync(new OpenAccount(id, "Ada Lovelace", 10m), null, Ct)).Status.ShouldBe(CommandStatus.Accepted);
        }

        foreach (IHost subscriber in (IHost[])[system.Notifications[0], system.Loyalty])
        {
            foreach (AccountId id in opened)
            {
                await EventuallyAsync(async () => (await CrossServiceSystem.EventsAsync(subscriber, CrossServiceSystem.Inbox(id))).Count > 0, $"the welcome for {id}");
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(1), TimeProvider.System, Ct);
        foreach (AccountId id in opened)
        {
            (await CrossServiceSystem.EventsAsync(system.Loyalty, CrossServiceSystem.Inbox(id))).Count.ShouldBe(1, "decrypted and handled once");
        }

        // The broker only ever carried ciphertext.
        published.Count.ShouldBeGreaterThanOrEqualTo(opened.Length);
        foreach (MessageEnvelope envelope in published)
        {
            envelope.Protection.ShouldNotBeNull();
            Encoding.UTF8.GetString(envelope.Body.Span).ShouldNotContain("Lovelace");
        }

        // A tampered copy (under a new id, so the inbox does not drop it as a duplicate) is parked, not retried.
        MessageEnvelope original = published.First();
        byte[] body = original.Body.ToArray();
        body[^1] ^= 0x01;
        await spy!.Inner.PublishAsync(CrossServiceSystem.Topic, [original with { Body = body }], Ct);
        IDeadLetterManager deadLetters = system.Loyalty.Services.GetRequiredService<IDeadLetterManager>();
        await EventuallyAsync(async () => (await deadLetters.ListAsync(new DeadLetterQuery(), Ct)).Any(d => d.Reason == "DecryptionFailed"), "the tampered message parked as DecryptionFailed");
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(60);
        while (!await condition())
        {
            if (TimeProvider.System.GetUtcNow() > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, Ct);
        }
    }

    // Records what Accounts hands the broker.
    private sealed class SpyTransport(ITransport inner, ConcurrentQueue<MessageEnvelope> published) : ITransport, IAsyncDisposable
    {
        public ITransport Inner => inner;

        public string SystemName => inner.SystemName;

        public TransportCapabilities Capabilities => inner.Capabilities;

        public ValueTask SendAsync(string endpoint, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken) => inner.SendAsync(endpoint, messages, cancellationToken);

        public async ValueTask PublishAsync(string topic, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
        {
            foreach (MessageEnvelope message in messages)
            {
                published.Enqueue(message);
            }

            await inner.PublishAsync(topic, messages, cancellationToken);
        }

        public ValueTask<ITransportReceiver> CreateReceiverAsync(ReceiverOptions options, CancellationToken cancellationToken) => inner.CreateReceiverAsync(options, cancellationToken);

        public ValueTask<ITransportReceiver> CreateDeadLetterReceiverAsync(string endpoint, CancellationToken cancellationToken) => inner.CreateDeadLetterReceiverAsync(endpoint, cancellationToken);

        public ValueTask ProvisionAsync(TransportTopology topology, ProvisioningMode mode, CancellationToken cancellationToken) => inner.ProvisionAsync(topology, mode, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (inner is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }
        }
    }
}

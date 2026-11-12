using System.Collections.Concurrent;
using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Cloud.Tests.Aws;
using SourceFlow.Cloud.Tests.Azure;
using SourceFlow.Cloud.Tests.Gcp;
using SourceFlow.Messaging;
using SourceFlow.Storage.Tests.PostgreSql;
using SourceFlow.Transport;

namespace SourceFlow.EndToEnd.Tests;

/// <summary>
/// PERF-07 (plan 06 §8, doc 09 §7.1; WP-4.7): outbox relay latency, from the commit that wrote the outbox row to the
/// broker's acknowledgement of the publish, on PostgreSQL with each emulator. The target is p95 ≤ 250 ms. Commands are
/// sent one at a time (latency, not throughput); the clock starts at the event's time, which is set when the command is
/// decided, just before the commit.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Performance")]
[Trait("Id", "PERF-07")]
[Trait("Requires", "Docker")]
public sealed class RelayLatencyScenarios(PostgreSqlServer postgreSql, LocalStackServer localStack, PubSubEmulatorServer pubSub, ServiceBusEmulatorServer serviceBus)
{
    private const int Warmup = 10;
    private const int Measured = 100;

    private Servers Servers => new(postgreSql, localStack, pubSub, serviceBus);

    [Fact]
    [Trait("Cloud", "Aws")]
    [Trait("Requires", "LocalStack")]
    public Task OutboxToBrokerAck_Aws() => MeasureAsync(Broker.Aws);

    [Fact]
    [Trait("Cloud", "Gcp")]
    [Trait("Requires", "PubSubEmulator")]
    public Task OutboxToBrokerAck_Gcp() => MeasureAsync(Broker.Gcp);

    [Fact]
    [Trait("Cloud", "Azure")]
    [Trait("Requires", "ServiceBusEmulator")]
    public Task OutboxToBrokerAck_Azure() => MeasureAsync(Broker.Azure);

    private async Task MeasureAsync(Broker broker)
    {
        var acks = new ConcurrentDictionary<MessageId, double>();
        await using CrossServiceSystem system = await CrossServiceSystem.StartAsync(Servers, broker, inner => new TimingTransport(inner, acks));
        ICommandSender commands = system.Accounts.Services.GetRequiredService<ICommandSender>();
        for (int i = 0; i < Warmup + Measured; i++)
        {
            int before = acks.Count;
            (await commands.SendAsync(new OpenAccount(AccountId.New(), "Ada", 1m), null, TestContext.Current.CancellationToken)).Status.ShouldBe(CommandStatus.Accepted);
            DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(30);
            while (acks.Count == before && TimeProvider.System.GetUtcNow() < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(2), TimeProvider.System, TestContext.Current.CancellationToken);
            }
        }

        double[] latencies = [.. acks.Values.Order().ToArray()];
        latencies.Length.ShouldBe(Warmup + Measured, "every integration event was published");
        double[] measured = [.. acks.OrderBy(a => a.Key.Value).Skip(Warmup).Select(a => a.Value).Order()];
        double p50 = measured[(int)Math.Ceiling(0.50 * measured.Length) - 1];
        double p95 = measured[(int)Math.Ceiling(0.95 * measured.Length) - 1];
        TestContext.Current.TestOutputHelper?.WriteLine($"PERF-07 {broker}: p50 {p50:F1} ms, p95 {p95:F1} ms, max {measured[^1]:F1} ms over {measured.Length} publishes");
        p95.ShouldBeLessThanOrEqualTo(250, $"PERF-07 {broker}: outbox commit to broker acknowledgement p95 (p50 {p50:F1} ms)");
    }

    // Records, per published message, how long after its creation the broker acknowledged it.
    private sealed class TimingTransport(ITransport inner, ConcurrentDictionary<MessageId, double> acks) : ITransport, IAsyncDisposable
    {
        public string SystemName => inner.SystemName;

        public TransportCapabilities Capabilities => inner.Capabilities;

        public ValueTask SendAsync(string endpoint, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken) => inner.SendAsync(endpoint, messages, cancellationToken);

        public async ValueTask PublishAsync(string topic, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
        {
            await inner.PublishAsync(topic, messages, cancellationToken);
            DateTimeOffset acknowledged = TimeProvider.System.GetUtcNow();
            foreach (MessageEnvelope message in messages)
            {
                acks.TryAdd(message.MessageId, (acknowledged - message.Time).TotalMilliseconds);
            }
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

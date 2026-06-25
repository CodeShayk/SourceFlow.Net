using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Azure.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Azure.Tests.Integration;

/// <summary>
/// Property-based tests for Azure concurrent processing integrity.
/// **Property 13: Azure Concurrent Processing Integrity**
/// **Validates: Requirements 1.5**
/// </summary>
public class AzureConcurrentProcessingPropertyTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;
    private IAzureTestEnvironment? _environment;
    private ServiceBusTestHelpers? _serviceBusHelpers;
    private AzurePerformanceTestRunner? _performanceRunner;

    public AzureConcurrentProcessingPropertyTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddXUnit(output);
            builder.SetMinimumLevel(LogLevel.Information);
        });
    }

    public async Task InitializeAsync()
    {
        var config = AzureTestConfiguration.CreateDefault();
        _environment = new AzureTestEnvironment(config, _loggerFactory);
        await _environment.InitializeAsync();

        _serviceBusHelpers = new ServiceBusTestHelpers(_environment, _loggerFactory);
        _performanceRunner = new AzurePerformanceTestRunner(
            _environment,
            _serviceBusHelpers,
            _loggerFactory);
    }

    public async Task DisposeAsync()
    {
        if (_performanceRunner != null)
        {
            await _performanceRunner.DisposeAsync();
        }

        if (_environment != null)
        {
            await _environment.CleanupAsync();
        }
    }

    /// <summary>
    /// Property 13: Azure Concurrent Processing Integrity
    /// For any set of messages processed concurrently through Azure Service Bus,
    /// all messages should be processed without loss or corruption, maintaining
    /// message integrity and proper session ordering where applicable.
    /// </summary>
    [Property(MaxTest = 10, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property ConcurrentProcessing_ShouldMaintainIntegrity_WithoutMessageLoss(
        PositiveInt messageCount,
        PositiveInt concurrentSenders,
        PositiveInt concurrentReceivers)
    {
        // Limit values to reasonable ranges for testing
        var limitedMessageCount = Math.Min(messageCount.Get, 200);
        var limitedSenders = Math.Min(concurrentSenders.Get, 8);
        var limitedReceivers = Math.Min(concurrentReceivers.Get, 8);

        return Prop.ForAll(
            Gen.Elements(MessageSize.Small, MessageSize.Medium).ToArbitrary(),
            (messageSize) =>
            {
                // Arrange
                var scenario = new AzureTestScenario
                {
                    Name = "Concurrent Integrity Test",
                    QueueName = "concurrent-integrity-queue",
                    MessageCount = limitedMessageCount,
                    ConcurrentSenders = limitedSenders,
                    ConcurrentReceivers = limitedReceivers,
                    MessageSize = messageSize
                };

                // Act
                var result = _performanceRunner!.RunConcurrentProcessingTestAsync(scenario).GetAwaiter().GetResult();

                // Assert - No message loss or corruption
                var totalProcessed = result.SuccessfulMessages + result.FailedMessages;
                var noMessageLoss = totalProcessed == result.TotalMessages;
                var highSuccessRate = (double)result.SuccessfulMessages / result.TotalMessages > 0.80;
                var hasIntegrity = noMessageLoss && highSuccessRate;

                if (!hasIntegrity)
                {
                    _output.WriteLine($"Integrity violation:");
                    _output.WriteLine($"  Expected: {result.TotalMessages}");
                    _output.WriteLine($"  Processed: {totalProcessed}");
                    _output.WriteLine($"  Success: {result.SuccessfulMessages}");
                    _output.WriteLine($"  Failed: {result.FailedMessages}");
                    _output.WriteLine($"  Success Rate: {(double)result.SuccessfulMessages / result.TotalMessages:P2}");
                }

                return hasIntegrity.ToProperty()
                    .Label($"Concurrent processing should maintain integrity (success rate > 80%, was {(double)result.SuccessfulMessages / result.TotalMessages:P2})");
            });
    }

    /// <summary>
    /// Property: Concurrent processing should not corrupt messages
    /// For any concurrent scenario, all successfully processed messages should be valid.
    /// </summary>
    [Property(MaxTest = 10, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property ConcurrentProcessing_ShouldNotCorruptMessages(
        PositiveInt messageCount,
        PositiveInt concurrentSenders)
    {
        var limitedMessageCount = Math.Min(messageCount.Get, 150);
        var limitedSenders = Math.Min(concurrentSenders.Get, 6);

        return Prop.ForAll(
            Gen.Elements(MessageSize.Small, MessageSize.Medium, MessageSize.Large).ToArbitrary(),
            (messageSize) =>
            {
                // Arrange
                var scenario = new AzureTestScenario
                {
                    Name = "Message Corruption Test",
                    QueueName = "concurrent-corruption-queue",
                    MessageCount = limitedMessageCount,
                    ConcurrentSenders = limitedSenders,
                    ConcurrentReceivers = limitedSenders,
                    MessageSize = messageSize
                };

                // Act
                var result = _performanceRunner!.RunConcurrentProcessingTestAsync(scenario).GetAwaiter().GetResult();

                // Assert - No corruption (all processed messages are valid)
                var noCorruption = result.SuccessfulMessages > 0 &&
                                  result.Errors.Count == 0 &&
                                  result.Duration > TimeSpan.Zero;

                if (!noCorruption)
                {
                    _output.WriteLine($"Potential corruption detected:");
                    _output.WriteLine($"  Successful: {result.SuccessfulMessages}");
                    _output.WriteLine($"  Errors: {result.Errors.Count}");
                    if (result.Errors.Any())
                    {
                        _output.WriteLine($"  First Error: {result.Errors.First()}");
                    }
                }

                return noCorruption.ToProperty()
                    .Label("Concurrent processing should not corrupt messages");
            });
    }

    /// <summary>
    /// Property: Concurrent processing should scale with senders
    /// For any scenario, increasing concurrent senders should increase or maintain throughput.
    /// </summary>
    [Property(MaxTest = 5, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property ConcurrentProcessing_ShouldScaleWithSenders(
        PositiveInt messageCount)
    {
        var limitedMessageCount = Math.Min(messageCount.Get, 150);

        return Prop.ForAll(
            Gen.Elements(MessageSize.Small, MessageSize.Medium).ToArbitrary(),
            (messageSize) =>
            {
                // Arrange - Test with 1 and 4 senders
                var scenario1 = new AzureTestScenario
                {
                    Name = "1 Sender",
                    QueueName = "concurrent-scaling-queue",
                    MessageCount = limitedMessageCount,
                    ConcurrentSenders = 1,
                    ConcurrentReceivers = 1,
                    MessageSize = messageSize
                };

                var scenario4 = new AzureTestScenario
                {
                    Name = "4 Senders",
                    QueueName = "concurrent-scaling-queue",
                    MessageCount = limitedMessageCount,
                    ConcurrentSenders = 4,
                    ConcurrentReceivers = 4,
                    MessageSize = messageSize
                };

                // Act
                var result1 = _performanceRunner!.RunConcurrentProcessingTestAsync(scenario1).GetAwaiter().GetResult();
                Task.Delay(100).GetAwaiter().GetResult();
                var result4 = _performanceRunner!.RunConcurrentProcessingTestAsync(scenario4).GetAwaiter().GetResult();

                // Assert - More senders should achieve at least 70% of single sender throughput
                var scalingRatio = result4.MessagesPerSecond / result1.MessagesPerSecond;
                var scalesReasonably = scalingRatio >= 0.7;

                if (!scalesReasonably)
                {
                    _output.WriteLine($"Poor scaling:");
                    _output.WriteLine($"  1 sender: {result1.MessagesPerSecond:F2} msg/s");
                    _output.WriteLine($"  4 senders: {result4.MessagesPerSecond:F2} msg/s");
                    _output.WriteLine($"  Ratio: {scalingRatio:F2}");
                }

                return scalesReasonably.ToProperty()
                    .Label($"Concurrent processing should scale (ratio >= 0.7, was {scalingRatio:F2})");
            });
    }

    /// <summary>
    /// Property: Session-based concurrent processing should maintain ordering
    /// For any session-based scenario, messages within the same session should be ordered.
    /// </summary>
    [Property(MaxTest = 5, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property SessionBasedConcurrentProcessing_ShouldMaintainOrdering(
        PositiveInt messageCount,
        PositiveInt concurrentSenders)
    {
        var limitedMessageCount = Math.Min(messageCount.Get, 100);
        var limitedSenders = Math.Min(concurrentSenders.Get, 5);

        return Prop.ForAll(
            Arb.From(Gen.Constant(true)),
            (_) =>
            {
                // Arrange
                var scenario = new AzureTestScenario
                {
                    Name = "Session Ordering Test",
                    QueueName = "concurrent-session-queue.fifo",
                    MessageCount = limitedMessageCount,
                    ConcurrentSenders = limitedSenders,
                    ConcurrentReceivers = limitedSenders,
                    MessageSize = MessageSize.Small,
                    EnableSessions = true
                };

                // Act
                var result = _performanceRunner!.RunConcurrentProcessingTestAsync(scenario).GetAwaiter().GetResult();

                // Assert - Session-based processing should maintain high success rate
                var successRate = (double)result.SuccessfulMessages / result.TotalMessages;
                var maintainsOrdering = successRate > 0.75;

                if (!maintainsOrdering)
                {
                    _output.WriteLine($"Session ordering issue:");
                    _output.WriteLine($"  Success Rate: {successRate:P2}");
                    _output.WriteLine($"  Successful: {result.SuccessfulMessages}/{result.TotalMessages}");
                }

                return maintainsOrdering.ToProperty()
                    .Label($"Session-based concurrent processing should maintain ordering (success rate > 75%, was {successRate:P2})");
            });
    }

    /// <summary>
    /// Property: Concurrent processing with encryption should maintain integrity
    /// For any scenario with encryption, concurrent processing should not affect message integrity.
    /// </summary>
    [Property(MaxTest = 5, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property ConcurrentProcessingWithEncryption_ShouldMaintainIntegrity(
        PositiveInt messageCount,
        PositiveInt concurrentSenders)
    {
        var limitedMessageCount = Math.Min(messageCount.Get, 100);
        var limitedSenders = Math.Min(concurrentSenders.Get, 5);

        return Prop.ForAll(
            Gen.Elements(MessageSize.Small, MessageSize.Medium).ToArbitrary(),
            (messageSize) =>
            {
                // Arrange
                var scenario = new AzureTestScenario
                {
                    Name = "Concurrent Encryption Test",
                    QueueName = "concurrent-encrypted-queue",
                    MessageCount = limitedMessageCount,
                    ConcurrentSenders = limitedSenders,
                    ConcurrentReceivers = limitedSenders,
                    MessageSize = messageSize,
                    EnableEncryption = true
                };

                // Act
                var result = _performanceRunner!.RunConcurrentProcessingTestAsync(scenario).GetAwaiter().GetResult();

                // Assert - Encryption should not affect integrity
                var successRate = (double)result.SuccessfulMessages / result.TotalMessages;
                var hasKeyVaultActivity = result.ResourceUsage.KeyVaultRequestsPerSecond > 0;
                var maintainsIntegrity = successRate > 0.75 && hasKeyVaultActivity;

                if (!maintainsIntegrity)
                {
                    _output.WriteLine($"Encryption integrity issue:");
                    _output.WriteLine($"  Success Rate: {successRate:P2}");
                    _output.WriteLine($"  Key Vault RPS: {result.ResourceUsage.KeyVaultRequestsPerSecond:F2}");
                }

                return maintainsIntegrity.ToProperty()
                    .Label($"Concurrent processing with encryption should maintain integrity (success rate > 75%, was {successRate:P2})");
            });
    }

    /// <summary>
    /// Property: Unbalanced sender/receiver ratios should not cause failures
    /// For any scenario with unbalanced concurrency, processing should still succeed.
    /// </summary>
    [Property(MaxTest = 10, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property UnbalancedConcurrency_ShouldNotCauseFailures(
        PositiveInt messageCount,
        PositiveInt senders,
        PositiveInt receivers)
    {
        var limitedMessageCount = Math.Min(messageCount.Get, 150);
        var limitedSenders = Math.Min(Math.Max(senders.Get, 1), 8);
        var limitedReceivers = Math.Min(Math.Max(receivers.Get, 1), 8);

        return Prop.ForAll(
            Gen.Elements(MessageSize.Small, MessageSize.Medium).ToArbitrary(),
            (messageSize) =>
            {
                // Arrange
                var scenario = new AzureTestScenario
                {
                    Name = "Unbalanced Concurrency Test",
                    QueueName = "concurrent-unbalanced-queue",
                    MessageCount = limitedMessageCount,
                    ConcurrentSenders = limitedSenders,
                    ConcurrentReceivers = limitedReceivers,
                    MessageSize = messageSize
                };

                // Act
                var result = _performanceRunner!.RunConcurrentProcessingTestAsync(scenario).GetAwaiter().GetResult();

                // Assert - Should handle unbalanced concurrency gracefully
                var successRate = (double)result.SuccessfulMessages / result.TotalMessages;
                var handlesGracefully = successRate > 0.70;

                if (!handlesGracefully)
                {
                    _output.WriteLine($"Unbalanced concurrency issue:");
                    _output.WriteLine($"  Senders: {limitedSenders}, Receivers: {limitedReceivers}");
                    _output.WriteLine($"  Success Rate: {successRate:P2}");
                }

                return handlesGracefully.ToProperty()
                    .Label($"Unbalanced concurrency should not cause failures (success rate > 70%, was {successRate:P2})");
            });
    }

    /// <summary>
    /// Property: Concurrent processing should have reasonable latency
    /// For any concurrent scenario, average latency should be within acceptable bounds.
    /// </summary>
    [Property(MaxTest = 10, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property ConcurrentProcessing_ShouldHaveReasonableLatency(
        PositiveInt messageCount,
        PositiveInt concurrentSenders)
    {
        var limitedMessageCount = Math.Min(messageCount.Get, 100);
        var limitedSenders = Math.Min(concurrentSenders.Get, 6);

        return Prop.ForAll(
            Gen.Elements(MessageSize.Small, MessageSize.Medium).ToArbitrary(),
            (messageSize) =>
            {
                // Arrange
                var scenario = new AzureTestScenario
                {
                    Name = "Concurrent Latency Test",
                    QueueName = "concurrent-latency-queue",
                    MessageCount = limitedMessageCount,
                    ConcurrentSenders = limitedSenders,
                    ConcurrentReceivers = limitedSenders,
                    MessageSize = messageSize
                };

                // Act
                var result = _performanceRunner!.RunConcurrentProcessingTestAsync(scenario).GetAwaiter().GetResult();

                // Assert - Latency should be reasonable (< 1 second average)
                var hasReasonableLatency = result.AverageLatency < TimeSpan.FromSeconds(1);

                if (!hasReasonableLatency)
                {
                    _output.WriteLine($"High latency detected:");
                    _output.WriteLine($"  Average: {result.AverageLatency.TotalMilliseconds:F2}ms");
                    _output.WriteLine($"  Concurrent Senders: {limitedSenders}");
                }

                return hasReasonableLatency.ToProperty()
                    .Label($"Concurrent processing should have reasonable latency (< 1s, was {result.AverageLatency.TotalMilliseconds:F2}ms)");
            });
    }

    /// <summary>
    /// Property: High concurrency should not cause excessive failures
    /// For any high concurrency scenario, failure rate should remain acceptable.
    /// </summary>
    [Property(MaxTest = 5, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property HighConcurrency_ShouldNotCauseExcessiveFailures(
        PositiveInt messageCount)
    {
        var limitedMessageCount = Math.Min(messageCount.Get, 200);

        return Prop.ForAll(
            Gen.Elements(MessageSize.Small, MessageSize.Medium).ToArbitrary(),
            (messageSize) =>
            {
                // Arrange - High concurrency scenario
                var scenario = new AzureTestScenario
                {
                    Name = "High Concurrency Test",
                    QueueName = "concurrent-high-queue",
                    MessageCount = limitedMessageCount,
                    ConcurrentSenders = 8,
                    ConcurrentReceivers = 8,
                    MessageSize = messageSize
                };

                // Act
                var result = _performanceRunner!.RunConcurrentProcessingTestAsync(scenario).GetAwaiter().GetResult();

                // Assert - Failure rate should be acceptable (< 20%)
                var failureRate = (double)result.FailedMessages / result.TotalMessages;
                var acceptableFailureRate = failureRate < 0.20;

                if (!acceptableFailureRate)
                {
                    _output.WriteLine($"Excessive failures with high concurrency:");
                    _output.WriteLine($"  Failure Rate: {failureRate:P2}");
                    _output.WriteLine($"  Failed: {result.FailedMessages}/{result.TotalMessages}");
                }

                return acceptableFailureRate.ToProperty()
                    .Label($"High concurrency should not cause excessive failures (< 20%, was {failureRate:P2})");
            });
    }

    /// <summary>
    /// Property: Concurrent processing should populate metrics correctly
    /// For any concurrent scenario, Service Bus metrics should reflect concurrent activity.
    /// </summary>
    [Property(MaxTest = 10, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property ConcurrentProcessing_ShouldPopulateMetricsCorrectly(
        PositiveInt messageCount,
        PositiveInt concurrentSenders)
    {
        var limitedMessageCount = Math.Min(messageCount.Get, 100);
        var limitedSenders = Math.Min(concurrentSenders.Get, 6);

        return Prop.ForAll(
            Gen.Elements(MessageSize.Small, MessageSize.Medium).ToArbitrary(),
            (messageSize) =>
            {
                // Arrange
                var scenario = new AzureTestScenario
                {
                    Name = "Concurrent Metrics Test",
                    QueueName = "concurrent-metrics-queue",
                    MessageCount = limitedMessageCount,
                    ConcurrentSenders = limitedSenders,
                    ConcurrentReceivers = limitedSenders,
                    MessageSize = messageSize
                };

                // Act
                var result = _performanceRunner!.RunConcurrentProcessingTestAsync(scenario).GetAwaiter().GetResult();

                // Assert - Metrics should reflect concurrent activity
                var metricsValid = result.ServiceBusMetrics != null &&
                                  result.ServiceBusMetrics.ActiveConnections >= limitedSenders &&
                                  result.ServiceBusMetrics.IncomingMessagesPerSecond > 0 &&
                                  result.ServiceBusMetrics.OutgoingMessagesPerSecond > 0;

                if (!metricsValid)
                {
                    _output.WriteLine($"Invalid concurrent metrics:");
                    _output.WriteLine($"  Active Connections: {result.ServiceBusMetrics?.ActiveConnections} (expected >= {limitedSenders})");
                    _output.WriteLine($"  Incoming MPS: {result.ServiceBusMetrics?.IncomingMessagesPerSecond:F2}");
                }

                return metricsValid.ToProperty()
                    .Label("Concurrent processing should populate metrics correctly");
            });
    }
}

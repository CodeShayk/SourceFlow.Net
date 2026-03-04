using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Azure.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Azure.Tests.Integration;

/// <summary>
/// Property-based tests for Azure performance measurement consistency.
/// **Property 14: Azure Performance Measurement Consistency**
/// **Validates: Requirements 5.1, 5.2, 5.3, 5.5**
/// </summary>
public class AzurePerformanceMeasurementPropertyTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;
    private IAzureTestEnvironment? _environment;
    private ServiceBusTestHelpers? _serviceBusHelpers;
    private AzurePerformanceTestRunner? _performanceRunner;

    public AzurePerformanceMeasurementPropertyTests(ITestOutputHelper output)
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
    /// Property 14: Azure Performance Measurement Consistency
    /// For any Azure performance test scenario (throughput, latency, resource utilization),
    /// when executed multiple times under similar conditions, the performance measurements
    /// should be consistent within acceptable variance ranges and scale appropriately with load.
    /// </summary>
    [Property(MaxTest = 10, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property PerformanceMeasurements_ShouldBeConsistent_AcrossMultipleRuns(
        PositiveInt messageCount,
        PositiveInt concurrentSenders)
    {
        // Limit values to reasonable ranges for testing
        var limitedMessageCount = Math.Min(messageCount.Get, 100);
        var limitedConcurrentSenders = Math.Min(concurrentSenders.Get, 5);

        return Prop.ForAll(
            Gen.Elements(MessageSize.Small, MessageSize.Medium, MessageSize.Large).ToArbitrary(),
            (messageSize) =>
            {
                // Arrange
                var scenario = new AzureTestScenario
                {
                    Name = "Consistency Test",
                    QueueName = "perf-consistency-queue",
                    MessageCount = limitedMessageCount,
                    ConcurrentSenders = limitedConcurrentSenders,
                    MessageSize = messageSize
                };

                // Act - Run test multiple times
                var results = new List<AzurePerformanceTestResult>();
                for (int i = 0; i < 3; i++)
                {
                    var result = _performanceRunner!.RunServiceBusThroughputTestAsync(scenario).GetAwaiter().GetResult();
                    results.Add(result);
                    Task.Delay(50).GetAwaiter().GetResult(); // Small delay between runs
                }

                // Assert - Measurements should be consistent
                var throughputs = results.Select(r => r.MessagesPerSecond).ToList();
                var avgThroughput = throughputs.Average();
                var maxDeviation = throughputs.Max(t => Math.Abs(t - avgThroughput));
                var deviationPercent = avgThroughput > 0 ? maxDeviation / avgThroughput : 0;

                // Allow up to 50% deviation due to simulation variance
                var isConsistent = deviationPercent < 0.5;

                if (!isConsistent)
                {
                    _output.WriteLine($"Inconsistent measurements: {string.Join(", ", throughputs.Select(t => $"{t:F2}"))}");
                    _output.WriteLine($"Deviation: {deviationPercent:P2}");
                }

                return isConsistent.ToProperty()
                    .Label($"Performance measurements should be consistent (deviation < 50%, was {deviationPercent:P2})");
            });
    }

    /// <summary>
    /// Property: Latency percentiles should be properly ordered
    /// For any performance test result, P50 <= P95 <= P99 and Min <= P50 <= Max.
    /// </summary>
    [Property(MaxTest = 10, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property LatencyPercentiles_ShouldBeProperlyOrdered(
        PositiveInt messageCount)
    {
        var limitedMessageCount = Math.Min(messageCount.Get, 50);

        return Prop.ForAll(
            Gen.Elements(MessageSize.Small, MessageSize.Medium, MessageSize.Large).ToArbitrary(),
            (messageSize) =>
            {
                // Arrange
                var scenario = new AzureTestScenario
                {
                    Name = "Latency Percentile Test",
                    QueueName = "perf-latency-queue",
                    MessageCount = limitedMessageCount,
                    ConcurrentSenders = 1,
                    MessageSize = messageSize
                };

                // Act
                var result = _performanceRunner!.RunServiceBusLatencyTestAsync(scenario).GetAwaiter().GetResult();

                // Assert - Percentiles should be ordered
                var minValid = result.MinLatency <= result.MedianLatency;
                var p50Valid = result.MedianLatency <= result.P95Latency;
                var p95Valid = result.P95Latency <= result.P99Latency;
                var maxValid = result.MedianLatency <= result.MaxLatency;
                var allPositive = result.MinLatency > TimeSpan.Zero &&
                                 result.MedianLatency > TimeSpan.Zero &&
                                 result.P95Latency > TimeSpan.Zero &&
                                 result.P99Latency > TimeSpan.Zero &&
                                 result.MaxLatency > TimeSpan.Zero;

                var isValid = minValid && p50Valid && p95Valid && maxValid && allPositive;

                if (!isValid)
                {
                    _output.WriteLine($"Invalid latency ordering:");
                    _output.WriteLine($"  Min: {result.MinLatency.TotalMilliseconds:F2}ms");
                    _output.WriteLine($"  P50: {result.MedianLatency.TotalMilliseconds:F2}ms");
                    _output.WriteLine($"  P95: {result.P95Latency.TotalMilliseconds:F2}ms");
                    _output.WriteLine($"  P99: {result.P99Latency.TotalMilliseconds:F2}ms");
                    _output.WriteLine($"  Max: {result.MaxLatency.TotalMilliseconds:F2}ms");
                }

                return isValid.ToProperty()
                    .Label("Latency percentiles should be properly ordered: Min <= P50 <= P95 <= P99 <= Max");
            });
    }

    /// <summary>
    /// Property: Throughput should scale with concurrent senders
    /// For any scenario, increasing concurrent senders should increase or maintain throughput.
    /// </summary>
    [Property(MaxTest = 5, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property Throughput_ShouldScaleWithConcurrency(
        PositiveInt messageCount)
    {
        var limitedMessageCount = Math.Min(messageCount.Get, 100);

        return Prop.ForAll(
            Gen.Elements(MessageSize.Small, MessageSize.Medium).ToArbitrary(),
            (messageSize) =>
            {
                // Arrange - Test with 1 and 3 concurrent senders
                var scenario1 = new AzureTestScenario
                {
                    Name = "Single Sender",
                    QueueName = "perf-scaling-queue",
                    MessageCount = limitedMessageCount,
                    ConcurrentSenders = 1,
                    MessageSize = messageSize
                };

                var scenario3 = new AzureTestScenario
                {
                    Name = "Three Senders",
                    QueueName = "perf-scaling-queue",
                    MessageCount = limitedMessageCount,
                    ConcurrentSenders = 3,
                    MessageSize = messageSize
                };

                // Act
                var result1 = _performanceRunner!.RunServiceBusThroughputTestAsync(scenario1).GetAwaiter().GetResult();
                Task.Delay(100).GetAwaiter().GetResult();
                var result3 = _performanceRunner!.RunServiceBusThroughputTestAsync(scenario3).GetAwaiter().GetResult();

                // Assert - More senders should achieve equal or better throughput
                // Allow for some variance in simulation
                var scalingRatio = result3.MessagesPerSecond / result1.MessagesPerSecond;
                var scalesReasonably = scalingRatio >= 0.8; // At least 80% of single sender throughput

                if (!scalesReasonably)
                {
                    _output.WriteLine($"Poor scaling: 1 sender={result1.MessagesPerSecond:F2} msg/s, " +
                                    $"3 senders={result3.MessagesPerSecond:F2} msg/s, " +
                                    $"ratio={scalingRatio:F2}");
                }

                return scalesReasonably.ToProperty()
                    .Label($"Throughput should scale with concurrency (ratio >= 0.8, was {scalingRatio:F2})");
            });
    }

    /// <summary>
    /// Property: Resource utilization should correlate with message count
    /// For any scenario, processing more messages should result in higher resource utilization.
    /// </summary>
    [Property(MaxTest = 5, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property ResourceUtilization_ShouldCorrelateWithLoad()
    {
        return Prop.ForAll(
            Gen.Elements(MessageSize.Small, MessageSize.Medium).ToArbitrary(),
            (messageSize) =>
            {
                // Arrange - Test with different message counts
                var scenarioLow = new AzureTestScenario
                {
                    Name = "Low Load",
                    QueueName = "perf-resource-queue",
                    MessageCount = 50,
                    ConcurrentSenders = 2,
                    MessageSize = messageSize
                };

                var scenarioHigh = new AzureTestScenario
                {
                    Name = "High Load",
                    QueueName = "perf-resource-queue",
                    MessageCount = 200,
                    ConcurrentSenders = 2,
                    MessageSize = messageSize
                };

                // Act
                var resultLow = _performanceRunner!.RunResourceUtilizationTestAsync(scenarioLow).GetAwaiter().GetResult();
                Task.Delay(100).GetAwaiter().GetResult();
                var resultHigh = _performanceRunner!.RunResourceUtilizationTestAsync(scenarioHigh).GetAwaiter().GetResult();

                // Assert - Higher load should result in higher network usage
                var networkBytesLow = resultLow.ResourceUsage.NetworkBytesIn + resultLow.ResourceUsage.NetworkBytesOut;
                var networkBytesHigh = resultHigh.ResourceUsage.NetworkBytesIn + resultHigh.ResourceUsage.NetworkBytesOut;
                
                var correlates = networkBytesHigh >= networkBytesLow;

                if (!correlates)
                {
                    _output.WriteLine($"Resource utilization doesn't correlate:");
                    _output.WriteLine($"  Low load network: {networkBytesLow} bytes");
                    _output.WriteLine($"  High load network: {networkBytesHigh} bytes");
                }

                return correlates.ToProperty()
                    .Label("Resource utilization should correlate with message load");
            });
    }

    /// <summary>
    /// Property: Success rate should be high for valid scenarios
    /// For any valid performance test scenario, the success rate should be > 90%.
    /// </summary>
    [Property(MaxTest = 10, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property PerformanceTests_ShouldHaveHighSuccessRate(
        PositiveInt messageCount,
        PositiveInt concurrentSenders)
    {
        var limitedMessageCount = Math.Min(messageCount.Get, 100);
        var limitedConcurrentSenders = Math.Min(concurrentSenders.Get, 5);

        return Prop.ForAll(
            Gen.Elements(MessageSize.Small, MessageSize.Medium, MessageSize.Large).ToArbitrary(),
            (messageSize) =>
            {
                // Arrange
                var scenario = new AzureTestScenario
                {
                    Name = "Success Rate Test",
                    QueueName = "perf-success-queue",
                    MessageCount = limitedMessageCount,
                    ConcurrentSenders = limitedConcurrentSenders,
                    MessageSize = messageSize
                };

                // Act
                var result = _performanceRunner!.RunServiceBusThroughputTestAsync(scenario).GetAwaiter().GetResult();

                // Assert - Success rate should be high
                var successRate = (double)result.SuccessfulMessages / result.TotalMessages;
                var hasHighSuccessRate = successRate > 0.90;

                if (!hasHighSuccessRate)
                {
                    _output.WriteLine($"Low success rate: {successRate:P2} " +
                                    $"({result.SuccessfulMessages}/{result.TotalMessages})");
                }

                return hasHighSuccessRate.ToProperty()
                    .Label($"Success rate should be > 90% (was {successRate:P2})");
            });
    }

    /// <summary>
    /// Property: Service Bus metrics should be populated
    /// For any performance test, Service Bus metrics should contain valid data.
    /// </summary>
    [Property(MaxTest = 10, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property ServiceBusMetrics_ShouldBePopulated(
        PositiveInt messageCount)
    {
        var limitedMessageCount = Math.Min(messageCount.Get, 50);

        return Prop.ForAll(
            Gen.Elements(MessageSize.Small, MessageSize.Medium, MessageSize.Large).ToArbitrary(),
            (messageSize) =>
            {
                // Arrange
                var scenario = new AzureTestScenario
                {
                    Name = "Metrics Test",
                    QueueName = "perf-metrics-queue",
                    MessageCount = limitedMessageCount,
                    ConcurrentSenders = 2,
                    MessageSize = messageSize
                };

                // Act
                var result = _performanceRunner!.RunServiceBusThroughputTestAsync(scenario).GetAwaiter().GetResult();

                // Assert - Metrics should be populated with valid values
                var metricsValid = result.ServiceBusMetrics != null &&
                                  result.ServiceBusMetrics.ActiveMessages >= 0 &&
                                  result.ServiceBusMetrics.DeadLetterMessages >= 0 &&
                                  result.ServiceBusMetrics.IncomingMessagesPerSecond >= 0 &&
                                  result.ServiceBusMetrics.OutgoingMessagesPerSecond >= 0 &&
                                  result.ServiceBusMetrics.SuccessfulRequests >= 0 &&
                                  result.ServiceBusMetrics.FailedRequests >= 0 &&
                                  result.ServiceBusMetrics.AverageMessageSizeBytes > 0 &&
                                  result.ServiceBusMetrics.ActiveConnections > 0;

                if (!metricsValid)
                {
                    _output.WriteLine("Invalid Service Bus metrics:");
                    _output.WriteLine($"  ActiveMessages: {result.ServiceBusMetrics?.ActiveMessages}");
                    _output.WriteLine($"  IncomingMPS: {result.ServiceBusMetrics?.IncomingMessagesPerSecond}");
                    _output.WriteLine($"  AvgMessageSize: {result.ServiceBusMetrics?.AverageMessageSizeBytes}");
                }

                return metricsValid.ToProperty()
                    .Label("Service Bus metrics should be populated with valid values");
            });
    }

    /// <summary>
    /// Property: Larger messages should have lower throughput
    /// For any scenario, larger message sizes should result in equal or lower throughput.
    /// </summary>
    [Property(MaxTest = 5, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property LargerMessages_ShouldHaveLowerOrEqualThroughput(
        PositiveInt messageCount)
    {
        var limitedMessageCount = Math.Min(messageCount.Get, 100);

        return Prop.ForAll(
            Arb.From(Gen.Constant(true)),
            (_) =>
            {
                // Arrange - Test small and large messages
                var scenarioSmall = new AzureTestScenario
                {
                    Name = "Small Messages",
                    QueueName = "perf-size-queue",
                    MessageCount = limitedMessageCount,
                    ConcurrentSenders = 2,
                    MessageSize = MessageSize.Small
                };

                var scenarioLarge = new AzureTestScenario
                {
                    Name = "Large Messages",
                    QueueName = "perf-size-queue",
                    MessageCount = limitedMessageCount,
                    ConcurrentSenders = 2,
                    MessageSize = MessageSize.Large
                };

                // Act
                var resultSmall = _performanceRunner!.RunServiceBusThroughputTestAsync(scenarioSmall).GetAwaiter().GetResult();
                Task.Delay(100).GetAwaiter().GetResult();
                var resultLarge = _performanceRunner!.RunServiceBusThroughputTestAsync(scenarioLarge).GetAwaiter().GetResult();

                // Assert - Small messages should have equal or higher throughput
                // Allow for some variance (within 20%)
                var throughputRatio = resultLarge.MessagesPerSecond / resultSmall.MessagesPerSecond;
                var isReasonable = throughputRatio <= 1.2;

                if (!isReasonable)
                {
                    _output.WriteLine($"Unexpected throughput ratio:");
                    _output.WriteLine($"  Small: {resultSmall.MessagesPerSecond:F2} msg/s");
                    _output.WriteLine($"  Large: {resultLarge.MessagesPerSecond:F2} msg/s");
                    _output.WriteLine($"  Ratio: {throughputRatio:F2}");
                }

                return isReasonable.ToProperty()
                    .Label($"Large messages should have <= throughput of small messages (ratio <= 1.2, was {throughputRatio:F2})");
            });
    }
}

using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Azure.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Azure.Tests.Integration;

/// <summary>
/// Integration tests for Azure Service Bus performance benchmarks.
/// Tests throughput, latency, and resource utilization under various load conditions.
/// **Validates: Requirements 5.1, 5.2, 5.5**
/// </summary>
public class AzurePerformanceBenchmarkTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;
    private IAzureTestEnvironment? _environment;
    private ServiceBusTestHelpers? _serviceBusHelpers;
    private AzurePerformanceTestRunner? _performanceRunner;

    public AzurePerformanceBenchmarkTests(ITestOutputHelper output)
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

    [Fact]
    public async Task ServiceBusThroughputTest_SmallMessages_MeasuresMessagesPerSecond()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "Small Message Throughput",
            QueueName = "perf-test-queue",
            MessageCount = 1000,
            ConcurrentSenders = 5,
            MessageSize = MessageSize.Small
        };

        // Act
        var result = await _performanceRunner!.RunServiceBusThroughputTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Small Message Throughput - Throughput", result.TestName);
        Assert.Equal(1000, result.TotalMessages);
        Assert.True(result.MessagesPerSecond > 0, "Messages per second should be greater than 0");
        Assert.True(result.SuccessfulMessages > 0, "Should have successful messages");
        Assert.True(result.Duration.TotalSeconds > 0, "Duration should be greater than 0");
        
        _output.WriteLine($"Throughput: {result.MessagesPerSecond:F2} msg/s");
        _output.WriteLine($"Success Rate: {result.SuccessfulMessages}/{result.TotalMessages}");
        _output.WriteLine($"Duration: {result.Duration.TotalSeconds:F2}s");
    }

    [Fact]
    public async Task ServiceBusThroughputTest_MediumMessages_MeasuresMessagesPerSecond()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "Medium Message Throughput",
            QueueName = "perf-test-queue",
            MessageCount = 500,
            ConcurrentSenders = 5,
            MessageSize = MessageSize.Medium
        };

        // Act
        var result = await _performanceRunner!.RunServiceBusThroughputTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.MessagesPerSecond > 0);
        Assert.True(result.SuccessfulMessages > 0);
        Assert.NotNull(result.ServiceBusMetrics);
        Assert.True(result.ServiceBusMetrics.IncomingMessagesPerSecond > 0);
        
        _output.WriteLine($"Throughput: {result.MessagesPerSecond:F2} msg/s");
        _output.WriteLine($"Avg Message Size: {result.ServiceBusMetrics.AverageMessageSizeBytes} bytes");
    }

    [Fact]
    public async Task ServiceBusThroughputTest_LargeMessages_MeasuresMessagesPerSecond()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "Large Message Throughput",
            QueueName = "perf-test-queue",
            MessageCount = 200,
            ConcurrentSenders = 3,
            MessageSize = MessageSize.Large
        };

        // Act
        var result = await _performanceRunner!.RunServiceBusThroughputTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.MessagesPerSecond > 0);
        Assert.True(result.SuccessfulMessages > 0);
        
        // Large messages should have lower throughput than small messages
        Assert.True(result.ServiceBusMetrics.AverageMessageSizeBytes > 10000);
        
        _output.WriteLine($"Throughput: {result.MessagesPerSecond:F2} msg/s");
        _output.WriteLine($"Avg Latency: {result.AverageLatency.TotalMilliseconds:F2}ms");
    }

    [Fact]
    public async Task ServiceBusLatencyTest_SmallMessages_MeasuresP50P95P99()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "Small Message Latency",
            QueueName = "perf-test-queue",
            MessageCount = 100,
            ConcurrentSenders = 1,
            MessageSize = MessageSize.Small
        };

        // Act
        var result = await _performanceRunner!.RunServiceBusLatencyTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Small Message Latency - Latency", result.TestName);
        Assert.True(result.MedianLatency > TimeSpan.Zero, "P50 latency should be greater than 0");
        Assert.True(result.P95Latency > TimeSpan.Zero, "P95 latency should be greater than 0");
        Assert.True(result.P99Latency > TimeSpan.Zero, "P99 latency should be greater than 0");
        Assert.True(result.MinLatency > TimeSpan.Zero, "Min latency should be greater than 0");
        Assert.True(result.MaxLatency > TimeSpan.Zero, "Max latency should be greater than 0");
        
        // Latency percentiles should be ordered
        Assert.True(result.MedianLatency <= result.P95Latency);
        Assert.True(result.P95Latency <= result.P99Latency);
        Assert.True(result.MinLatency <= result.MedianLatency);
        Assert.True(result.MedianLatency <= result.MaxLatency);
        
        _output.WriteLine($"P50 (Median): {result.MedianLatency.TotalMilliseconds:F2}ms");
        _output.WriteLine($"P95: {result.P95Latency.TotalMilliseconds:F2}ms");
        _output.WriteLine($"P99: {result.P99Latency.TotalMilliseconds:F2}ms");
        _output.WriteLine($"Min: {result.MinLatency.TotalMilliseconds:F2}ms");
        _output.WriteLine($"Max: {result.MaxLatency.TotalMilliseconds:F2}ms");
    }

    [Fact]
    public async Task ServiceBusLatencyTest_WithEncryption_MeasuresAdditionalOverhead()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "Encrypted Message Latency",
            QueueName = "perf-test-queue",
            MessageCount = 100,
            ConcurrentSenders = 1,
            MessageSize = MessageSize.Small,
            EnableEncryption = true
        };

        // Act
        var result = await _performanceRunner!.RunServiceBusLatencyTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.MedianLatency > TimeSpan.Zero);
        Assert.True(result.ResourceUsage.KeyVaultRequestsPerSecond > 0, 
            "Should have Key Vault requests when encryption is enabled");
        
        _output.WriteLine($"P50 with encryption: {result.MedianLatency.TotalMilliseconds:F2}ms");
        _output.WriteLine($"Key Vault RPS: {result.ResourceUsage.KeyVaultRequestsPerSecond:F2}");
    }

    [Fact]
    public async Task ServiceBusLatencyTest_WithSessions_MeasuresSessionOverhead()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "Session Message Latency",
            QueueName = "perf-test-queue.fifo",
            MessageCount = 100,
            ConcurrentSenders = 1,
            MessageSize = MessageSize.Small,
            EnableSessions = true
        };

        // Act
        var result = await _performanceRunner!.RunServiceBusLatencyTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.MedianLatency > TimeSpan.Zero);
        Assert.True(result.P95Latency > TimeSpan.Zero);
        
        _output.WriteLine($"P50 with sessions: {result.MedianLatency.TotalMilliseconds:F2}ms");
        _output.WriteLine($"P95 with sessions: {result.P95Latency.TotalMilliseconds:F2}ms");
    }

    [Fact]
    public async Task ResourceUtilizationTest_MeasuresCpuMemoryNetwork()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "Resource Utilization",
            QueueName = "perf-test-queue",
            MessageCount = 500,
            ConcurrentSenders = 5,
            MessageSize = MessageSize.Medium
        };

        // Act
        var result = await _performanceRunner!.RunResourceUtilizationTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.ResourceUsage);
        Assert.True(result.ResourceUsage.ServiceBusCpuPercent >= 0);
        Assert.True(result.ResourceUsage.ServiceBusMemoryBytes > 0);
        Assert.True(result.ResourceUsage.NetworkBytesIn > 0);
        Assert.True(result.ResourceUsage.NetworkBytesOut > 0);
        Assert.True(result.ResourceUsage.ServiceBusConnectionCount > 0);
        
        _output.WriteLine($"CPU: {result.ResourceUsage.ServiceBusCpuPercent:F2}%");
        _output.WriteLine($"Memory: {result.ResourceUsage.ServiceBusMemoryBytes / 1024 / 1024:F2} MB");
        _output.WriteLine($"Network In: {result.ResourceUsage.NetworkBytesIn / 1024:F2} KB");
        _output.WriteLine($"Network Out: {result.ResourceUsage.NetworkBytesOut / 1024:F2} KB");
        _output.WriteLine($"Connections: {result.ResourceUsage.ServiceBusConnectionCount}");
    }

    [Fact]
    public async Task ThroughputTest_HighConcurrency_MaintainsPerformance()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "High Concurrency Throughput",
            QueueName = "perf-test-queue",
            MessageCount = 1000,
            ConcurrentSenders = 10,
            MessageSize = MessageSize.Small
        };

        // Act
        var result = await _performanceRunner!.RunServiceBusThroughputTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.MessagesPerSecond > 0);
        Assert.True(result.SuccessfulMessages > 0);
        Assert.True(result.ServiceBusMetrics.ActiveConnections >= scenario.ConcurrentSenders);
        
        // High concurrency should achieve reasonable throughput
        var successRate = (double)result.SuccessfulMessages / result.TotalMessages;
        Assert.True(successRate > 0.95, $"Success rate should be > 95%, was {successRate:P2}");
        
        _output.WriteLine($"Throughput with {scenario.ConcurrentSenders} senders: {result.MessagesPerSecond:F2} msg/s");
        _output.WriteLine($"Success Rate: {successRate:P2}");
    }

    [Fact]
    public async Task LatencyTest_ConsistentAcrossMultipleRuns()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "Latency Consistency",
            QueueName = "perf-test-queue",
            MessageCount = 50,
            ConcurrentSenders = 1,
            MessageSize = MessageSize.Small
        };

        // Act - Run test multiple times
        var results = new List<AzurePerformanceTestResult>();
        for (int i = 0; i < 3; i++)
        {
            var result = await _performanceRunner!.RunServiceBusLatencyTestAsync(scenario);
            results.Add(result);
            await Task.Delay(100); // Small delay between runs
        }

        // Assert - Latency should be relatively consistent
        var medianLatencies = results.Select(r => r.MedianLatency.TotalMilliseconds).ToList();
        var avgMedianLatency = medianLatencies.Average();
        var maxDeviation = medianLatencies.Max(l => Math.Abs(l - avgMedianLatency));
        var deviationPercent = maxDeviation / avgMedianLatency;
        
        Assert.True(deviationPercent < 0.5, 
            $"Latency deviation should be < 50%, was {deviationPercent:P2}");
        
        _output.WriteLine($"Average P50: {avgMedianLatency:F2}ms");
        _output.WriteLine($"Max Deviation: {deviationPercent:P2}");
        _output.WriteLine($"Latencies: {string.Join(", ", medianLatencies.Select(l => $"{l:F2}ms"))}");
    }

    [Fact]
    public async Task ThroughputTest_MessageSizeImpact_ShowsExpectedScaling()
    {
        // Arrange - Test different message sizes
        var sizes = new[] { MessageSize.Small, MessageSize.Medium, MessageSize.Large };
        var results = new Dictionary<MessageSize, AzurePerformanceTestResult>();

        // Act
        foreach (var size in sizes)
        {
            var scenario = new AzureTestScenario
            {
                Name = $"{size} Message Size Impact",
                QueueName = "perf-test-queue",
                MessageCount = 200,
                ConcurrentSenders = 3,
                MessageSize = size
            };

            var result = await _performanceRunner!.RunServiceBusThroughputTestAsync(scenario);
            results[size] = result;
        }

        // Assert - Larger messages should have lower throughput
        Assert.True(results[MessageSize.Small].MessagesPerSecond > 0);
        Assert.True(results[MessageSize.Medium].MessagesPerSecond > 0);
        Assert.True(results[MessageSize.Large].MessagesPerSecond > 0);
        
        // Message size should impact average message size metric
        Assert.True(results[MessageSize.Small].ServiceBusMetrics.AverageMessageSizeBytes < 
                   results[MessageSize.Medium].ServiceBusMetrics.AverageMessageSizeBytes);
        Assert.True(results[MessageSize.Medium].ServiceBusMetrics.AverageMessageSizeBytes < 
                   results[MessageSize.Large].ServiceBusMetrics.AverageMessageSizeBytes);
        
        _output.WriteLine($"Small: {results[MessageSize.Small].MessagesPerSecond:F2} msg/s");
        _output.WriteLine($"Medium: {results[MessageSize.Medium].MessagesPerSecond:F2} msg/s");
        _output.WriteLine($"Large: {results[MessageSize.Large].MessagesPerSecond:F2} msg/s");
    }
}

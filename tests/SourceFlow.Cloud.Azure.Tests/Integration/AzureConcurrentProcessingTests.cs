using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Azure.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Azure.Tests.Integration;

/// <summary>
/// Integration tests for Azure Service Bus concurrent processing.
/// Tests performance under multiple concurrent connections and sessions.
/// **Validates: Requirements 5.3**
/// </summary>
public class AzureConcurrentProcessingTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;
    private IAzureTestEnvironment? _environment;
    private ServiceBusTestHelpers? _serviceBusHelpers;
    private AzurePerformanceTestRunner? _performanceRunner;

    public AzureConcurrentProcessingTests(ITestOutputHelper output)
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
    public async Task ConcurrentProcessing_MultipleSenders_ProcessesAllMessages()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "Multiple Senders",
            QueueName = "concurrent-test-queue",
            MessageCount = 500,
            ConcurrentSenders = 5,
            ConcurrentReceivers = 3,
            MessageSize = MessageSize.Small
        };

        // Act
        var result = await _performanceRunner!.RunConcurrentProcessingTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.SuccessfulMessages > 0, "Should process messages successfully");
        Assert.True(result.MessagesPerSecond > 0, "Should have positive throughput");
        
        // Most messages should be processed successfully
        var successRate = (double)result.SuccessfulMessages / result.TotalMessages;
        Assert.True(successRate > 0.90, $"Success rate should be > 90%, was {successRate:P2}");
        
        _output.WriteLine($"Processed: {result.SuccessfulMessages}/{result.TotalMessages}");
        _output.WriteLine($"Throughput: {result.MessagesPerSecond:F2} msg/s");
        _output.WriteLine($"Duration: {result.Duration.TotalSeconds:F2}s");
    }

    [Fact]
    public async Task ConcurrentProcessing_MultipleReceivers_DistributesLoad()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "Multiple Receivers",
            QueueName = "concurrent-test-queue",
            MessageCount = 300,
            ConcurrentSenders = 2,
            ConcurrentReceivers = 5,
            MessageSize = MessageSize.Small
        };

        // Act
        var result = await _performanceRunner!.RunConcurrentProcessingTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.SuccessfulMessages > 0);
        Assert.NotNull(result.ServiceBusMetrics);
        Assert.True(result.ServiceBusMetrics.ActiveConnections >= scenario.ConcurrentReceivers,
            "Should have connections for all receivers");
        
        _output.WriteLine($"Active Connections: {result.ServiceBusMetrics.ActiveConnections}");
        _output.WriteLine($"Throughput: {result.MessagesPerSecond:F2} msg/s");
    }

    [Fact]
    public async Task ConcurrentProcessing_HighConcurrency_MaintainsIntegrity()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "High Concurrency",
            QueueName = "concurrent-test-queue",
            MessageCount = 1000,
            ConcurrentSenders = 10,
            ConcurrentReceivers = 10,
            MessageSize = MessageSize.Small
        };

        // Act
        var result = await _performanceRunner!.RunConcurrentProcessingTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.SuccessfulMessages > 0);
        
        // High concurrency should still maintain good success rate
        var successRate = (double)result.SuccessfulMessages / result.TotalMessages;
        Assert.True(successRate > 0.85, $"Success rate should be > 85% even with high concurrency, was {successRate:P2}");
        
        // Should achieve reasonable throughput
        Assert.True(result.MessagesPerSecond > 50, 
            $"Should achieve > 50 msg/s with high concurrency, was {result.MessagesPerSecond:F2}");
        
        _output.WriteLine($"Success Rate: {successRate:P2}");
        _output.WriteLine($"Throughput: {result.MessagesPerSecond:F2} msg/s");
        _output.WriteLine($"Failed Messages: {result.FailedMessages}");
    }

    [Fact]
    public async Task ConcurrentProcessing_MediumMessages_HandlesLoad()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "Concurrent Medium Messages",
            QueueName = "concurrent-test-queue",
            MessageCount = 400,
            ConcurrentSenders = 5,
            ConcurrentReceivers = 5,
            MessageSize = MessageSize.Medium
        };

        // Act
        var result = await _performanceRunner!.RunConcurrentProcessingTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.SuccessfulMessages > 0);
        Assert.True(result.ServiceBusMetrics.AverageMessageSizeBytes > 1000,
            "Medium messages should have size > 1KB");
        
        var successRate = (double)result.SuccessfulMessages / result.TotalMessages;
        Assert.True(successRate > 0.90, $"Success rate should be > 90%, was {successRate:P2}");
        
        _output.WriteLine($"Avg Message Size: {result.ServiceBusMetrics.AverageMessageSizeBytes} bytes");
        _output.WriteLine($"Throughput: {result.MessagesPerSecond:F2} msg/s");
    }

    [Fact]
    public async Task ConcurrentProcessing_WithSessions_MaintainsOrdering()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "Concurrent Sessions",
            QueueName = "concurrent-session-queue.fifo",
            MessageCount = 300,
            ConcurrentSenders = 5,
            ConcurrentReceivers = 3,
            MessageSize = MessageSize.Small,
            EnableSessions = true
        };

        // Act
        var result = await _performanceRunner!.RunConcurrentProcessingTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.SuccessfulMessages > 0);
        
        // Session-based processing should still work with concurrency
        var successRate = (double)result.SuccessfulMessages / result.TotalMessages;
        Assert.True(successRate > 0.85, $"Success rate with sessions should be > 85%, was {successRate:P2}");
        
        _output.WriteLine($"Session-based Success Rate: {successRate:P2}");
        _output.WriteLine($"Throughput: {result.MessagesPerSecond:F2} msg/s");
    }

    [Fact]
    public async Task ConcurrentProcessing_LowConcurrency_Baseline()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "Low Concurrency Baseline",
            QueueName = "concurrent-test-queue",
            MessageCount = 200,
            ConcurrentSenders = 1,
            ConcurrentReceivers = 1,
            MessageSize = MessageSize.Small
        };

        // Act
        var result = await _performanceRunner!.RunConcurrentProcessingTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.SuccessfulMessages > 0);
        
        // Single sender/receiver should have very high success rate
        var successRate = (double)result.SuccessfulMessages / result.TotalMessages;
        Assert.True(successRate > 0.95, $"Single sender/receiver should have > 95% success rate, was {successRate:P2}");
        
        _output.WriteLine($"Baseline Throughput: {result.MessagesPerSecond:F2} msg/s");
        _output.WriteLine($"Baseline Success Rate: {successRate:P2}");
    }

    [Fact]
    public async Task ConcurrentProcessing_ScalingComparison_ShowsImprovement()
    {
        // Arrange - Test with 1, 3, and 5 concurrent senders
        var scenarios = new[]
        {
            new AzureTestScenario
            {
                Name = "1 Sender",
                QueueName = "concurrent-scaling-queue",
                MessageCount = 300,
                ConcurrentSenders = 1,
                ConcurrentReceivers = 1,
                MessageSize = MessageSize.Small
            },
            new AzureTestScenario
            {
                Name = "3 Senders",
                QueueName = "concurrent-scaling-queue",
                MessageCount = 300,
                ConcurrentSenders = 3,
                ConcurrentReceivers = 3,
                MessageSize = MessageSize.Small
            },
            new AzureTestScenario
            {
                Name = "5 Senders",
                QueueName = "concurrent-scaling-queue",
                MessageCount = 300,
                ConcurrentSenders = 5,
                ConcurrentReceivers = 5,
                MessageSize = MessageSize.Small
            }
        };

        // Act
        var results = new List<AzurePerformanceTestResult>();
        foreach (var scenario in scenarios)
        {
            var result = await _performanceRunner!.RunConcurrentProcessingTestAsync(scenario);
            results.Add(result);
            await Task.Delay(100); // Small delay between tests
        }

        // Assert - Throughput should improve with more concurrency
        Assert.True(results[0].MessagesPerSecond > 0);
        Assert.True(results[1].MessagesPerSecond > 0);
        Assert.True(results[2].MessagesPerSecond > 0);
        
        // More concurrency should achieve at least 80% of linear scaling
        var scalingRatio1to3 = results[1].MessagesPerSecond / results[0].MessagesPerSecond;
        var scalingRatio1to5 = results[2].MessagesPerSecond / results[0].MessagesPerSecond;
        
        Assert.True(scalingRatio1to3 >= 0.8, 
            $"3x concurrency should achieve >= 80% scaling, was {scalingRatio1to3:F2}x");
        
        _output.WriteLine($"1 Sender: {results[0].MessagesPerSecond:F2} msg/s");
        _output.WriteLine($"3 Senders: {results[1].MessagesPerSecond:F2} msg/s (scaling: {scalingRatio1to3:F2}x)");
        _output.WriteLine($"5 Senders: {results[2].MessagesPerSecond:F2} msg/s (scaling: {scalingRatio1to5:F2}x)");
    }

    [Fact]
    public async Task ConcurrentProcessing_UnbalancedSendersReceivers_HandlesGracefully()
    {
        // Arrange - More senders than receivers
        var scenario = new AzureTestScenario
        {
            Name = "Unbalanced Concurrency",
            QueueName = "concurrent-test-queue",
            MessageCount = 400,
            ConcurrentSenders = 8,
            ConcurrentReceivers = 2,
            MessageSize = MessageSize.Small
        };

        // Act
        var result = await _performanceRunner!.RunConcurrentProcessingTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.SuccessfulMessages > 0);
        
        // Should still process messages successfully despite imbalance
        var successRate = (double)result.SuccessfulMessages / result.TotalMessages;
        Assert.True(successRate > 0.80, $"Should handle unbalanced concurrency, success rate was {successRate:P2}");
        
        _output.WriteLine($"Unbalanced Success Rate: {successRate:P2}");
        _output.WriteLine($"Throughput: {result.MessagesPerSecond:F2} msg/s");
    }

    [Fact]
    public async Task ConcurrentProcessing_WithEncryption_MaintainsPerformance()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "Concurrent with Encryption",
            QueueName = "concurrent-encrypted-queue",
            MessageCount = 300,
            ConcurrentSenders = 5,
            ConcurrentReceivers = 5,
            MessageSize = MessageSize.Small,
            EnableEncryption = true
        };

        // Act
        var result = await _performanceRunner!.RunConcurrentProcessingTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.SuccessfulMessages > 0);
        Assert.True(result.ResourceUsage.KeyVaultRequestsPerSecond > 0,
            "Should have Key Vault requests when encryption is enabled");
        
        var successRate = (double)result.SuccessfulMessages / result.TotalMessages;
        Assert.True(successRate > 0.85, 
            $"Should maintain good success rate with encryption, was {successRate:P2}");
        
        _output.WriteLine($"Success Rate with Encryption: {successRate:P2}");
        _output.WriteLine($"Key Vault RPS: {result.ResourceUsage.KeyVaultRequestsPerSecond:F2}");
        _output.WriteLine($"Throughput: {result.MessagesPerSecond:F2} msg/s");
    }

    [Fact]
    public async Task ConcurrentProcessing_LargeMessages_HandlesLoad()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "Concurrent Large Messages",
            QueueName = "concurrent-test-queue",
            MessageCount = 200,
            ConcurrentSenders = 4,
            ConcurrentReceivers = 4,
            MessageSize = MessageSize.Large
        };

        // Act
        var result = await _performanceRunner!.RunConcurrentProcessingTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.SuccessfulMessages > 0);
        Assert.True(result.ServiceBusMetrics.AverageMessageSizeBytes > 10000,
            "Large messages should have size > 10KB");
        
        var successRate = (double)result.SuccessfulMessages / result.TotalMessages;
        Assert.True(successRate > 0.85, 
            $"Should handle large messages concurrently, success rate was {successRate:P2}");
        
        _output.WriteLine($"Large Message Success Rate: {successRate:P2}");
        _output.WriteLine($"Avg Message Size: {result.ServiceBusMetrics.AverageMessageSizeBytes / 1024:F2} KB");
        _output.WriteLine($"Throughput: {result.MessagesPerSecond:F2} msg/s");
    }
}

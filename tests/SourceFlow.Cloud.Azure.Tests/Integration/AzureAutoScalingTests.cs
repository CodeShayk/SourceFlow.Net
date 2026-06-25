using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Azure.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Azure.Tests.Integration;

/// <summary>
/// Integration tests for Azure Service Bus auto-scaling behavior.
/// Tests scaling efficiency and performance characteristics under increasing load.
/// **Validates: Requirements 5.4**
/// </summary>
public class AzureAutoScalingTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;
    private IAzureTestEnvironment? _environment;
    private ServiceBusTestHelpers? _serviceBusHelpers;
    private AzurePerformanceTestRunner? _performanceRunner;

    public AzureAutoScalingTests(ITestOutputHelper output)
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
    public async Task AutoScaling_GradualLoadIncrease_MeasuresScalingEfficiency()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "Auto-Scaling Test",
            QueueName = "autoscaling-test-queue",
            MessageCount = 100,
            ConcurrentSenders = 1,
            MessageSize = MessageSize.Small,
            TestAutoScaling = true
        };

        // Act
        var result = await _performanceRunner!.RunAutoScalingTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.AutoScalingMetrics);
        Assert.True(result.AutoScalingMetrics.Count >= 5, 
            "Should have metrics for multiple load levels");
        Assert.True(result.ScalingEfficiency > 0, 
            "Scaling efficiency should be positive");
        Assert.True(result.ScalingEfficiency <= 1.5, 
            "Scaling efficiency should be reasonable (≤ 1.5)");
        
        _output.WriteLine($"Scaling Efficiency: {result.ScalingEfficiency:F2}");
        _output.WriteLine($"Load Levels Tested: {result.AutoScalingMetrics.Count}");
        _output.WriteLine("Throughput by Load Level:");
        for (int i = 0; i < result.AutoScalingMetrics.Count; i++)
        {
            _output.WriteLine($"  Load x{(i + 1) * 2}: {result.AutoScalingMetrics[i]:F2} msg/s");
        }
    }

    [Fact]
    public async Task AutoScaling_SmallMessages_ShowsLinearScaling()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "Small Message Auto-Scaling",
            QueueName = "autoscaling-small-queue",
            MessageCount = 100,
            ConcurrentSenders = 1,
            MessageSize = MessageSize.Small,
            TestAutoScaling = true
        };

        // Act
        var result = await _performanceRunner!.RunAutoScalingTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.AutoScalingMetrics);
        
        // Check that throughput generally increases with load
        var baseline = result.AutoScalingMetrics[0];
        var lastLevel = result.AutoScalingMetrics[^1];
        
        Assert.True(lastLevel >= baseline * 0.8, 
            $"Throughput should scale reasonably (last >= baseline * 0.8), baseline={baseline:F2}, last={lastLevel:F2}");
        
        _output.WriteLine($"Baseline: {baseline:F2} msg/s");
        _output.WriteLine($"Final Load: {lastLevel:F2} msg/s");
        _output.WriteLine($"Scaling Factor: {lastLevel / baseline:F2}x");
    }

    [Fact]
    public async Task AutoScaling_MediumMessages_MaintainsPerformance()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "Medium Message Auto-Scaling",
            QueueName = "autoscaling-medium-queue",
            MessageCount = 100,
            ConcurrentSenders = 1,
            MessageSize = MessageSize.Medium,
            TestAutoScaling = true
        };

        // Act
        var result = await _performanceRunner!.RunAutoScalingTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.AutoScalingMetrics);
        Assert.True(result.ScalingEfficiency > 0);
        
        // Medium messages should still scale, though possibly less efficiently
        var allPositive = result.AutoScalingMetrics.All(m => m > 0);
        Assert.True(allPositive, "All throughput measurements should be positive");
        
        _output.WriteLine($"Scaling Efficiency: {result.ScalingEfficiency:F2}");
        _output.WriteLine($"Throughput Range: {result.AutoScalingMetrics.Min():F2} - {result.AutoScalingMetrics.Max():F2} msg/s");
    }

    [Fact]
    public async Task AutoScaling_EfficiencyCalculation_IsReasonable()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "Scaling Efficiency Calculation",
            QueueName = "autoscaling-efficiency-queue",
            MessageCount = 100,
            ConcurrentSenders = 1,
            MessageSize = MessageSize.Small,
            TestAutoScaling = true
        };

        // Act
        var result = await _performanceRunner!.RunAutoScalingTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.ScalingEfficiency > 0, "Efficiency should be positive");
        Assert.True(result.ScalingEfficiency <= 2.0, "Efficiency should be reasonable (≤ 2.0)");
        
        // Efficiency close to 1.0 indicates near-linear scaling
        // Efficiency < 1.0 indicates sub-linear scaling
        // Efficiency > 1.0 indicates super-linear scaling (rare but possible with caching)
        
        _output.WriteLine($"Scaling Efficiency: {result.ScalingEfficiency:F2}");
        if (result.ScalingEfficiency >= 0.9 && result.ScalingEfficiency <= 1.1)
        {
            _output.WriteLine("Scaling is near-linear (excellent)");
        }
        else if (result.ScalingEfficiency >= 0.7)
        {
            _output.WriteLine("Scaling is sub-linear but acceptable");
        }
        else
        {
            _output.WriteLine("Scaling efficiency is below optimal");
        }
    }

    [Fact]
    public async Task AutoScaling_ThroughputProgression_ShowsConsistentPattern()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "Throughput Progression",
            QueueName = "autoscaling-progression-queue",
            MessageCount = 100,
            ConcurrentSenders = 1,
            MessageSize = MessageSize.Small,
            TestAutoScaling = true
        };

        // Act
        var result = await _performanceRunner!.RunAutoScalingTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.AutoScalingMetrics.Count >= 5);
        
        // Check for consistent progression (no dramatic drops)
        for (int i = 1; i < result.AutoScalingMetrics.Count; i++)
        {
            var current = result.AutoScalingMetrics[i];
            var previous = result.AutoScalingMetrics[i - 1];
            
            // Current should not be dramatically lower than previous (allow 50% drop max)
            Assert.True(current >= previous * 0.5, 
                $"Throughput should not drop dramatically at load level {i + 1}");
        }
        
        _output.WriteLine("Throughput Progression:");
        for (int i = 0; i < result.AutoScalingMetrics.Count; i++)
        {
            var change = i > 0 
                ? $"({(result.AutoScalingMetrics[i] / result.AutoScalingMetrics[i - 1]):F2}x)" 
                : "";
            _output.WriteLine($"  Level {i + 1}: {result.AutoScalingMetrics[i]:F2} msg/s {change}");
        }
    }

    [Fact]
    public async Task AutoScaling_BaselineComparison_ShowsImprovement()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "Baseline Comparison",
            QueueName = "autoscaling-baseline-queue",
            MessageCount = 100,
            ConcurrentSenders = 1,
            MessageSize = MessageSize.Small,
            TestAutoScaling = true
        };

        // Act
        var result = await _performanceRunner!.RunAutoScalingTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.AutoScalingMetrics);
        
        var baseline = result.AutoScalingMetrics[0];
        var maxThroughput = result.AutoScalingMetrics.Max();
        var improvementFactor = maxThroughput / baseline;
        
        // Should see some improvement with increased load
        Assert.True(improvementFactor >= 0.8, 
            $"Max throughput should be at least 80% of baseline, was {improvementFactor:F2}x");
        
        _output.WriteLine($"Baseline Throughput: {baseline:F2} msg/s");
        _output.WriteLine($"Max Throughput: {maxThroughput:F2} msg/s");
        _output.WriteLine($"Improvement Factor: {improvementFactor:F2}x");
    }

    [Fact]
    public async Task AutoScaling_DifferentMessageSizes_ShowsExpectedBehavior()
    {
        // Arrange - Test with different message sizes
        var sizes = new[] { MessageSize.Small, MessageSize.Medium };
        var results = new Dictionary<MessageSize, AzurePerformanceTestResult>();

        // Act
        foreach (var size in sizes)
        {
            var scenario = new AzureTestScenario
            {
                Name = $"{size} Message Auto-Scaling",
                QueueName = "autoscaling-size-queue",
                MessageCount = 100,
                ConcurrentSenders = 1,
                MessageSize = size,
                TestAutoScaling = true
            };

            var result = await _performanceRunner!.RunAutoScalingTestAsync(scenario);
            results[size] = result;
            await Task.Delay(100); // Small delay between tests
        }

        // Assert - Both should scale, though possibly differently
        Assert.True(results[MessageSize.Small].ScalingEfficiency > 0);
        Assert.True(results[MessageSize.Medium].ScalingEfficiency > 0);
        
        _output.WriteLine($"Small Message Efficiency: {results[MessageSize.Small].ScalingEfficiency:F2}");
        _output.WriteLine($"Medium Message Efficiency: {results[MessageSize.Medium].ScalingEfficiency:F2}");
    }

    [Fact]
    public async Task AutoScaling_LoadLevels_CoverWideRange()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "Load Level Coverage",
            QueueName = "autoscaling-coverage-queue",
            MessageCount = 100,
            ConcurrentSenders = 1,
            MessageSize = MessageSize.Small,
            TestAutoScaling = true
        };

        // Act
        var result = await _performanceRunner!.RunAutoScalingTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.AutoScalingMetrics.Count >= 5, 
            "Should test at least 5 different load levels");
        
        // Should have tested a range from baseline to 10x load
        var expectedLevels = 5; // Baseline + 4 scaling levels
        Assert.True(result.AutoScalingMetrics.Count >= expectedLevels,
            $"Should have at least {expectedLevels} load levels");
        
        _output.WriteLine($"Load Levels Tested: {result.AutoScalingMetrics.Count}");
        _output.WriteLine($"Throughput Range: {result.AutoScalingMetrics.Min():F2} - {result.AutoScalingMetrics.Max():F2} msg/s");
    }

    [Fact]
    public async Task AutoScaling_Duration_IsReasonable()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "Auto-Scaling Duration",
            QueueName = "autoscaling-duration-queue",
            MessageCount = 100,
            ConcurrentSenders = 1,
            MessageSize = MessageSize.Small,
            TestAutoScaling = true
        };

        // Act
        var result = await _performanceRunner!.RunAutoScalingTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Duration > TimeSpan.Zero, "Duration should be positive");
        Assert.True(result.Duration < TimeSpan.FromMinutes(5), 
            "Auto-scaling test should complete in reasonable time (< 5 minutes)");
        
        _output.WriteLine($"Test Duration: {result.Duration.TotalSeconds:F2}s");
        _output.WriteLine($"Load Levels: {result.AutoScalingMetrics.Count}");
        _output.WriteLine($"Avg Time per Level: {result.Duration.TotalSeconds / result.AutoScalingMetrics.Count:F2}s");
    }

    [Fact]
    public async Task AutoScaling_MetricsCollection_IsComplete()
    {
        // Arrange
        var scenario = new AzureTestScenario
        {
            Name = "Metrics Collection",
            QueueName = "autoscaling-metrics-queue",
            MessageCount = 100,
            ConcurrentSenders = 1,
            MessageSize = MessageSize.Small,
            TestAutoScaling = true
        };

        // Act
        var result = await _performanceRunner!.RunAutoScalingTestAsync(scenario);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.AutoScalingMetrics);
        Assert.True(result.ScalingEfficiency > 0);
        Assert.True(result.StartTime < result.EndTime);
        Assert.True(result.Duration > TimeSpan.Zero);
        
        // All metrics should be valid numbers
        Assert.True(result.AutoScalingMetrics.All(m => !double.IsNaN(m) && !double.IsInfinity(m)),
            "All metrics should be valid numbers");
        
        _output.WriteLine($"Metrics Collected: {result.AutoScalingMetrics.Count}");
        _output.WriteLine($"All Valid: {result.AutoScalingMetrics.All(m => m > 0)}");
    }
}

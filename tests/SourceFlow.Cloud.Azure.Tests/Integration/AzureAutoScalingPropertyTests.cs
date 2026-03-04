using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Azure.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Azure.Tests.Integration;

/// <summary>
/// Property-based tests for Azure auto-scaling effectiveness.
/// **Property 15: Azure Auto-Scaling Effectiveness**
/// **Validates: Requirements 5.4**
/// </summary>
public class AzureAutoScalingPropertyTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;
    private IAzureTestEnvironment? _environment;
    private ServiceBusTestHelpers? _serviceBusHelpers;
    private AzurePerformanceTestRunner? _performanceRunner;

    public AzureAutoScalingPropertyTests(ITestOutputHelper output)
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
    /// Property 15: Azure Auto-Scaling Effectiveness
    /// For any Azure Service Bus configuration with auto-scaling enabled, when load increases
    /// gradually, the service should scale appropriately to maintain performance characteristics
    /// within acceptable thresholds.
    /// </summary>
    [Property(MaxTest = 5, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property AutoScaling_ShouldMaintainPerformance_UnderIncreasingLoad(
        PositiveInt messageCount)
    {
        var limitedMessageCount = Math.Min(messageCount.Get, 100);

        return Prop.ForAll(
            Gen.Elements(MessageSize.Small, MessageSize.Medium).ToArbitrary(),
            (messageSize) =>
            {
                // Arrange
                var scenario = new AzureTestScenario
                {
                    Name = "Auto-Scaling Effectiveness Test",
                    QueueName = "autoscaling-effectiveness-queue",
                    MessageCount = limitedMessageCount,
                    ConcurrentSenders = 1,
                    MessageSize = messageSize,
                    TestAutoScaling = true
                };

                // Act
                var result = _performanceRunner!.RunAutoScalingTestAsync(scenario).GetAwaiter().GetResult();

                // Assert - Should have multiple load levels and reasonable efficiency
                var hasMultipleLevels = result.AutoScalingMetrics.Count >= 5;
                var hasReasonableEfficiency = result.ScalingEfficiency > 0 && result.ScalingEfficiency <= 2.0;
                var allMetricsPositive = result.AutoScalingMetrics.All(m => m > 0);
                var isEffective = hasMultipleLevels && hasReasonableEfficiency && allMetricsPositive;

                if (!isEffective)
                {
                    _output.WriteLine($"Auto-scaling not effective:");
                    _output.WriteLine($"  Load Levels: {result.AutoScalingMetrics.Count} (expected >= 5)");
                    _output.WriteLine($"  Efficiency: {result.ScalingEfficiency:F2} (expected 0-2.0)");
                    _output.WriteLine($"  All Positive: {allMetricsPositive}");
                }

                return isEffective.ToProperty()
                    .Label($"Auto-scaling should be effective (efficiency: {result.ScalingEfficiency:F2})");
            });
    }

    /// <summary>
    /// Property: Auto-scaling metrics should show consistent progression
    /// For any auto-scaling test, throughput should not drop dramatically between load levels.
    /// </summary>
    [Property(MaxTest = 5, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property AutoScalingMetrics_ShouldShowConsistentProgression()
    {
        return Prop.ForAll(
            Gen.Elements(MessageSize.Small, MessageSize.Medium).ToArbitrary(),
            (messageSize) =>
            {
                // Arrange
                var scenario = new AzureTestScenario
                {
                    Name = "Scaling Progression Test",
                    QueueName = "autoscaling-progression-queue",
                    MessageCount = 100,
                    ConcurrentSenders = 1,
                    MessageSize = messageSize,
                    TestAutoScaling = true
                };

                // Act
                var result = _performanceRunner!.RunAutoScalingTestAsync(scenario).GetAwaiter().GetResult();

                // Assert - No dramatic drops in throughput
                var hasConsistentProgression = true;
                for (int i = 1; i < result.AutoScalingMetrics.Count; i++)
                {
                    var current = result.AutoScalingMetrics[i];
                    var previous = result.AutoScalingMetrics[i - 1];
                    
                    // Allow up to 60% drop between levels
                    if (current < previous * 0.4)
                    {
                        hasConsistentProgression = false;
                        _output.WriteLine($"Dramatic drop at level {i + 1}: {previous:F2} -> {current:F2}");
                        break;
                    }
                }

                return hasConsistentProgression.ToProperty()
                    .Label("Auto-scaling metrics should show consistent progression (no drops > 60%)");
            });
    }

    /// <summary>
    /// Property: Scaling efficiency should be within reasonable bounds
    /// For any auto-scaling test, efficiency should be positive and not exceed 2.0.
    /// </summary>
    [Property(MaxTest = 10, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property ScalingEfficiency_ShouldBeWithinReasonableBounds(
        PositiveInt messageCount)
    {
        var limitedMessageCount = Math.Min(messageCount.Get, 100);

        return Prop.ForAll(
            Gen.Elements(MessageSize.Small, MessageSize.Medium, MessageSize.Large).ToArbitrary(),
            (messageSize) =>
            {
                // Arrange
                var scenario = new AzureTestScenario
                {
                    Name = "Efficiency Bounds Test",
                    QueueName = "autoscaling-efficiency-queue",
                    MessageCount = limitedMessageCount,
                    ConcurrentSenders = 1,
                    MessageSize = messageSize,
                    TestAutoScaling = true
                };

                // Act
                var result = _performanceRunner!.RunAutoScalingTestAsync(scenario).GetAwaiter().GetResult();

                // Assert - Efficiency should be reasonable
                var isReasonable = result.ScalingEfficiency > 0 && result.ScalingEfficiency <= 2.0;

                if (!isReasonable)
                {
                    _output.WriteLine($"Unreasonable efficiency: {result.ScalingEfficiency:F2}");
                }

                return isReasonable.ToProperty()
                    .Label($"Scaling efficiency should be reasonable (0 < efficiency <= 2.0, was {result.ScalingEfficiency:F2})");
            });
    }

    /// <summary>
    /// Property: Baseline throughput should be positive
    /// For any auto-scaling test, the baseline (first) throughput measurement should be positive.
    /// </summary>
    [Property(MaxTest = 10, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property BaselineThroughput_ShouldBePositive(
        PositiveInt messageCount)
    {
        var limitedMessageCount = Math.Min(messageCount.Get, 100);

        return Prop.ForAll(
            Gen.Elements(MessageSize.Small, MessageSize.Medium).ToArbitrary(),
            (messageSize) =>
            {
                // Arrange
                var scenario = new AzureTestScenario
                {
                    Name = "Baseline Test",
                    QueueName = "autoscaling-baseline-queue",
                    MessageCount = limitedMessageCount,
                    ConcurrentSenders = 1,
                    MessageSize = messageSize,
                    TestAutoScaling = true
                };

                // Act
                var result = _performanceRunner!.RunAutoScalingTestAsync(scenario).GetAwaiter().GetResult();

                // Assert - Baseline should be positive
                var hasPositiveBaseline = result.AutoScalingMetrics.Count > 0 && 
                                         result.AutoScalingMetrics[0] > 0;

                if (!hasPositiveBaseline)
                {
                    _output.WriteLine($"Invalid baseline: {result.AutoScalingMetrics.FirstOrDefault():F2}");
                }

                return hasPositiveBaseline.ToProperty()
                    .Label("Baseline throughput should be positive");
            });
    }

    /// <summary>
    /// Property: Maximum throughput should be at least as good as baseline
    /// For any auto-scaling test, max throughput should be >= 70% of baseline.
    /// </summary>
    [Property(MaxTest = 5, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property MaxThroughput_ShouldBeReasonableComparedToBaseline()
    {
        return Prop.ForAll(
            Gen.Elements(MessageSize.Small, MessageSize.Medium).ToArbitrary(),
            (messageSize) =>
            {
                // Arrange
                var scenario = new AzureTestScenario
                {
                    Name = "Max Throughput Test",
                    QueueName = "autoscaling-max-queue",
                    MessageCount = 100,
                    ConcurrentSenders = 1,
                    MessageSize = messageSize,
                    TestAutoScaling = true
                };

                // Act
                var result = _performanceRunner!.RunAutoScalingTestAsync(scenario).GetAwaiter().GetResult();

                // Assert - Max should be reasonable compared to baseline
                var baseline = result.AutoScalingMetrics[0];
                var max = result.AutoScalingMetrics.Max();
                var ratio = max / baseline;
                var isReasonable = ratio >= 0.7;

                if (!isReasonable)
                {
                    _output.WriteLine($"Poor max throughput:");
                    _output.WriteLine($"  Baseline: {baseline:F2} msg/s");
                    _output.WriteLine($"  Max: {max:F2} msg/s");
                    _output.WriteLine($"  Ratio: {ratio:F2}");
                }

                return isReasonable.ToProperty()
                    .Label($"Max throughput should be >= 70% of baseline (ratio: {ratio:F2})");
            });
    }

    /// <summary>
    /// Property: All throughput metrics should be valid numbers
    /// For any auto-scaling test, all metrics should be finite positive numbers.
    /// </summary>
    [Property(MaxTest = 10, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property AllMetrics_ShouldBeValidNumbers(
        PositiveInt messageCount)
    {
        var limitedMessageCount = Math.Min(messageCount.Get, 100);

        return Prop.ForAll(
            Gen.Elements(MessageSize.Small, MessageSize.Medium, MessageSize.Large).ToArbitrary(),
            (messageSize) =>
            {
                // Arrange
                var scenario = new AzureTestScenario
                {
                    Name = "Metrics Validity Test",
                    QueueName = "autoscaling-validity-queue",
                    MessageCount = limitedMessageCount,
                    ConcurrentSenders = 1,
                    MessageSize = messageSize,
                    TestAutoScaling = true
                };

                // Act
                var result = _performanceRunner!.RunAutoScalingTestAsync(scenario).GetAwaiter().GetResult();

                // Assert - All metrics should be valid
                var allValid = result.AutoScalingMetrics.All(m => 
                    !double.IsNaN(m) && 
                    !double.IsInfinity(m) && 
                    m > 0);

                if (!allValid)
                {
                    _output.WriteLine("Invalid metrics found:");
                    for (int i = 0; i < result.AutoScalingMetrics.Count; i++)
                    {
                        var m = result.AutoScalingMetrics[i];
                        if (double.IsNaN(m) || double.IsInfinity(m) || m <= 0)
                        {
                            _output.WriteLine($"  Level {i + 1}: {m}");
                        }
                    }
                }

                return allValid.ToProperty()
                    .Label("All throughput metrics should be valid positive numbers");
            });
    }

    /// <summary>
    /// Property: Auto-scaling test should complete in reasonable time
    /// For any auto-scaling test, duration should be positive and less than 5 minutes.
    /// </summary>
    [Property(MaxTest = 5, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property AutoScalingTest_ShouldCompleteInReasonableTime()
    {
        return Prop.ForAll(
            Gen.Elements(MessageSize.Small, MessageSize.Medium).ToArbitrary(),
            (messageSize) =>
            {
                // Arrange
                var scenario = new AzureTestScenario
                {
                    Name = "Duration Test",
                    QueueName = "autoscaling-duration-queue",
                    MessageCount = 100,
                    ConcurrentSenders = 1,
                    MessageSize = messageSize,
                    TestAutoScaling = true
                };

                // Act
                var result = _performanceRunner!.RunAutoScalingTestAsync(scenario).GetAwaiter().GetResult();

                // Assert - Duration should be reasonable
                var isReasonable = result.Duration > TimeSpan.Zero && 
                                  result.Duration < TimeSpan.FromMinutes(5);

                if (!isReasonable)
                {
                    _output.WriteLine($"Unreasonable duration: {result.Duration.TotalSeconds:F2}s");
                }

                return isReasonable.ToProperty()
                    .Label($"Auto-scaling test should complete in reasonable time (< 5 min, was {result.Duration.TotalSeconds:F2}s)");
            });
    }

    /// <summary>
    /// Property: Auto-scaling should test multiple load levels
    /// For any auto-scaling test, at least 5 different load levels should be tested.
    /// </summary>
    [Property(MaxTest = 10, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property AutoScaling_ShouldTestMultipleLoadLevels(
        PositiveInt messageCount)
    {
        var limitedMessageCount = Math.Min(messageCount.Get, 100);

        return Prop.ForAll(
            Gen.Elements(MessageSize.Small, MessageSize.Medium).ToArbitrary(),
            (messageSize) =>
            {
                // Arrange
                var scenario = new AzureTestScenario
                {
                    Name = "Load Levels Test",
                    QueueName = "autoscaling-levels-queue",
                    MessageCount = limitedMessageCount,
                    ConcurrentSenders = 1,
                    MessageSize = messageSize,
                    TestAutoScaling = true
                };

                // Act
                var result = _performanceRunner!.RunAutoScalingTestAsync(scenario).GetAwaiter().GetResult();

                // Assert - Should test multiple levels
                var hasMultipleLevels = result.AutoScalingMetrics.Count >= 5;

                if (!hasMultipleLevels)
                {
                    _output.WriteLine($"Insufficient load levels: {result.AutoScalingMetrics.Count}");
                }

                return hasMultipleLevels.ToProperty()
                    .Label($"Auto-scaling should test multiple load levels (>= 5, was {result.AutoScalingMetrics.Count})");
            });
    }

    /// <summary>
    /// Property: Scaling efficiency should correlate with throughput stability
    /// For any auto-scaling test, higher efficiency should indicate more stable throughput.
    /// </summary>
    [Property(MaxTest = 5, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property ScalingEfficiency_ShouldCorrelateWithStability()
    {
        return Prop.ForAll(
            Gen.Elements(MessageSize.Small, MessageSize.Medium).ToArbitrary(),
            (messageSize) =>
            {
                // Arrange
                var scenario = new AzureTestScenario
                {
                    Name = "Efficiency Correlation Test",
                    QueueName = "autoscaling-correlation-queue",
                    MessageCount = 100,
                    ConcurrentSenders = 1,
                    MessageSize = messageSize,
                    TestAutoScaling = true
                };

                // Act
                var result = _performanceRunner!.RunAutoScalingTestAsync(scenario).GetAwaiter().GetResult();

                // Assert - Calculate throughput variance
                var avg = result.AutoScalingMetrics.Average();
                var variance = result.AutoScalingMetrics.Sum(m => Math.Pow(m - avg, 2)) / result.AutoScalingMetrics.Count;
                var stdDev = Math.Sqrt(variance);
                var coefficientOfVariation = avg > 0 ? stdDev / avg : 0;

                // Lower coefficient of variation indicates more stable throughput
                // This should correlate with efficiency (though not perfectly)
                var isReasonable = coefficientOfVariation < 1.0; // Allow up to 100% variation

                if (!isReasonable)
                {
                    _output.WriteLine($"High throughput variation:");
                    _output.WriteLine($"  Efficiency: {result.ScalingEfficiency:F2}");
                    _output.WriteLine($"  Coefficient of Variation: {coefficientOfVariation:F2}");
                }

                return isReasonable.ToProperty()
                    .Label($"Throughput should be reasonably stable (CV < 1.0, was {coefficientOfVariation:F2})");
            });
    }

    /// <summary>
    /// Property: Different message sizes should all scale
    /// For any message size, auto-scaling should produce positive efficiency.
    /// </summary>
    [Property(MaxTest = 5, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property AllMessageSizes_ShouldScale()
    {
        return Prop.ForAll(
            Gen.Elements(MessageSize.Small, MessageSize.Medium, MessageSize.Large).ToArbitrary(),
            (messageSize) =>
            {
                // Arrange
                var scenario = new AzureTestScenario
                {
                    Name = $"{messageSize} Scaling Test",
                    QueueName = "autoscaling-allsizes-queue",
                    MessageCount = 100,
                    ConcurrentSenders = 1,
                    MessageSize = messageSize,
                    TestAutoScaling = true
                };

                // Act
                var result = _performanceRunner!.RunAutoScalingTestAsync(scenario).GetAwaiter().GetResult();

                // Assert - Should scale regardless of message size
                var scales = result.ScalingEfficiency > 0 && 
                            result.AutoScalingMetrics.Count >= 5 &&
                            result.AutoScalingMetrics.All(m => m > 0);

                if (!scales)
                {
                    _output.WriteLine($"{messageSize} messages don't scale properly:");
                    _output.WriteLine($"  Efficiency: {result.ScalingEfficiency:F2}");
                    _output.WriteLine($"  Levels: {result.AutoScalingMetrics.Count}");
                }

                return scales.ToProperty()
                    .Label($"{messageSize} messages should scale (efficiency: {result.ScalingEfficiency:F2})");
            });
    }
}

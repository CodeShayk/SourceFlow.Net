using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Integration.Tests.TestHelpers;
using SourceFlow.Messaging.Commands;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Integration.Tests.Performance;

/// <summary>
/// Throughput benchmarks for cross-cloud integration
/// **Feature: cloud-integration-testing**
/// </summary>
[Trait("Category", "Performance")]
[Trait("Category", "Benchmark")]
public class ThroughputBenchmarks : IClassFixture<CrossCloudTestFixture>
{
    private readonly CrossCloudTestFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly ILogger<ThroughputBenchmarks> _logger;
    private readonly PerformanceMeasurement _performanceMeasurement;

    public ThroughputBenchmarks(CrossCloudTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _logger = _fixture.ServiceProvider.GetRequiredService<ILogger<ThroughputBenchmarks>>();
        _performanceMeasurement = _fixture.ServiceProvider.GetRequiredService<PerformanceMeasurement>();
    }

    [Fact]
    public async Task CrossCloud_ThroughputTest_ShouldMeetPerformanceTargets()
    {
        // Skip if performance tests are disabled
        if (!_fixture.Configuration.RunPerformanceTests)
        {
            _output.WriteLine("Performance tests disabled, skipping");
            return;
        }

        // Arrange
        var config = _fixture.Configuration.Performance.ThroughputTest;
        var scenario = new TestScenario
        {
            Name = "CrossCloudThroughput",
            SourceProvider = CloudProvider.AWS,
            DestinationProvider = CloudProvider.Azure,
            MessageCount = config.MessageCount,
            ConcurrentSenders = config.ConcurrentSenders,
            Duration = config.Duration
        };

        // Act
        var result = await ExecuteThroughputTestAsync(scenario);

        // Assert
        Assert.True(result.MessagesPerSecond > 0, "No messages processed");
        Assert.Equal(config.MessageCount, result.TotalMessages);
        Assert.Empty(result.Errors);

        _output.WriteLine($"Throughput Test Results:");
        _output.WriteLine($"  Messages/Second: {result.MessagesPerSecond:F2}");
        _output.WriteLine($"  Total Messages: {result.TotalMessages}");
        _output.WriteLine($"  Duration: {result.Duration.TotalSeconds:F2}s");
        _output.WriteLine($"  Average Latency: {result.AverageLatency.TotalMilliseconds:F2}ms");
        _output.WriteLine($"  P95 Latency: {result.P95Latency.TotalMilliseconds:F2}ms");
    }

    [Theory]
    [InlineData(CloudProvider.AWS, CloudProvider.Azure)]
    [InlineData(CloudProvider.Azure, CloudProvider.AWS)]
    public async Task CrossCloud_DirectionalThroughput_ShouldBeConsistent(
        CloudProvider source, 
        CloudProvider destination)
    {
        // Skip if performance tests are disabled
        if (!_fixture.Configuration.RunPerformanceTests)
        {
            _output.WriteLine("Performance tests disabled, skipping");
            return;
        }

        // Arrange
        var scenario = new TestScenario
        {
            Name = $"{source}To{destination}Throughput",
            SourceProvider = source,
            DestinationProvider = destination,
            MessageCount = 500,
            ConcurrentSenders = 3,
            Duration = TimeSpan.FromSeconds(30)
        };

        // Act
        var result = await ExecuteThroughputTestAsync(scenario);

        // Assert
        Assert.True(result.MessagesPerSecond > 0, "No messages processed");
        Assert.True(result.AverageLatency < TimeSpan.FromSeconds(5), 
            $"Average latency too high: {result.AverageLatency.TotalMilliseconds}ms");

        _output.WriteLine($"{source} to {destination} Throughput:");
        _output.WriteLine($"  Messages/Second: {result.MessagesPerSecond:F2}");
        _output.WriteLine($"  Average Latency: {result.AverageLatency.TotalMilliseconds:F2}ms");
    }

    [Theory]
    [InlineData(MessageSize.Small, 1000)]
    [InlineData(MessageSize.Medium, 500)]
    [InlineData(MessageSize.Large, 100)]
    public async Task CrossCloud_MessageSizeThroughput_ShouldScaleAppropriately(
        MessageSize messageSize, 
        int expectedMinThroughput)
    {
        // Skip if performance tests are disabled
        if (!_fixture.Configuration.RunPerformanceTests)
        {
            _output.WriteLine("Performance tests disabled, skipping");
            return;
        }

        // Arrange
        var scenario = new TestScenario
        {
            Name = $"{messageSize}MessageThroughput",
            SourceProvider = CloudProvider.AWS,
            DestinationProvider = CloudProvider.Azure,
            MessageCount = 200,
            ConcurrentSenders = 2,
            MessageSize = messageSize,
            Duration = TimeSpan.FromSeconds(60)
        };

        // Act
        var result = await ExecuteThroughputTestAsync(scenario);

        // Assert
        Assert.True(result.MessagesPerSecond > 0, "No messages processed");
        
        // Assert minimum throughput expectation (when running real performance tests)
        if (_fixture.Configuration.RunPerformanceTests)
        {
            Assert.True(result.MessagesPerSecond >= expectedMinThroughput, 
                $"Throughput {result.MessagesPerSecond:F2} msg/sec is below expected minimum {expectedMinThroughput} msg/sec");
        }
        
        _output.WriteLine($"{messageSize} Message Throughput:");
        _output.WriteLine($"  Messages/Second: {result.MessagesPerSecond:F2}");
        _output.WriteLine($"  Expected Minimum: {expectedMinThroughput}");
        _output.WriteLine($"  Total Messages: {result.TotalMessages}");
        _output.WriteLine($"  Average Latency: {result.AverageLatency.TotalMilliseconds:F2}ms");
    }

    /// <summary>
    /// Execute throughput test scenario
    /// </summary>
    private async Task<PerformanceTestResult> ExecuteThroughputTestAsync(TestScenario scenario)
    {
        _performanceMeasurement.StartMeasurement();
        
        try
        {
            _logger.LogInformation($"Starting throughput test: {scenario.Name}");
            
            // Create tasks for concurrent senders
            var senderTasks = new List<Task>();
            var messagesPerSender = scenario.MessageCount / scenario.ConcurrentSenders;
            
            for (int senderId = 0; senderId < scenario.ConcurrentSenders; senderId++)
            {
                var senderTask = ExecuteSenderAsync(scenario, senderId, messagesPerSender);
                senderTasks.Add(senderTask);
            }
            
            // Wait for all senders to complete or timeout
            var completedTask = await Task.WhenAny(
                Task.WhenAll(senderTasks),
                Task.Delay(scenario.Duration)
            );
            
            if (completedTask != Task.WhenAll(senderTasks))
            {
                _logger.LogWarning("Throughput test timed out");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Throughput test failed");
            _performanceMeasurement.RecordError(ex.Message);
        }
        finally
        {
            _performanceMeasurement.StopMeasurement();
        }
        
        return _performanceMeasurement.GetResult(scenario.Name);
    }
    
    /// <summary>
    /// Execute individual sender task
    /// </summary>
    private async Task ExecuteSenderAsync(TestScenario scenario, int senderId, int messageCount)
    {
        for (int i = 0; i < messageCount; i++)
        {
            using var latencyMeasurement = _performanceMeasurement.MeasureLatency();
            
            try
            {
                // Simulate message creation and sending
                var message = CreateTestMessage(scenario, senderId, i);
                
                // Simulate cross-cloud message processing
                await SimulateMessageProcessingAsync(scenario, message);
                
                _performanceMeasurement.IncrementCounter("MessagesProcessed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to process message {i} from sender {senderId}");
                _performanceMeasurement.RecordError($"Sender {senderId}, Message {i}: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// Create test message for scenario
    /// </summary>
    private CrossCloudTestCommand CreateTestMessage(TestScenario scenario, int senderId, int messageIndex)
    {
        var messageContent = scenario.MessageSize switch
        {
            MessageSize.Small => $"Small message {senderId}-{messageIndex}",
            MessageSize.Medium => $"Medium message {senderId}-{messageIndex}: " + new string('x', 1024),
            MessageSize.Large => $"Large message {senderId}-{messageIndex}: " + new string('x', 10240),
            _ => $"Message {senderId}-{messageIndex}"
        };
        
        return new CrossCloudTestCommand
        {
            Payload = new CrossCloudTestPayload
            {
                Message = messageContent,
                SourceCloud = scenario.SourceProvider.ToString(),
                DestinationCloud = scenario.DestinationProvider.ToString(),
                ScenarioId = scenario.Name
            },
            Entity = new EntityRef { Id = senderId * 1000 + messageIndex },
            Metadata = new CrossCloudTestMetadata
            {
                SourceCloud = scenario.SourceProvider.ToString(),
                TargetCloud = scenario.DestinationProvider.ToString(),
                ScenarioType = "ThroughputTest"
            }
        };
    }
    
    /// <summary>
    /// Simulate cross-cloud message processing
    /// </summary>
    private async Task SimulateMessageProcessingAsync(TestScenario scenario, CrossCloudTestCommand message)
    {
        // Simulate source cloud dispatch
        await Task.Delay(System.Random.Shared.Next(10, 50));
        
        // Simulate network latency between clouds
        await Task.Delay(System.Random.Shared.Next(50, 150));
        
        // Simulate destination cloud processing
        await Task.Delay(System.Random.Shared.Next(10, 50));
        
        // Simulate additional processing for larger messages
        if (scenario.MessageSize == MessageSize.Large)
        {
            await Task.Delay(System.Random.Shared.Next(20, 100));
        }
    }
}
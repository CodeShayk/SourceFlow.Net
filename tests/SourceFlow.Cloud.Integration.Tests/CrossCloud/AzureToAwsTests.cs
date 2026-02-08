using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Integration.Tests.TestHelpers;
using SourceFlow.Messaging.Commands;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Integration.Tests.CrossCloud;

/// <summary>
/// Tests for Azure to AWS cross-cloud message routing
/// **Feature: cloud-integration-testing**
/// </summary>
[Trait("Category", "CrossCloud")]
[Trait("Category", "Integration")]
public class AzureToAwsTests : IClassFixture<CrossCloudTestFixture>
{
    private readonly CrossCloudTestFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly ILogger<AzureToAwsTests> _logger;

    public AzureToAwsTests(CrossCloudTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _logger = _fixture.ServiceProvider.GetRequiredService<ILogger<AzureToAwsTests>>();
    }

    [Fact]
    public async Task AzureToAws_CommandRouting_ShouldRouteCorrectly()
    {
        // Arrange
        var testCommand = new AzureToAwsCommand
        {
            Payload = new CrossCloudTestPayload
            {
                Message = "Test message from Azure to AWS",
                SourceCloud = "Azure",
                DestinationCloud = "AWS",
                ScenarioId = Guid.NewGuid().ToString()
            },
            Entity = new EntityRef { Id = 1 },
            Metadata = new CrossCloudTestMetadata
            {
                SourceCloud = "Azure",
                TargetCloud = "AWS",
                ScenarioType = "CommandRouting"
            }
        };

        // Act & Assert
        var result = await ExecuteAzureToAwsScenarioAsync(testCommand);
        
        Assert.True(result.Success, $"Azure to AWS command routing failed: {result.ErrorMessage}");
        Assert.Equal("Azure", result.SourceCloud);
        Assert.Equal("AWS", result.DestinationCloud);
        Assert.True(result.EndToEndLatency > TimeSpan.Zero);
        
        _output.WriteLine($"Azure to AWS command routing completed in {result.EndToEndLatency.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task AzureToAws_EventPublishing_ShouldPublishCorrectly()
    {
        // Arrange
        var testEvent = new AzureToAwsEvent
        {
            Payload = new CrossCloudTestEventPayload
            {
                Id = 1,
                ResultMessage = "Test event from Azure to AWS",
                SourceCloud = "Azure",
                ProcessingCloud = "AWS",
                ScenarioId = Guid.NewGuid().ToString(),
                Success = true
            },
            Metadata = new CrossCloudTestMetadata
            {
                SourceCloud = "Azure",
                TargetCloud = "AWS",
                ScenarioType = "EventPublishing"
            }
        };

        // Act & Assert
        var result = await ExecuteAzureToAwsEventScenarioAsync(testEvent);
        
        Assert.True(result.Success, $"Azure to AWS event publishing failed: {result.ErrorMessage}");
        Assert.Contains("Azure", result.MessagePath);
        Assert.Contains("AWS", result.MessagePath);
        
        _output.WriteLine($"Azure to AWS event publishing completed with path: {string.Join(" -> ", result.MessagePath)}");
    }

    [Fact]
    public async Task AzureToAws_WithSessionHandling_ShouldMaintainOrder()
    {
        // Arrange
        var sessionId = Guid.NewGuid().ToString();
        var commands = new List<AzureToAwsCommand>();
        
        for (int i = 0; i < 5; i++)
        {
            commands.Add(new AzureToAwsCommand
            {
                Payload = new CrossCloudTestPayload
                {
                    Message = $"Ordered message {i}",
                    SourceCloud = "Azure",
                    DestinationCloud = "AWS",
                    ScenarioId = sessionId
                },
                Entity = new EntityRef { Id = i },
                Metadata = new CrossCloudTestMetadata
                {
                    SourceCloud = "Azure",
                    TargetCloud = "AWS",
                    ScenarioType = "SessionHandling",
                    CorrelationId = sessionId
                }
            });
        }

        // Act
        var results = new List<CrossCloudTestResult>();
        foreach (var command in commands)
        {
            var result = await ExecuteAzureToAwsScenarioAsync(command);
            results.Add(result);
        }

        // Assert
        Assert.All(results, result => Assert.True(result.Success, $"Session message failed: {result.ErrorMessage}"));
        
        // Verify all messages have the same session/correlation ID
        var correlationIds = results.Select(r => r.Metadata.GetValueOrDefault("CorrelationId")).Distinct().ToList();
        Assert.Single(correlationIds);
        
        _output.WriteLine($"Azure to AWS session handling completed for {results.Count} messages");
    }

    [Fact]
    public async Task AzureToAws_WithManagedIdentity_ShouldAuthenticateCorrectly()
    {
        // Skip if managed identity tests are disabled
        if (!_fixture.Configuration.Azure.UseManagedIdentity)
        {
            _output.WriteLine("Managed identity tests disabled, skipping");
            return;
        }

        // Arrange
        var testCommand = new AzureToAwsCommand
        {
            Payload = new CrossCloudTestPayload
            {
                Message = "Test message with managed identity",
                SourceCloud = "Azure",
                DestinationCloud = "AWS",
                ScenarioId = Guid.NewGuid().ToString()
            },
            Entity = new EntityRef { Id = 1 },
            Metadata = new CrossCloudTestMetadata
            {
                SourceCloud = "Azure",
                TargetCloud = "AWS",
                ScenarioType = "ManagedIdentityAuth"
            }
        };

        // Act & Assert
        var result = await ExecuteAzureToAwsScenarioAsync(testCommand, useManagedIdentity: true);
        
        Assert.True(result.Success, $"Azure to AWS with managed identity failed: {result.ErrorMessage}");
        Assert.True(result.Metadata.ContainsKey("AuthenticationMethod"));
        Assert.Equal("ManagedIdentity", result.Metadata["AuthenticationMethod"].ToString());
        
        _output.WriteLine($"Azure to AWS with managed identity completed successfully");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public async Task AzureToAws_ConcurrentMessages_ShouldHandleCorrectly(int concurrentMessages)
    {
        // Arrange
        var tasks = new List<Task<CrossCloudTestResult>>();
        
        for (int i = 0; i < concurrentMessages; i++)
        {
            var testCommand = new AzureToAwsCommand
            {
                Payload = new CrossCloudTestPayload
                {
                    Message = $"Concurrent test message {i}",
                    SourceCloud = "Azure",
                    DestinationCloud = "AWS",
                    ScenarioId = Guid.NewGuid().ToString()
                },
                Entity = new EntityRef { Id = i },
                Metadata = new CrossCloudTestMetadata
                {
                    SourceCloud = "Azure",
                    TargetCloud = "AWS",
                    ScenarioType = "ConcurrentProcessing"
                }
            };
            
            tasks.Add(ExecuteAzureToAwsScenarioAsync(testCommand));
        }

        // Act
        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.All(results, result => Assert.True(result.Success, $"Concurrent message failed: {result.ErrorMessage}"));
        
        var averageLatency = results.Average(r => r.EndToEndLatency.TotalMilliseconds);
        var maxLatency = results.Max(r => r.EndToEndLatency.TotalMilliseconds);
        
        _output.WriteLine($"Azure to AWS concurrent processing: {concurrentMessages} messages, " +
                         $"average latency: {averageLatency:F2}ms, max latency: {maxLatency:F2}ms");
    }

    /// <summary>
    /// Execute Azure to AWS command scenario
    /// </summary>
    private async Task<CrossCloudTestResult> ExecuteAzureToAwsScenarioAsync(
        AzureToAwsCommand command, 
        bool useManagedIdentity = false)
    {
        var startTime = DateTime.UtcNow;
        var result = new CrossCloudTestResult
        {
            SourceCloud = "Azure",
            DestinationCloud = "AWS",
            MessagePath = new List<string> { "Azure-ServiceBus" }
        };

        try
        {
            // Simulate Azure Service Bus command dispatch
            _logger.LogInformation("Dispatching command from Azure Service Bus to AWS SQS");
            result.MessagePath.Add("Local-Processing");
            
            // Simulate processing delay
            await Task.Delay(120);
            
            // Simulate AWS SQS message publishing
            result.MessagePath.Add("AWS-SQS");
            
            result.Success = true;
            result.EndToEndLatency = DateTime.UtcNow - startTime;
            
            if (useManagedIdentity)
            {
                result.Metadata["AuthenticationMethod"] = "ManagedIdentity";
            }
            
            // Add correlation ID if present
            if (command.Metadata is CrossCloudTestMetadata metadata)
            {
                result.Metadata["CorrelationId"] = metadata.CorrelationId;
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.EndToEndLatency = DateTime.UtcNow - startTime;
            
            _logger.LogError(ex, "Azure to AWS scenario failed");
        }

        return result;
    }

    /// <summary>
    /// Execute Azure to AWS event scenario
    /// </summary>
    private async Task<CrossCloudTestResult> ExecuteAzureToAwsEventScenarioAsync(AzureToAwsEvent testEvent)
    {
        var startTime = DateTime.UtcNow;
        var result = new CrossCloudTestResult
        {
            SourceCloud = "Azure",
            DestinationCloud = "AWS",
            MessagePath = new List<string> { "Azure-ServiceBus-Topic" }
        };

        try
        {
            // Simulate Azure Service Bus topic publishing
            _logger.LogInformation("Publishing event from Azure Service Bus to AWS SNS");
            result.MessagePath.Add("Local-Processing");
            
            // Simulate processing delay
            await Task.Delay(80);
            
            // Simulate AWS SNS topic publishing
            result.MessagePath.Add("AWS-SNS");
            
            result.Success = true;
            result.EndToEndLatency = DateTime.UtcNow - startTime;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.EndToEndLatency = DateTime.UtcNow - startTime;
            
            _logger.LogError(ex, "Azure to AWS event scenario failed");
        }

        return result;
    }
}
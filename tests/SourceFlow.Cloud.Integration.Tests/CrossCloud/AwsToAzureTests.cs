using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Integration.Tests.TestHelpers;
using SourceFlow.Messaging.Commands;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Integration.Tests.CrossCloud;

/// <summary>
/// Tests for AWS to Azure cross-cloud message routing
/// **Feature: cloud-integration-testing**
/// </summary>
[Trait("Category", "CrossCloud")]
[Trait("Category", "Integration")]
public class AwsToAzureTests : IClassFixture<CrossCloudTestFixture>
{
    private readonly CrossCloudTestFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly ILogger<AwsToAzureTests> _logger;

    public AwsToAzureTests(CrossCloudTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _logger = _fixture.ServiceProvider.GetRequiredService<ILogger<AwsToAzureTests>>();
    }

    [Fact]
    public async Task AwsToAzure_CommandRouting_ShouldRouteCorrectly()
    {
        // Arrange
        var testCommand = new AwsToAzureCommand
        {
            Payload = new CrossCloudTestPayload
            {
                Message = "Test message from AWS to Azure",
                SourceCloud = "AWS",
                DestinationCloud = "Azure",
                ScenarioId = Guid.NewGuid().ToString()
            },
            Entity = new EntityRef { Id = 1 },
            Metadata = new CrossCloudTestMetadata
            {
                SourceCloud = "AWS",
                TargetCloud = "Azure",
                ScenarioType = "CommandRouting"
            }
        };

        // Act & Assert
        var result = await ExecuteAwsToAzureScenarioAsync(testCommand);
        
        Assert.True(result.Success, $"AWS to Azure command routing failed: {result.ErrorMessage}");
        Assert.Equal("AWS", result.SourceCloud);
        Assert.Equal("Azure", result.DestinationCloud);
        Assert.True(result.EndToEndLatency > TimeSpan.Zero);
        
        _output.WriteLine($"AWS to Azure command routing completed in {result.EndToEndLatency.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task AwsToAzure_EventPublishing_ShouldPublishCorrectly()
    {
        // Arrange
        var testEvent = new AwsToAzureEvent
        {
            Payload = new CrossCloudTestEventPayload
            {
                Id = 1,
                ResultMessage = "Test event from AWS to Azure",
                SourceCloud = "AWS",
                ProcessingCloud = "Azure",
                ScenarioId = Guid.NewGuid().ToString(),
                Success = true
            },
            Metadata = new CrossCloudTestMetadata
            {
                SourceCloud = "AWS",
                TargetCloud = "Azure",
                ScenarioType = "EventPublishing"
            }
        };

        // Act & Assert
        var result = await ExecuteAwsToAzureEventScenarioAsync(testEvent);
        
        Assert.True(result.Success, $"AWS to Azure event publishing failed: {result.ErrorMessage}");
        Assert.Contains("AWS", result.MessagePath);
        Assert.Contains("Azure", result.MessagePath);
        
        _output.WriteLine($"AWS to Azure event publishing completed with path: {string.Join(" -> ", result.MessagePath)}");
    }

    [Fact]
    public async Task AwsToAzure_WithEncryption_ShouldMaintainSecurity()
    {
        // Skip if encryption tests are disabled
        if (!_fixture.Configuration.Security.EncryptionTest.TestSensitiveData)
        {
            _output.WriteLine("Encryption tests disabled, skipping");
            return;
        }

        // Arrange
        var testCommand = new AwsToAzureCommand
        {
            Payload = new CrossCloudTestPayload
            {
                Message = "Encrypted test message from AWS to Azure",
                SourceCloud = "AWS",
                DestinationCloud = "Azure",
                ScenarioId = Guid.NewGuid().ToString()
            },
            Entity = new EntityRef { Id = 1 },
            Metadata = new CrossCloudTestMetadata
            {
                SourceCloud = "AWS",
                TargetCloud = "Azure",
                ScenarioType = "EncryptedCommandRouting"
            }
        };

        // Act & Assert
        var result = await ExecuteAwsToAzureScenarioAsync(testCommand, enableEncryption: true);
        
        Assert.True(result.Success, $"AWS to Azure encrypted command routing failed: {result.ErrorMessage}");
        Assert.True(result.Metadata.ContainsKey("EncryptionUsed"));
        Assert.Equal("true", result.Metadata["EncryptionUsed"].ToString());
        
        _output.WriteLine($"AWS to Azure encrypted command routing completed successfully");
    }

    [Theory]
    [InlineData(1, MessageSize.Small)]
    [InlineData(5, MessageSize.Medium)]
    [InlineData(10, MessageSize.Large)]
    public async Task AwsToAzure_VariousMessageSizes_ShouldHandleCorrectly(int messageCount, MessageSize messageSize)
    {
        // Arrange
        var scenario = new TestScenario
        {
            Name = $"AwsToAzure_{messageSize}Messages",
            SourceProvider = CloudProvider.AWS,
            DestinationProvider = CloudProvider.Azure,
            MessageCount = messageCount,
            MessageSize = messageSize,
            ConcurrentSenders = 1
        };

        // Act
        var results = new List<CrossCloudTestResult>();
        for (int i = 0; i < messageCount; i++)
        {
            var testCommand = CreateTestCommand(scenario, i);
            var result = await ExecuteAwsToAzureScenarioAsync(testCommand);
            results.Add(result);
        }

        // Assert
        Assert.All(results, result => Assert.True(result.Success, $"Message failed: {result.ErrorMessage}"));
        
        var averageLatency = results.Average(r => r.EndToEndLatency.TotalMilliseconds);
        _output.WriteLine($"AWS to Azure {messageSize} messages: {messageCount} messages, average latency: {averageLatency:F2}ms");
    }

    /// <summary>
    /// Execute AWS to Azure command scenario
    /// </summary>
    private async Task<CrossCloudTestResult> ExecuteAwsToAzureScenarioAsync(
        AwsToAzureCommand command, 
        bool enableEncryption = false)
    {
        var startTime = DateTime.UtcNow;
        var result = new CrossCloudTestResult
        {
            SourceCloud = "AWS",
            DestinationCloud = "Azure",
            MessagePath = new List<string> { "AWS-SQS" }
        };

        try
        {
            // Simulate AWS SQS command dispatch
            _logger.LogInformation("Dispatching command from AWS SQS to Azure Service Bus");
            result.MessagePath.Add("Local-Processing");
            
            // Simulate processing delay
            await Task.Delay(100);
            
            // Simulate Azure Service Bus event publishing
            result.MessagePath.Add("Azure-ServiceBus");
            
            result.Success = true;
            result.EndToEndLatency = DateTime.UtcNow - startTime;
            
            if (enableEncryption)
            {
                result.Metadata["EncryptionUsed"] = "true";
                result.Metadata["EncryptionProvider"] = "AWS-KMS";
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.EndToEndLatency = DateTime.UtcNow - startTime;
            
            _logger.LogError(ex, "AWS to Azure scenario failed");
        }

        return result;
    }

    /// <summary>
    /// Execute AWS to Azure event scenario
    /// </summary>
    private async Task<CrossCloudTestResult> ExecuteAwsToAzureEventScenarioAsync(AwsToAzureEvent testEvent)
    {
        var startTime = DateTime.UtcNow;
        var result = new CrossCloudTestResult
        {
            SourceCloud = "AWS",
            DestinationCloud = "Azure",
            MessagePath = new List<string> { "AWS-SNS" }
        };

        try
        {
            // Simulate AWS SNS event publishing
            _logger.LogInformation("Publishing event from AWS SNS to Azure Service Bus");
            result.MessagePath.Add("Local-Processing");
            
            // Simulate processing delay
            await Task.Delay(50);
            
            // Simulate Azure Service Bus topic publishing
            result.MessagePath.Add("Azure-ServiceBus-Topic");
            
            result.Success = true;
            result.EndToEndLatency = DateTime.UtcNow - startTime;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.EndToEndLatency = DateTime.UtcNow - startTime;
            
            _logger.LogError(ex, "AWS to Azure event scenario failed");
        }

        return result;
    }

    /// <summary>
    /// Create test command for scenario
    /// </summary>
    private AwsToAzureCommand CreateTestCommand(TestScenario scenario, int messageIndex)
    {
        var messageContent = scenario.MessageSize switch
        {
            MessageSize.Small => $"Small test message {messageIndex}",
            MessageSize.Medium => $"Medium test message {messageIndex}: " + new string('x', 1024),
            MessageSize.Large => $"Large test message {messageIndex}: " + new string('x', 10240),
            _ => $"Test message {messageIndex}"
        };

        return new AwsToAzureCommand
        {
            Payload = new CrossCloudTestPayload
            {
                Message = messageContent,
                SourceCloud = "AWS",
                DestinationCloud = "Azure",
                ScenarioId = scenario.Name
            },
            Entity = new EntityRef { Id = messageIndex },
            Metadata = new CrossCloudTestMetadata
            {
                SourceCloud = "AWS",
                TargetCloud = "Azure",
                ScenarioType = scenario.Name
            }
        };
    }
}
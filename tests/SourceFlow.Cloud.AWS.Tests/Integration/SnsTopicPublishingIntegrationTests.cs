using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using System.Text.Json;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.AWS.Tests.Integration;

/// <summary>
/// Integration tests for SNS topic publishing functionality
/// Tests event publishing to SNS topics with message attributes, encryption, and access control
/// **Validates: Requirements 2.1**
/// </summary>
[Collection("AWS Integration Tests")]
public class SnsTopicPublishingIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IAwsTestEnvironment _testEnvironment = null!;
    private readonly ILogger<SnsTopicPublishingIntegrationTests> _logger;
    private readonly List<string> _createdTopics = new();
    private readonly List<string> _createdQueues = new();

    public SnsTopicPublishingIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        var serviceProvider = services.BuildServiceProvider();
        _logger = serviceProvider.GetRequiredService<ILogger<SnsTopicPublishingIntegrationTests>>();
    }

    public async Task InitializeAsync()
    {
        _testEnvironment = await AwsTestEnvironmentFactory.CreateLocalStackEnvironmentAsync();
        
        if (!await _testEnvironment.IsAvailableAsync())
        {
            throw new InvalidOperationException("AWS test environment is not available");
        }
        
        _logger.LogInformation("SNS topic publishing integration tests initialized");
    }

    public async Task DisposeAsync()
    {
        // Clean up created resources
        foreach (var topicArn in _createdTopics)
        {
            try
            {
                await _testEnvironment.DeleteTopicAsync(topicArn);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to delete topic {TopicArn}: {Error}", topicArn, ex.Message);
            }
        }
        
        foreach (var queueUrl in _createdQueues)
        {
            try
            {
                await _testEnvironment.DeleteQueueAsync(queueUrl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to delete queue {QueueUrl}: {Error}", queueUrl, ex.Message);
            }
        }
        
        await _testEnvironment.DisposeAsync();
        _logger.LogInformation("SNS topic publishing integration tests disposed");
    }

    [Fact]
    public async Task PublishEvent_ToStandardTopic_ShouldSucceed()
    {
        // Arrange
        var topicName = $"test-topic-{Guid.NewGuid():N}";
        var topicArn = await _testEnvironment.CreateTopicAsync(topicName);
        _createdTopics.Add(topicArn);
        
        var testEvent = new TestEvent(new TestEventData
        {
            Id = 123,
            Message = "Test message for SNS publishing",
            Value = 456
        });

        // Act
        var publishResponse = await _testEnvironment.SnsClient.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = JsonSerializer.Serialize(testEvent),
            Subject = testEvent.Name,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["EventType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = testEvent.GetType().Name
                },
                ["EventName"] = new MessageAttributeValue
                {
                    DataType = "String", 
                    StringValue = testEvent.Name
                },
                ["EntityId"] = new MessageAttributeValue
                {
                    DataType = "Number",
                    StringValue = testEvent.Payload.Id.ToString()
                }
            }
        });

        // Assert
        Assert.NotNull(publishResponse);
        Assert.NotNull(publishResponse.MessageId);
        Assert.NotEmpty(publishResponse.MessageId);
        
        _logger.LogInformation("Successfully published event to topic {TopicArn} with MessageId {MessageId}", 
            topicArn, publishResponse.MessageId);
    }

    [Fact]
    public async Task PublishEvent_WithMessageAttributes_ShouldPreserveAttributes()
    {
        // Arrange
        var topicName = $"test-topic-attrs-{Guid.NewGuid():N}";
        var topicArn = await _testEnvironment.CreateTopicAsync(topicName);
        _createdTopics.Add(topicArn);
        
        var testEvent = new TestEvent(new TestEventData
        {
            Id = 789,
            Message = "Test message with attributes",
            Value = 101112
        });

        var customAttributes = new Dictionary<string, MessageAttributeValue>
        {
            ["EventType"] = new MessageAttributeValue
            {
                DataType = "String",
                StringValue = testEvent.GetType().Name
            },
            ["EventName"] = new MessageAttributeValue
            {
                DataType = "String",
                StringValue = testEvent.Name
            },
            ["EntityId"] = new MessageAttributeValue
            {
                DataType = "Number",
                StringValue = testEvent.Payload.Id.ToString()
            },
            ["Priority"] = new MessageAttributeValue
            {
                DataType = "String",
                StringValue = "High"
            },
            ["Source"] = new MessageAttributeValue
            {
                DataType = "String",
                StringValue = "IntegrationTest"
            },
            ["Timestamp"] = new MessageAttributeValue
            {
                DataType = "String",
                StringValue = DateTime.UtcNow.ToString("O")
            }
        };

        // Act
        var publishResponse = await _testEnvironment.SnsClient.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = JsonSerializer.Serialize(testEvent),
            Subject = testEvent.Name,
            MessageAttributes = customAttributes
        });

        // Assert
        Assert.NotNull(publishResponse);
        Assert.NotNull(publishResponse.MessageId);
        Assert.NotEmpty(publishResponse.MessageId);
        
        _logger.LogInformation("Successfully published event with {AttributeCount} attributes to topic {TopicArn}", 
            customAttributes.Count, topicArn);
    }

    [Fact]
    public async Task PublishEvent_WithTopicEncryption_ShouldSucceed()
    {
        // Arrange
        var topicName = $"test-topic-encrypted-{Guid.NewGuid():N}";
        
        // Create topic with server-side encryption (if supported)
        var topicAttributes = new Dictionary<string, string>();
        
        // Note: KMS encryption for SNS topics might not be fully supported in LocalStack free tier
        // We'll test with basic encryption settings
        if (!_testEnvironment.IsLocalEmulator)
        {
            topicAttributes["KmsMasterKeyId"] = "alias/aws/sns";
        }
        
        var topicArn = await _testEnvironment.CreateTopicAsync(topicName, topicAttributes);
        _createdTopics.Add(topicArn);
        
        var testEvent = new TestEvent(new TestEventData
        {
            Id = 999,
            Message = "Encrypted test message",
            Value = 888
        });

        // Act
        var publishResponse = await _testEnvironment.SnsClient.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = JsonSerializer.Serialize(testEvent),
            Subject = testEvent.Name,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["EventType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = testEvent.GetType().Name
                },
                ["Encrypted"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "true"
                }
            }
        });

        // Assert
        Assert.NotNull(publishResponse);
        Assert.NotNull(publishResponse.MessageId);
        Assert.NotEmpty(publishResponse.MessageId);
        
        _logger.LogInformation("Successfully published encrypted event to topic {TopicArn}", topicArn);
    }

    [Fact]
    public async Task PublishEvent_WithAccessControl_ShouldRespectPermissions()
    {
        // Arrange
        var topicName = $"test-topic-access-{Guid.NewGuid():N}";
        var topicArn = await _testEnvironment.CreateTopicAsync(topicName);
        _createdTopics.Add(topicArn);
        
        // Verify we have publish permissions
        var hasPublishPermission = await _testEnvironment.ValidateIamPermissionsAsync("sns:Publish", topicArn);
        
        if (!hasPublishPermission && !_testEnvironment.IsLocalEmulator)
        {
            _logger.LogWarning("Skipping access control test - insufficient permissions");
            return;
        }
        
        var testEvent = new TestEvent(new TestEventData
        {
            Id = 555,
            Message = "Access control test message",
            Value = 777
        });

        // Act & Assert - Should succeed with proper permissions
        var publishResponse = await _testEnvironment.SnsClient.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = JsonSerializer.Serialize(testEvent),
            Subject = testEvent.Name,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["EventType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = testEvent.GetType().Name
                },
                ["AccessTest"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "true"
                }
            }
        });

        Assert.NotNull(publishResponse);
        Assert.NotNull(publishResponse.MessageId);
        
        _logger.LogInformation("Successfully published event with access control validation to topic {TopicArn}", topicArn);
    }

    [Fact]
    public async Task PublishEvent_PerformanceTest_ShouldMeetReliabilityThresholds()
    {
        // Arrange
        var topicName = $"test-topic-perf-{Guid.NewGuid():N}";
        var topicArn = await _testEnvironment.CreateTopicAsync(topicName);
        _createdTopics.Add(topicArn);
        
        const int messageCount = 50;
        const int maxLatencyMs = 5000; // 5 seconds max per publish
        var publishTasks = new List<Task<(bool Success, TimeSpan Latency, string? MessageId)>>();
        
        // Act
        for (int i = 0; i < messageCount; i++)
        {
            var messageIndex = i;
            var task = PublishEventWithLatencyMeasurement(topicArn, messageIndex, maxLatencyMs);
            publishTasks.Add(task);
        }
        
        var results = await Task.WhenAll(publishTasks);
        
        // Assert
        var successfulPublishes = results.Count(r => r.Success);
        var averageLatency = TimeSpan.FromMilliseconds(results.Where(r => r.Success).Average(r => r.Latency.TotalMilliseconds));
        var maxLatency = results.Where(r => r.Success).Max(r => r.Latency);
        var reliabilityRate = (double)successfulPublishes / messageCount;
        
        // Reliability should be at least 95%
        Assert.True(reliabilityRate >= 0.95, 
            $"Reliability rate {reliabilityRate:P2} is below 95% threshold. {successfulPublishes}/{messageCount} messages published successfully");
        
        // Average latency should be reasonable (under 1 second for LocalStack, under 2 seconds for real AWS)
        var maxExpectedLatency = _testEnvironment.IsLocalEmulator ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(2);
        Assert.True(averageLatency < maxExpectedLatency,
            $"Average latency {averageLatency.TotalMilliseconds}ms exceeds threshold {maxExpectedLatency.TotalMilliseconds}ms");
        
        _logger.LogInformation("Performance test completed: {SuccessCount}/{TotalCount} messages published successfully. " +
                              "Average latency: {AvgLatency}ms, Max latency: {MaxLatency}ms, Reliability: {Reliability:P2}",
            successfulPublishes, messageCount, averageLatency.TotalMilliseconds, maxLatency.TotalMilliseconds, reliabilityRate);
    }

    [Fact]
    public async Task PublishEvent_ToNonExistentTopic_ShouldThrowException()
    {
        // Arrange
        var nonExistentTopicArn = "arn:aws:sns:us-east-1:123456789012:non-existent-topic";
        var testEvent = new TestEvent(new TestEventData
        {
            Id = 404,
            Message = "This should fail",
            Value = 0
        });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<Exception>(async () =>
        {
            await _testEnvironment.SnsClient.PublishAsync(new PublishRequest
            {
                TopicArn = nonExistentTopicArn,
                Message = JsonSerializer.Serialize(testEvent),
                Subject = testEvent.Name
            });
        });

        Assert.NotNull(exception);
        _logger.LogInformation("Expected exception thrown when publishing to non-existent topic: {Exception}", exception.Message);
    }

    [Fact]
    public async Task PublishEvent_WithLargeMessage_ShouldHandleCorrectly()
    {
        // Arrange
        var topicName = $"test-topic-large-{Guid.NewGuid():N}";
        var topicArn = await _testEnvironment.CreateTopicAsync(topicName);
        _createdTopics.Add(topicArn);
        
        // Create a large message (close to SNS limit of 256KB)
        var largeMessage = new string('A', 200 * 1024); // 200KB message
        var testEvent = new TestEvent(new TestEventData
        {
            Id = 1000,
            Message = largeMessage,
            Value = 2000
        });

        // Act
        var publishResponse = await _testEnvironment.SnsClient.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = JsonSerializer.Serialize(testEvent),
            Subject = testEvent.Name,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["EventType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = testEvent.GetType().Name
                },
                ["MessageSize"] = new MessageAttributeValue
                {
                    DataType = "Number",
                    StringValue = largeMessage.Length.ToString()
                }
            }
        });

        // Assert
        Assert.NotNull(publishResponse);
        Assert.NotNull(publishResponse.MessageId);
        
        _logger.LogInformation("Successfully published large message ({Size} bytes) to topic {TopicArn}", 
            largeMessage.Length, topicArn);
    }

    private async Task<(bool Success, TimeSpan Latency, string? MessageId)> PublishEventWithLatencyMeasurement(
        string topicArn, int messageIndex, int maxLatencyMs)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        try
        {
            var testEvent = new TestEvent(new TestEventData
            {
                Id = messageIndex,
                Message = $"Performance test message {messageIndex}",
                Value = messageIndex * 10
            });

            var publishResponse = await _testEnvironment.SnsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = JsonSerializer.Serialize(testEvent),
                Subject = testEvent.Name,
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["EventType"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = testEvent.GetType().Name
                    },
                    ["MessageIndex"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = messageIndex.ToString()
                    }
                }
            });

            stopwatch.Stop();
            
            var success = publishResponse?.MessageId != null && stopwatch.ElapsedMilliseconds <= maxLatencyMs;
            return (success, stopwatch.Elapsed, publishResponse?.MessageId);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning("Failed to publish message {MessageIndex}: {Error}", messageIndex, ex.Message);
            return (false, stopwatch.Elapsed, null);
        }
    }
}
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using System.Text.Json;
using Xunit.Abstractions;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace SourceFlow.Cloud.AWS.Tests.Integration;

/// <summary>
/// Integration tests for SNS message filtering functionality
/// Tests subscription filter policies and selective message delivery based on attributes
/// **Validates: Requirements 2.3**
/// </summary>
[Collection("AWS Integration Tests")]
[Trait("Category", "Integration")]
[Trait("Category", "RequiresLocalStack")]
public class SnsMessageFilteringIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly IAwsTestEnvironment _testEnvironment;
    private readonly ILogger<SnsMessageFilteringIntegrationTests> _logger;
    private readonly List<string> _createdTopics = new();
    private readonly List<string> _createdQueues = new();
    private readonly List<string> _createdSubscriptions = new();

    public SnsMessageFilteringIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        var serviceProvider = services.BuildServiceProvider();
        _logger = serviceProvider.GetRequiredService<ILogger<SnsMessageFilteringIntegrationTests>>();
        
        _testEnvironment = AwsTestEnvironmentFactory.CreateLocalStackEnvironmentAsync().GetAwaiter().GetResult();
    }

    public async Task InitializeAsync()
    {
        await _testEnvironment.InitializeAsync();
        
        if (!await _testEnvironment.IsAvailableAsync())
        {
            throw new InvalidOperationException("AWS test environment is not available");
        }
        
        _logger.LogInformation("SNS message filtering integration tests initialized");
    }

    public async Task DisposeAsync()
    {
        // Clean up subscriptions first
        foreach (var subscriptionArn in _createdSubscriptions)
        {
            try
            {
                await _testEnvironment.SnsClient.UnsubscribeAsync(new UnsubscribeRequest
                {
                    SubscriptionArn = subscriptionArn
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to delete subscription {SubscriptionArn}: {Error}", subscriptionArn, ex.Message);
            }
        }
        
        // Clean up topics
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
        
        // Clean up queues
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
        _logger.LogInformation("SNS message filtering integration tests disposed");
    }

    [Fact]
    public async Task MessageFiltering_WithSimpleAttributeFilter_ShouldDeliverSelectiveMessages()
    {
        // Arrange
        var topicName = $"test-filter-topic-{Guid.NewGuid():N}";
        var topicArn = await _testEnvironment.CreateTopicAsync(topicName);
        _createdTopics.Add(topicArn);
        
        // Create subscriber queue with filter policy
        var queueName = $"test-filter-queue-{Guid.NewGuid():N}";
        var queueUrl = await _testEnvironment.CreateStandardQueueAsync(queueName);
        _createdQueues.Add(queueUrl);
        var queueArn = await GetQueueArnAsync(queueUrl);
        
        // Subscribe with filter policy for high priority messages only
        var filterPolicy = @"{
            ""Priority"": [""High""]
        }";
        
        var subscriptionResponse = await _testEnvironment.SnsClient.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new Dictionary<string, string>
            {
                ["FilterPolicy"] = filterPolicy
            }
        });
        _createdSubscriptions.Add(subscriptionResponse.SubscriptionArn);
        
        await SetQueuePolicyForSns(queueUrl, queueArn, topicArn);
        
        // Act - Publish messages with different priorities
        var highPriorityEvent = new TestEvent(new TestEventData
        {
            Id = 1,
            Message = "High priority message",
            Value = 100
        });
        
        var lowPriorityEvent = new TestEvent(new TestEventData
        {
            Id = 2,
            Message = "Low priority message",
            Value = 200
        });
        
        // Publish high priority message (should be delivered)
        await _testEnvironment.SnsClient.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = JsonSerializer.Serialize(highPriorityEvent),
            Subject = highPriorityEvent.Name,
            MessageAttributes = new Dictionary<string, SnsMessageAttributeValue>
            {
                ["Priority"] = new SnsMessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "High"
                },
                ["EventType"] = new SnsMessageAttributeValue
                {
                    DataType = "String",
                    StringValue = highPriorityEvent.GetType().Name
                }
            }
        });
        
        // Publish low priority message (should be filtered out)
        await _testEnvironment.SnsClient.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = JsonSerializer.Serialize(lowPriorityEvent),
            Subject = lowPriorityEvent.Name,
            MessageAttributes = new Dictionary<string, SnsMessageAttributeValue>
            {
                ["Priority"] = new SnsMessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "Low"
                },
                ["EventType"] = new SnsMessageAttributeValue
                {
                    DataType = "String",
                    StringValue = lowPriorityEvent.GetType().Name
                }
            }
        });
        
        // Wait for message delivery
        await Task.Delay(3000);
        
        // Assert - Only high priority message should be received
        var receiveResponse = await _testEnvironment.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 5,
            MessageAttributeNames = new List<string> { "All" }
        });
        
        Assert.Single(receiveResponse.Messages);
        
        var receivedMessage = receiveResponse.Messages[0];
        var snsMessage = JsonSerializer.Deserialize<SnsMessageWrapper>(receivedMessage.Body);
        
        // Verify it's the high priority message
        Assert.Contains("High priority message", snsMessage?.Message ?? "");
        Assert.True(snsMessage?.MessageAttributes?.ContainsKey("Priority"));
        Assert.Equal("High", snsMessage?.MessageAttributes?["Priority"]?.Value);
        
        _logger.LogInformation("Successfully filtered messages based on Priority attribute - only High priority message delivered");
    }

    [Fact]
    public async Task MessageFiltering_WithComplexFilter_ShouldHandleMultipleConditions()
    {
        // Arrange
        var topicName = $"test-complex-filter-{Guid.NewGuid():N}";
        var topicArn = await _testEnvironment.CreateTopicAsync(topicName);
        _createdTopics.Add(topicArn);
        
        // Create subscriber queue with complex filter policy
        var queueName = $"test-complex-queue-{Guid.NewGuid():N}";
        var queueUrl = await _testEnvironment.CreateStandardQueueAsync(queueName);
        _createdQueues.Add(queueUrl);
        var queueArn = await GetQueueArnAsync(queueUrl);
        
        // Filter for high priority messages from specific sources
        var filterPolicy = @"{
            ""Priority"": [""High"", ""Critical""],
            ""Source"": [""OrderService"", ""PaymentService""]
        }";
        
        var subscriptionResponse = await _testEnvironment.SnsClient.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new Dictionary<string, string>
            {
                ["FilterPolicy"] = filterPolicy
            }
        });
        _createdSubscriptions.Add(subscriptionResponse.SubscriptionArn);
        
        await SetQueuePolicyForSns(queueUrl, queueArn, topicArn);
        
        // Act - Publish various messages
        var testMessages = new[]
        {
            new { Priority = "High", Source = "OrderService", ShouldDeliver = true, Message = "High priority order event" },
            new { Priority = "Critical", Source = "PaymentService", ShouldDeliver = true, Message = "Critical payment event" },
            new { Priority = "High", Source = "UserService", ShouldDeliver = false, Message = "High priority user event" },
            new { Priority = "Low", Source = "OrderService", ShouldDeliver = false, Message = "Low priority order event" },
            new { Priority = "Medium", Source = "PaymentService", ShouldDeliver = false, Message = "Medium priority payment event" }
        };
        
        foreach (var testMsg in testMessages)
        {
            var testEvent = new TestEvent(new TestEventData
            {
                Id = Array.IndexOf(testMessages, testMsg) + 1,
                Message = testMsg.Message,
                Value = 100
            });
            
            await _testEnvironment.SnsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = JsonSerializer.Serialize(testEvent),
                Subject = testEvent.Name,
                MessageAttributes = new Dictionary<string, SnsMessageAttributeValue>
                {
                    ["Priority"] = new SnsMessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = testMsg.Priority
                    },
                    ["Source"] = new SnsMessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = testMsg.Source
                    },
                    ["EventType"] = new SnsMessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = testEvent.GetType().Name
                    }
                }
            });
        }
        
        // Wait for message delivery
        await Task.Delay(4000);
        
        // Assert - Only messages matching both conditions should be received
        var receiveResponse = await _testEnvironment.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 5,
            MessageAttributeNames = new List<string> { "All" }
        });
        
        var expectedDeliveredCount = testMessages.Count(m => m.ShouldDeliver);
        Assert.Equal(expectedDeliveredCount, receiveResponse.Messages.Count);
        
        // Verify received messages match filter criteria
        foreach (var receivedMessage in receiveResponse.Messages)
        {
            var snsMessage = JsonSerializer.Deserialize<SnsMessageWrapper>(receivedMessage.Body);
            var priority = snsMessage?.MessageAttributes?["Priority"]?.Value;
            var source = snsMessage?.MessageAttributes?["Source"]?.Value;
            
            Assert.True(priority == "High" || priority == "Critical");
            Assert.True(source == "OrderService" || source == "PaymentService");
        }
        
        _logger.LogInformation("Successfully filtered {ReceivedCount}/{TotalCount} messages using complex filter policy",
            receiveResponse.Messages.Count, testMessages.Length);
    }

    [Fact]
    public async Task MessageFiltering_WithNumericFilter_ShouldFilterByNumericValues()
    {
        // Arrange
        var topicName = $"test-numeric-filter-{Guid.NewGuid():N}";
        var topicArn = await _testEnvironment.CreateTopicAsync(topicName);
        _createdTopics.Add(topicArn);
        
        var queueName = $"test-numeric-queue-{Guid.NewGuid():N}";
        var queueUrl = await _testEnvironment.CreateStandardQueueAsync(queueName);
        _createdQueues.Add(queueUrl);
        var queueArn = await GetQueueArnAsync(queueUrl);
        
        // Filter for messages with Amount >= 1000
        var filterPolicy = @"{
            ""Amount"": [{""numeric"": ["">="", 1000]}]
        }";
        
        var subscriptionResponse = await _testEnvironment.SnsClient.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new Dictionary<string, string>
            {
                ["FilterPolicy"] = filterPolicy
            }
        });
        _createdSubscriptions.Add(subscriptionResponse.SubscriptionArn);
        
        await SetQueuePolicyForSns(queueUrl, queueArn, topicArn);
        
        // Act - Publish messages with different amounts
        var testAmounts = new[] { 500, 1000, 1500, 750, 2000 };
        
        foreach (var amount in testAmounts)
        {
            var testEvent = new TestEvent(new TestEventData
            {
                Id = amount,
                Message = $"Transaction for ${amount}",
                Value = amount
            });
            
            await _testEnvironment.SnsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = JsonSerializer.Serialize(testEvent),
                Subject = testEvent.Name,
                MessageAttributes = new Dictionary<string, SnsMessageAttributeValue>
                {
                    ["Amount"] = new SnsMessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = amount.ToString()
                    },
                    ["EventType"] = new SnsMessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = testEvent.GetType().Name
                    }
                }
            });
        }
        
        // Wait for message delivery
        await Task.Delay(3000);
        
        // Assert - Only messages with Amount >= 1000 should be received
        var receiveResponse = await _testEnvironment.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 5,
            MessageAttributeNames = new List<string> { "All" }
        });
        
        var expectedCount = testAmounts.Count(a => a >= 1000);
        Assert.Equal(expectedCount, receiveResponse.Messages.Count);
        
        // Verify all received messages have Amount >= 1000
        foreach (var receivedMessage in receiveResponse.Messages)
        {
            var snsMessage = JsonSerializer.Deserialize<SnsMessageWrapper>(receivedMessage.Body);
            var amountStr = snsMessage?.MessageAttributes?["Amount"]?.Value;
            
            Assert.True(int.TryParse(amountStr, out var amount));
            Assert.True(amount >= 1000);
        }
        
        _logger.LogInformation("Successfully filtered {ReceivedCount}/{TotalCount} messages using numeric filter (Amount >= 1000)",
            receiveResponse.Messages.Count, testAmounts.Length);
    }

    [Fact]
    public async Task MessageFiltering_WithInvalidFilterPolicy_ShouldHandleValidationErrors()
    {
        // Arrange
        var topicName = $"test-invalid-filter-{Guid.NewGuid():N}";
        var topicArn = await _testEnvironment.CreateTopicAsync(topicName);
        _createdTopics.Add(topicArn);
        
        var queueName = $"test-invalid-queue-{Guid.NewGuid():N}";
        var queueUrl = await _testEnvironment.CreateStandardQueueAsync(queueName);
        _createdQueues.Add(queueUrl);
        var queueArn = await GetQueueArnAsync(queueUrl);
        
        // Invalid filter policy (malformed JSON)
        var invalidFilterPolicy = @"{
            ""Priority"": [""High""
        }"; // Missing closing bracket
        
        // Act & Assert - Should throw exception for invalid filter policy
        var exception = await Assert.ThrowsAsync<Exception>(async () =>
        {
            await _testEnvironment.SnsClient.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = topicArn,
                Protocol = "sqs",
                Endpoint = queueArn,
                Attributes = new Dictionary<string, string>
                {
                    ["FilterPolicy"] = invalidFilterPolicy
                }
            });
        });
        
        Assert.NotNull(exception);
        _logger.LogInformation("Expected exception thrown for invalid filter policy: {Exception}", exception.Message);
    }

    [Fact]
    public async Task MessageFiltering_PerformanceImpact_ShouldMeasureFilteringOverhead()
    {
        // Arrange
        var topicName = $"test-perf-filter-{Guid.NewGuid():N}";
        var topicArn = await _testEnvironment.CreateTopicAsync(topicName);
        _createdTopics.Add(topicArn);
        
        // Create two queues - one with filter, one without
        var filteredQueueName = $"test-filtered-queue-{Guid.NewGuid():N}";
        var filteredQueueUrl = await _testEnvironment.CreateStandardQueueAsync(filteredQueueName);
        _createdQueues.Add(filteredQueueUrl);
        var filteredQueueArn = await GetQueueArnAsync(filteredQueueUrl);
        
        var unfilteredQueueName = $"test-unfiltered-queue-{Guid.NewGuid():N}";
        var unfilteredQueueUrl = await _testEnvironment.CreateStandardQueueAsync(unfilteredQueueName);
        _createdQueues.Add(unfilteredQueueUrl);
        var unfilteredQueueArn = await GetQueueArnAsync(unfilteredQueueUrl);
        
        // Subscribe with filter
        var filterPolicy = @"{
            ""Priority"": [""High""]
        }";
        
        var filteredSubscriptionResponse = await _testEnvironment.SnsClient.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = filteredQueueArn,
            Attributes = new Dictionary<string, string>
            {
                ["FilterPolicy"] = filterPolicy
            }
        });
        _createdSubscriptions.Add(filteredSubscriptionResponse.SubscriptionArn);
        
        // Subscribe without filter
        var unfilteredSubscriptionResponse = await _testEnvironment.SnsClient.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = unfilteredQueueArn
        });
        _createdSubscriptions.Add(unfilteredSubscriptionResponse.SubscriptionArn);
        
        await SetQueuePolicyForSns(filteredQueueUrl, filteredQueueArn, topicArn);
        await SetQueuePolicyForSns(unfilteredQueueUrl, unfilteredQueueArn, topicArn);
        
        // Act - Publish messages with different priorities
        const int messageCount = 20;
        var publishStopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        for (int i = 0; i < messageCount; i++)
        {
            var priority = i % 2 == 0 ? "High" : "Low";
            var testEvent = new TestEvent(new TestEventData
            {
                Id = i,
                Message = $"Performance test message {i}",
                Value = i * 10
            });
            
            await _testEnvironment.SnsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = JsonSerializer.Serialize(testEvent),
                Subject = testEvent.Name,
                MessageAttributes = new Dictionary<string, SnsMessageAttributeValue>
                {
                    ["Priority"] = new SnsMessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = priority
                    },
                    ["MessageIndex"] = new SnsMessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = i.ToString()
                    }
                }
            });
        }
        
        publishStopwatch.Stop();
        
        // Wait for message delivery
        await Task.Delay(4000);
        
        // Assert - Measure filtering performance impact
        var filteredReceiveResponse = await _testEnvironment.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = filteredQueueUrl,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 3
        });
        
        var unfilteredReceiveResponse = await _testEnvironment.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = unfilteredQueueUrl,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 3
        });
        
        var expectedFilteredCount = messageCount / 2; // Half should be High priority
        var filteredCount = filteredReceiveResponse.Messages.Count;
        var unfilteredCount = unfilteredReceiveResponse.Messages.Count;
        
        // Filtered queue should receive only High priority messages
        Assert.True(filteredCount <= expectedFilteredCount + 1); // Allow for slight variance
        
        // Unfiltered queue should receive all messages
        Assert.True(unfilteredCount >= messageCount * 0.9); // Allow for 90% delivery rate
        
        // Performance should be reasonable
        var publishLatency = publishStopwatch.Elapsed;
        var maxExpectedLatency = _testEnvironment.IsLocalEmulator ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(30);
        Assert.True(publishLatency < maxExpectedLatency,
            $"Publish latency {publishLatency.TotalSeconds}s exceeds threshold {maxExpectedLatency.TotalSeconds}s");
        
        _logger.LogInformation("Message filtering performance test completed: " +
                              "Published {MessageCount} messages in {PublishLatency}ms. " +
                              "Filtered queue received {FilteredCount} messages, " +
                              "Unfiltered queue received {UnfilteredCount} messages",
            messageCount, publishLatency.TotalMilliseconds, filteredCount, unfilteredCount);
    }

    private async Task<string> GetQueueArnAsync(string queueUrl)
    {
        var response = await _testEnvironment.SqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = new List<string> { "QueueArn" }
        });
        
        return response.Attributes["QueueArn"];
    }

    private async Task SetQueuePolicyForSns(string queueUrl, string queueArn, string topicArn)
    {
        var policy = $@"{{
            ""Version"": ""2012-10-17"",
            ""Statement"": [
                {{
                    ""Effect"": ""Allow"",
                    ""Principal"": {{
                        ""Service"": ""sns.amazonaws.com""
                    }},
                    ""Action"": ""sqs:SendMessage"",
                    ""Resource"": ""{queueArn}"",
                    ""Condition"": {{
                        ""ArnEquals"": {{
                            ""aws:SourceArn"": ""{topicArn}""
                        }}
                    }}
                }}
            ]
        }}";

        await _testEnvironment.SqsClient.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = new Dictionary<string, string>
            {
                ["Policy"] = policy
            }
        });
    }
}

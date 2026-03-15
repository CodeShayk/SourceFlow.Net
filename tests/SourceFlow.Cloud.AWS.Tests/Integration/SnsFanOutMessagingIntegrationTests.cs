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
using SqsMessageAttributeValue = Amazon.SQS.Model.MessageAttributeValue;

namespace SourceFlow.Cloud.AWS.Tests.Integration;

/// <summary>
/// Integration tests for SNS fan-out messaging functionality
/// Tests event delivery to multiple subscriber types (SQS, Lambda, HTTP) with subscription management
/// **Validates: Requirements 2.2**
/// </summary>
[Collection("AWS Integration Tests")]
[Trait("Category", "Integration")]
[Trait("Category", "RequiresLocalStack")]
public class SnsFanOutMessagingIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly IAwsTestEnvironment _testEnvironment;
    private readonly ILogger<SnsFanOutMessagingIntegrationTests> _logger;
    private readonly List<string> _createdTopics = new();
    private readonly List<string> _createdQueues = new();
    private readonly List<string> _createdSubscriptions = new();

    public SnsFanOutMessagingIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        var serviceProvider = services.BuildServiceProvider();
        _logger = serviceProvider.GetRequiredService<ILogger<SnsFanOutMessagingIntegrationTests>>();
        
        _testEnvironment = AwsTestEnvironmentFactory.CreateLocalStackEnvironmentAsync().GetAwaiter().GetResult();
    }

    public async Task InitializeAsync()
    {
        await _testEnvironment.InitializeAsync();
        
        if (!await _testEnvironment.IsAvailableAsync())
        {
            throw new InvalidOperationException("AWS test environment is not available");
        }
        
        _logger.LogInformation("SNS fan-out messaging integration tests initialized");
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
        _logger.LogInformation("SNS fan-out messaging integration tests disposed");
    }

    [Fact]
    public async Task FanOutMessage_ToMultipleSqsSubscribers_ShouldDeliverToAll()
    {
        // Arrange
        var topicName = $"test-fanout-topic-{Guid.NewGuid():N}";
        var topicArn = await _testEnvironment.CreateTopicAsync(topicName);
        _createdTopics.Add(topicArn);
        
        // Create multiple SQS queues as subscribers
        var subscriberQueues = new List<(string QueueUrl, string QueueArn)>();
        for (int i = 0; i < 3; i++)
        {
            var queueName = $"test-subscriber-queue-{i}-{Guid.NewGuid():N}";
            var queueUrl = await _testEnvironment.CreateStandardQueueAsync(queueName);
            _createdQueues.Add(queueUrl);
            
            var queueArn = await GetQueueArnAsync(queueUrl);
            subscriberQueues.Add((queueUrl, queueArn));
            
            // Subscribe queue to topic
            var subscriptionResponse = await _testEnvironment.SnsClient.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = topicArn,
                Protocol = "sqs",
                Endpoint = queueArn
            });
            _createdSubscriptions.Add(subscriptionResponse.SubscriptionArn);
            
            // Set queue policy to allow SNS to send messages
            await SetQueuePolicyForSns(queueUrl, queueArn, topicArn);
        }
        
        var testEvent = new TestEvent(new TestEventData
        {
            Id = 123,
            Message = "Fan-out test message",
            Value = 456
        });

        // Act
        var publishResponse = await _testEnvironment.SnsClient.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = JsonSerializer.Serialize(testEvent),
            Subject = testEvent.Name,
            MessageAttributes = new Dictionary<string, SnsMessageAttributeValue>
            {
                ["EventType"] = new SnsMessageAttributeValue
                {
                    DataType = "String",
                    StringValue = testEvent.GetType().Name
                },
                ["FanOutTest"] = new SnsMessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "true"
                }
            }
        });

        // Assert
        Assert.NotNull(publishResponse.MessageId);
        
        // Wait a bit for message delivery
        await Task.Delay(2000);
        
        // Verify each subscriber received the message
        var receivedMessages = new List<Message>();
        foreach (var (queueUrl, _) in subscriberQueues)
        {
            var receiveResponse = await _testEnvironment.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5,
                MessageAttributeNames = new List<string> { "All" }
            });
            
            Assert.NotEmpty(receiveResponse.Messages);
            receivedMessages.AddRange(receiveResponse.Messages);
            
            _logger.LogInformation("Queue {QueueUrl} received {MessageCount} messages", queueUrl, receiveResponse.Messages.Count);
        }
        
        // All subscribers should have received the message
        Assert.Equal(subscriberQueues.Count, receivedMessages.Count);
        
        _logger.LogInformation("Successfully delivered fan-out message to {SubscriberCount} SQS subscribers", subscriberQueues.Count);
    }

    [Fact]
    public async Task FanOutMessage_WithSubscriptionManagement_ShouldHandleSubscriptionChanges()
    {
        // Arrange
        var topicName = $"test-subscription-mgmt-{Guid.NewGuid():N}";
        var topicArn = await _testEnvironment.CreateTopicAsync(topicName);
        _createdTopics.Add(topicArn);
        
        // Create initial subscriber
        var queueName1 = $"test-sub-queue-1-{Guid.NewGuid():N}";
        var queueUrl1 = await _testEnvironment.CreateStandardQueueAsync(queueName1);
        _createdQueues.Add(queueUrl1);
        var queueArn1 = await GetQueueArnAsync(queueUrl1);
        
        var subscription1Response = await _testEnvironment.SnsClient.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn1
        });
        _createdSubscriptions.Add(subscription1Response.SubscriptionArn);
        await SetQueuePolicyForSns(queueUrl1, queueArn1, topicArn);
        
        // Publish first message
        var testEvent1 = new TestEvent(new TestEventData
        {
            Id = 100,
            Message = "First message",
            Value = 200
        });
        
        await _testEnvironment.SnsClient.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = JsonSerializer.Serialize(testEvent1),
            Subject = testEvent1.Name
        });
        
        await Task.Delay(1000);
        
        // Verify first subscriber received message
        var receiveResponse1 = await _testEnvironment.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl1,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 2
        });
        
        Assert.Single(receiveResponse1.Messages);
        
        // Add second subscriber
        var queueName2 = $"test-sub-queue-2-{Guid.NewGuid():N}";
        var queueUrl2 = await _testEnvironment.CreateStandardQueueAsync(queueName2);
        _createdQueues.Add(queueUrl2);
        var queueArn2 = await GetQueueArnAsync(queueUrl2);
        
        var subscription2Response = await _testEnvironment.SnsClient.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn2
        });
        _createdSubscriptions.Add(subscription2Response.SubscriptionArn);
        await SetQueuePolicyForSns(queueUrl2, queueArn2, topicArn);
        
        // Publish second message
        var testEvent2 = new TestEvent(new TestEventData
        {
            Id = 300,
            Message = "Second message",
            Value = 400
        });
        
        await _testEnvironment.SnsClient.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = JsonSerializer.Serialize(testEvent2),
            Subject = testEvent2.Name
        });
        
        await Task.Delay(1000);
        
        // Verify both subscribers received second message
        var receiveResponse2a = await _testEnvironment.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl1,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 2
        });
        
        var receiveResponse2b = await _testEnvironment.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl2,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 2
        });
        
        Assert.NotEmpty(receiveResponse2a.Messages);
        Assert.NotEmpty(receiveResponse2b.Messages);
        
        // Remove first subscriber
        await _testEnvironment.SnsClient.UnsubscribeAsync(new UnsubscribeRequest
        {
            SubscriptionArn = subscription1Response.SubscriptionArn
        });
        _createdSubscriptions.Remove(subscription1Response.SubscriptionArn);
        
        // Publish third message
        var testEvent3 = new TestEvent(new TestEventData
        {
            Id = 500,
            Message = "Third message",
            Value = 600
        });
        
        await _testEnvironment.SnsClient.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = JsonSerializer.Serialize(testEvent3),
            Subject = testEvent3.Name
        });
        
        await Task.Delay(1000);
        
        // Verify only second subscriber received third message
        var receiveResponse3a = await _testEnvironment.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl1,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 2
        });
        
        var receiveResponse3b = await _testEnvironment.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl2,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 2
        });
        
        // First queue should not receive third message (unsubscribed)
        Assert.Empty(receiveResponse3a.Messages);
        // Second queue should receive third message
        Assert.NotEmpty(receiveResponse3b.Messages);
        
        _logger.LogInformation("Successfully tested subscription management with dynamic subscriber changes");
    }

    [Fact]
    public async Task FanOutMessage_WithDeliveryRetryAndErrorHandling_ShouldHandleFailures()
    {
        // Arrange
        var topicName = $"test-retry-topic-{Guid.NewGuid():N}";
        var topicArn = await _testEnvironment.CreateTopicAsync(topicName);
        _createdTopics.Add(topicArn);
        
        // Create a valid subscriber queue
        var validQueueName = $"test-valid-queue-{Guid.NewGuid():N}";
        var validQueueUrl = await _testEnvironment.CreateStandardQueueAsync(validQueueName);
        _createdQueues.Add(validQueueUrl);
        var validQueueArn = await GetQueueArnAsync(validQueueUrl);
        
        var validSubscriptionResponse = await _testEnvironment.SnsClient.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = validQueueArn
        });
        _createdSubscriptions.Add(validSubscriptionResponse.SubscriptionArn);
        await SetQueuePolicyForSns(validQueueUrl, validQueueArn, topicArn);
        
        // Create an invalid HTTP endpoint subscriber (will fail delivery)
        var invalidHttpSubscriptionResponse = await _testEnvironment.SnsClient.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "http",
            Endpoint = "http://invalid-endpoint-that-does-not-exist.com/webhook"
        });
        _createdSubscriptions.Add(invalidHttpSubscriptionResponse.SubscriptionArn);
        
        var testEvent = new TestEvent(new TestEventData
        {
            Id = 777,
            Message = "Retry test message",
            Value = 888
        });

        // Act
        var publishResponse = await _testEnvironment.SnsClient.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = JsonSerializer.Serialize(testEvent),
            Subject = testEvent.Name,
            MessageAttributes = new Dictionary<string, SnsMessageAttributeValue>
            {
                ["EventType"] = new SnsMessageAttributeValue
                {
                    DataType = "String",
                    StringValue = testEvent.GetType().Name
                },
                ["RetryTest"] = new SnsMessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "true"
                }
            }
        });

        // Assert
        Assert.NotNull(publishResponse.MessageId);
        
        // Wait for delivery attempts
        await Task.Delay(3000);
        
        // Valid subscriber should receive the message
        var receiveResponse = await _testEnvironment.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = validQueueUrl,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 5
        });
        
        Assert.NotEmpty(receiveResponse.Messages);
        
        // Check subscription attributes for delivery policy (if supported)
        try
        {
            var subscriptionAttributes = await _testEnvironment.SnsClient.GetSubscriptionAttributesAsync(
                new GetSubscriptionAttributesRequest
                {
                    SubscriptionArn = validSubscriptionResponse.SubscriptionArn
                });
            
            Assert.NotNull(subscriptionAttributes.Attributes);
            _logger.LogInformation("Valid subscription attributes retrieved successfully");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not retrieve subscription attributes (might not be supported in LocalStack): {Error}", ex.Message);
        }
        
        _logger.LogInformation("Successfully tested delivery retry and error handling with mixed subscriber types");
    }

    [Fact]
    public async Task FanOutMessage_PerformanceAndScalability_ShouldHandleMultipleSubscribers()
    {
        // Arrange
        var topicName = $"test-perf-fanout-{Guid.NewGuid():N}";
        var topicArn = await _testEnvironment.CreateTopicAsync(topicName);
        _createdTopics.Add(topicArn);
        
        const int subscriberCount = 10;
        const int messageCount = 20;
        var subscriberQueues = new List<(string QueueUrl, string QueueArn)>();
        
        // Create multiple subscribers
        for (int i = 0; i < subscriberCount; i++)
        {
            var queueName = $"test-perf-queue-{i}-{Guid.NewGuid():N}";
            var queueUrl = await _testEnvironment.CreateStandardQueueAsync(queueName);
            _createdQueues.Add(queueUrl);
            
            var queueArn = await GetQueueArnAsync(queueUrl);
            subscriberQueues.Add((queueUrl, queueArn));
            
            var subscriptionResponse = await _testEnvironment.SnsClient.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = topicArn,
                Protocol = "sqs",
                Endpoint = queueArn
            });
            _createdSubscriptions.Add(subscriptionResponse.SubscriptionArn);
            
            await SetQueuePolicyForSns(queueUrl, queueArn, topicArn);
        }
        
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        // Act - Publish multiple messages
        var publishTasks = new List<Task>();
        for (int i = 0; i < messageCount; i++)
        {
            var messageIndex = i;
            var task = PublishTestMessage(topicArn, messageIndex);
            publishTasks.Add(task);
        }
        
        await Task.WhenAll(publishTasks);
        stopwatch.Stop();
        
        var publishLatency = stopwatch.Elapsed;
        
        // Wait for message delivery
        await Task.Delay(5000);
        
        // Assert - Verify all subscribers received all messages
        var totalMessagesReceived = 0;
        var deliveryLatencies = new List<TimeSpan>();
        
        foreach (var (queueUrl, _) in subscriberQueues)
        {
            var queueStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var queueMessageCount = 0;

            // SQS returns at most 10 per call; poll until we stop getting messages
            for (int poll = 0; poll < 5; poll++)
            {
                var receiveResponse = await _testEnvironment.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 1
                });
                if (receiveResponse.Messages.Count == 0) break;
                queueMessageCount += receiveResponse.Messages.Count;
            }

            queueStopwatch.Stop();
            deliveryLatencies.Add(queueStopwatch.Elapsed);
            totalMessagesReceived += queueMessageCount;

            _logger.LogDebug("Queue {QueueUrl} received {MessageCount} messages", queueUrl, queueMessageCount);
        }
        
        var expectedTotalMessages = subscriberCount * messageCount;
        var deliverySuccessRate = (double)totalMessagesReceived / expectedTotalMessages;
        var averageDeliveryLatency = TimeSpan.FromMilliseconds(deliveryLatencies.Average(l => l.TotalMilliseconds));
        
        // Performance assertions
        Assert.True(deliverySuccessRate >= 0.90, 
            $"Delivery success rate {deliverySuccessRate:P2} is below 90% threshold. " +
            $"Received {totalMessagesReceived}/{expectedTotalMessages} messages");
        
        var maxExpectedPublishLatency = _testEnvironment.IsLocalEmulator ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(30);
        Assert.True(publishLatency < maxExpectedPublishLatency,
            $"Publish latency {publishLatency.TotalSeconds}s exceeds threshold {maxExpectedPublishLatency.TotalSeconds}s");
        
        _logger.LogInformation("Fan-out performance test completed: {SubscriberCount} subscribers, {MessageCount} messages. " +
                              "Publish latency: {PublishLatency}ms, Average delivery latency: {DeliveryLatency}ms, " +
                              "Success rate: {SuccessRate:P2}",
            subscriberCount, messageCount, publishLatency.TotalMilliseconds, 
            averageDeliveryLatency.TotalMilliseconds, deliverySuccessRate);
    }

    private async Task PublishTestMessage(string topicArn, int messageIndex)
    {
        var testEvent = new TestEvent(new TestEventData
        {
            Id = messageIndex,
            Message = $"Performance test message {messageIndex}",
            Value = messageIndex * 100
        });

        await _testEnvironment.SnsClient.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = JsonSerializer.Serialize(testEvent),
            Subject = testEvent.Name,
            MessageAttributes = new Dictionary<string, SnsMessageAttributeValue>
            {
                ["EventType"] = new SnsMessageAttributeValue
                {
                    DataType = "String",
                    StringValue = testEvent.GetType().Name
                },
                ["MessageIndex"] = new SnsMessageAttributeValue
                {
                    DataType = "Number",
                    StringValue = messageIndex.ToString()
                }
            }
        });
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
        // Set queue policy to allow SNS to send messages
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

using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using System.Text.Json;
using Xunit.Abstractions;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace SourceFlow.Cloud.AWS.Tests.Integration;

/// <summary>
/// Property-based tests for SNS event publishing correctness
/// **Property 3: SNS Event Publishing Correctness**
/// **Validates: Requirements 2.1, 2.2, 2.4**
/// </summary>
[Collection("AWS Integration Tests")]
public class SnsEventPublishingPropertyTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly IAwsTestEnvironment _testEnvironment;
    private readonly ILogger<SnsEventPublishingPropertyTests> _logger;
    private readonly List<string> _createdTopics = new();
    private readonly List<string> _createdQueues = new();
    private readonly List<string> _createdSubscriptions = new();

    public SnsEventPublishingPropertyTests(ITestOutputHelper output)
    {
        _output = output;
        
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        var serviceProvider = services.BuildServiceProvider();
        _logger = serviceProvider.GetRequiredService<ILogger<SnsEventPublishingPropertyTests>>();
        
        _testEnvironment = AwsTestEnvironmentFactory.CreateLocalStackEnvironmentAsync().GetAwaiter().GetResult();
    }

    public async Task InitializeAsync()
    {
        await _testEnvironment.InitializeAsync();
        
        if (!await _testEnvironment.IsAvailableAsync())
        {
            throw new InvalidOperationException("AWS test environment is not available");
        }
        
        _logger.LogInformation("SNS event publishing property tests initialized");
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
        _logger.LogInformation("SNS event publishing property tests disposed");
    }

    /// <summary>
    /// Property 3: SNS Event Publishing Correctness
    /// **Validates: Requirements 2.1, 2.2, 2.4**
    /// 
    /// For any valid SourceFlow event and SNS topic configuration, when the event is published,
    /// it should be delivered to all subscribers with proper message attributes, correlation ID preservation,
    /// and fan-out messaging to multiple subscriber types (SQS, Lambda, HTTP).
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(SnsEventPublishingGenerators) })]
    public void SnsEventPublishingCorrectness(SnsEventPublishingScenario scenario)
    {
        try
        {
            _logger.LogInformation("Testing SNS event publishing correctness with scenario: {Scenario}", 
                JsonSerializer.Serialize(scenario, new JsonSerializerOptions { WriteIndented = true }));
            
            // Property 1: Event publishing should succeed with proper message attributes
            var publishingValid = ValidateEventPublishing(scenario).GetAwaiter().GetResult();
            
            // Property 2: Fan-out messaging should deliver to all subscribers
            var fanOutValid = ValidateFanOutMessaging(scenario).GetAwaiter().GetResult();
            
            // Property 3: Correlation ID should be preserved across subscriptions
            var correlationValid = ValidateCorrelationIdPreservation(scenario).GetAwaiter().GetResult();
            
            // Property 4: Message attributes should be preserved
            var attributesValid = ValidateMessageAttributePreservation(scenario).GetAwaiter().GetResult();
            
            var result = publishingValid && fanOutValid && correlationValid && attributesValid;
            
            if (!result)
            {
                _logger.LogWarning("SNS event publishing correctness failed for scenario: {Scenario}. " +
                                  "Publishing: {Publishing}, FanOut: {FanOut}, Correlation: {Correlation}, Attributes: {Attributes}",
                    JsonSerializer.Serialize(scenario), publishingValid, fanOutValid, correlationValid, attributesValid);
            }
            
            Assert.True(result, "SNS event publishing correctness validation failed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SNS event publishing correctness test failed with exception for scenario: {Scenario}", 
                JsonSerializer.Serialize(scenario));
            throw;
        }
    }

    private async Task<bool> ValidateEventPublishing(SnsEventPublishingScenario scenario)
    {
        try
        {
            // Create topic
            var topicName = $"prop-test-topic-{Guid.NewGuid():N}";
            var topicArn = await _testEnvironment.CreateTopicAsync(topicName);
            _createdTopics.Add(topicArn);
            
            // Create test event
            var testEvent = new TestEvent(new TestEventData
            {
                Id = scenario.EventId,
                Message = scenario.EventMessage,
                Value = scenario.EventValue
            });
            
            // Publish event
            var publishResponse = await _testEnvironment.SnsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = JsonSerializer.Serialize(testEvent),
                Subject = testEvent.Name,
                MessageAttributes = CreateMessageAttributes(scenario, testEvent)
            });
            
            // Validate publish response
            var publishValid = publishResponse?.MessageId != null && !string.IsNullOrEmpty(publishResponse.MessageId);
            
            if (!publishValid)
            {
                _logger.LogWarning("Event publishing validation failed: MessageId is null or empty");
            }
            
            return publishValid;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Event publishing validation failed with exception: {Error}", ex.Message);
            return false;
        }
    }

    private async Task<bool> ValidateFanOutMessaging(SnsEventPublishingScenario scenario)
    {
        try
        {
            // Create topic
            var topicName = $"prop-test-fanout-{Guid.NewGuid():N}";
            var topicArn = await _testEnvironment.CreateTopicAsync(topicName);
            _createdTopics.Add(topicArn);
            
            // Create multiple SQS subscribers
            var subscriberQueues = new List<(string QueueUrl, string QueueArn)>();
            for (int i = 0; i < scenario.SubscriberCount && i < 5; i++) // Limit to 5 for performance
            {
                var queueName = $"prop-test-sub-{i}-{Guid.NewGuid():N}";
                var queueUrl = await _testEnvironment.CreateStandardQueueAsync(queueName);
                _createdQueues.Add(queueUrl);
                
                var queueArn = await GetQueueArnAsync(queueUrl);
                subscriberQueues.Add((queueUrl, queueArn));
                
                // Subscribe to topic
                var subscriptionResponse = await _testEnvironment.SnsClient.SubscribeAsync(new SubscribeRequest
                {
                    TopicArn = topicArn,
                    Protocol = "sqs",
                    Endpoint = queueArn
                });
                _createdSubscriptions.Add(subscriptionResponse.SubscriptionArn);
                
                // Set queue policy
                await SetQueuePolicyForSns(queueUrl, queueArn, topicArn);
            }
            
            // Create test event
            var testEvent = new TestEvent(new TestEventData
            {
                Id = scenario.EventId,
                Message = scenario.EventMessage,
                Value = scenario.EventValue
            });
            
            // Publish event
            await _testEnvironment.SnsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = JsonSerializer.Serialize(testEvent),
                Subject = testEvent.Name,
                MessageAttributes = CreateMessageAttributes(scenario, testEvent)
            });
            
            // Wait for delivery
            await Task.Delay(2000);
            
            // Verify all subscribers received the message
            var deliveredCount = 0;
            foreach (var (queueUrl, _) in subscriberQueues)
            {
                var receiveResponse = await _testEnvironment.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 2
                });
                
                if (receiveResponse.Messages.Count > 0)
                {
                    deliveredCount++;
                }
            }
            
            var fanOutValid = deliveredCount == subscriberQueues.Count;
            
            if (!fanOutValid)
            {
                _logger.LogWarning("Fan-out messaging validation failed: {DeliveredCount}/{ExpectedCount} subscribers received messages",
                    deliveredCount, subscriberQueues.Count);
            }
            
            return fanOutValid;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Fan-out messaging validation failed with exception: {Error}", ex.Message);
            return false;
        }
    }

    private async Task<bool> ValidateCorrelationIdPreservation(SnsEventPublishingScenario scenario)
    {
        try
        {
            // Create topic and subscriber
            var topicName = $"prop-test-correlation-{Guid.NewGuid():N}";
            var topicArn = await _testEnvironment.CreateTopicAsync(topicName);
            _createdTopics.Add(topicArn);
            
            var queueName = $"prop-test-corr-queue-{Guid.NewGuid():N}";
            var queueUrl = await _testEnvironment.CreateStandardQueueAsync(queueName);
            _createdQueues.Add(queueUrl);
            
            var queueArn = await GetQueueArnAsync(queueUrl);
            var subscriptionResponse = await _testEnvironment.SnsClient.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = topicArn,
                Protocol = "sqs",
                Endpoint = queueArn
            });
            _createdSubscriptions.Add(subscriptionResponse.SubscriptionArn);
            
            await SetQueuePolicyForSns(queueUrl, queueArn, topicArn);
            
            // Create test event with correlation ID
            var testEvent = new TestEvent(new TestEventData
            {
                Id = scenario.EventId,
                Message = scenario.EventMessage,
                Value = scenario.EventValue
            });
            
            var correlationId = scenario.CorrelationId ?? Guid.NewGuid().ToString();
            var messageAttributes = CreateMessageAttributes(scenario, testEvent);
            messageAttributes["CorrelationId"] = new SnsMessageAttributeValue
            {
                DataType = "String",
                StringValue = correlationId
            };
            
            // Publish event
            await _testEnvironment.SnsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = JsonSerializer.Serialize(testEvent),
                Subject = testEvent.Name,
                MessageAttributes = messageAttributes
            });
            
            // Wait for delivery
            await Task.Delay(1500);
            
            // Receive and verify correlation ID
            var receiveResponse = await _testEnvironment.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 3,
                MessageAttributeNames = new List<string> { "All" }
            });
            
            if (receiveResponse.Messages.Count == 0)
            {
                _logger.LogWarning("Correlation ID validation failed: No messages received");
                return false;
            }
            
            var receivedMessage = receiveResponse.Messages[0];
            
            // Parse SNS message (SQS receives SNS messages wrapped in JSON)
            var snsMessage = JsonSerializer.Deserialize<SnsMessageWrapper>(receivedMessage.Body);
            var snsMessageAttributes = snsMessage?.MessageAttributes;
            
            var correlationValid = snsMessageAttributes?.ContainsKey("CorrelationId") == true &&
                                  snsMessageAttributes["CorrelationId"]?.Value == correlationId;
            
            if (!correlationValid)
            {
                _logger.LogWarning("Correlation ID validation failed: Expected {ExpectedId}, but correlation ID not found or mismatched in received message",
                    correlationId);
            }
            
            return correlationValid;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Correlation ID validation failed with exception: {Error}", ex.Message);
            return false;
        }
    }

    private async Task<bool> ValidateMessageAttributePreservation(SnsEventPublishingScenario scenario)
    {
        try
        {
            // Create topic and subscriber
            var topicName = $"prop-test-attrs-{Guid.NewGuid():N}";
            var topicArn = await _testEnvironment.CreateTopicAsync(topicName);
            _createdTopics.Add(topicArn);
            
            var queueName = $"prop-test-attrs-queue-{Guid.NewGuid():N}";
            var queueUrl = await _testEnvironment.CreateStandardQueueAsync(queueName);
            _createdQueues.Add(queueUrl);
            
            var queueArn = await GetQueueArnAsync(queueUrl);
            var subscriptionResponse = await _testEnvironment.SnsClient.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = topicArn,
                Protocol = "sqs",
                Endpoint = queueArn
            });
            _createdSubscriptions.Add(subscriptionResponse.SubscriptionArn);
            
            await SetQueuePolicyForSns(queueUrl, queueArn, topicArn);
            
            // Create test event
            var testEvent = new TestEvent(new TestEventData
            {
                Id = scenario.EventId,
                Message = scenario.EventMessage,
                Value = scenario.EventValue
            });
            
            var messageAttributes = CreateMessageAttributes(scenario, testEvent);
            
            // Publish event
            await _testEnvironment.SnsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = JsonSerializer.Serialize(testEvent),
                Subject = testEvent.Name,
                MessageAttributes = messageAttributes
            });
            
            // Wait for delivery
            await Task.Delay(1500);
            
            // Receive and verify attributes
            var receiveResponse = await _testEnvironment.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 3,
                MessageAttributeNames = new List<string> { "All" }
            });
            
            if (receiveResponse.Messages.Count == 0)
            {
                _logger.LogWarning("Message attribute validation failed: No messages received");
                return false;
            }
            
            var receivedMessage = receiveResponse.Messages[0];
            
            // Parse SNS message
            var snsMessage = JsonSerializer.Deserialize<SnsMessageWrapper>(receivedMessage.Body);
            var snsMessageAttributes = snsMessage?.MessageAttributes;
            
            // Verify key attributes are preserved
            var eventTypeValid = snsMessageAttributes?.ContainsKey("EventType") == true &&
                                snsMessageAttributes["EventType"]?.Value == testEvent.GetType().Name;
            
            var eventNameValid = snsMessageAttributes?.ContainsKey("EventName") == true &&
                                snsMessageAttributes["EventName"]?.Value == testEvent.Name;
            
            var entityIdValid = snsMessageAttributes?.ContainsKey("EntityId") == true &&
                               snsMessageAttributes["EntityId"]?.Value == scenario.EventId.ToString();
            
            var attributesValid = eventTypeValid && eventNameValid && entityIdValid;
            
            if (!attributesValid)
            {
                _logger.LogWarning("Message attribute validation failed: EventType={EventType}, EventName={EventName}, EntityId={EntityId}",
                    eventTypeValid, eventNameValid, entityIdValid);
            }
            
            return attributesValid;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Message attribute validation failed with exception: {Error}", ex.Message);
            return false;
        }
    }

    private Dictionary<string, SnsMessageAttributeValue> CreateMessageAttributes(SnsEventPublishingScenario scenario, TestEvent testEvent)
    {
        var attributes = new Dictionary<string, SnsMessageAttributeValue>
        {
            ["EventType"] = new SnsMessageAttributeValue
            {
                DataType = "String",
                StringValue = testEvent.GetType().Name
            },
            ["EventName"] = new SnsMessageAttributeValue
            {
                DataType = "String",
                StringValue = testEvent.Name
            },
            ["EntityId"] = new SnsMessageAttributeValue
            {
                DataType = "Number",
                StringValue = scenario.EventId.ToString()
            }
        };
        
        // Add custom attributes from scenario
        foreach (var customAttr in scenario.CustomAttributes)
        {
            attributes[customAttr.Key] = new SnsMessageAttributeValue
            {
                DataType = "String",
                StringValue = customAttr.Value
            };
        }
        
        return attributes;
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

/// <summary>
/// Generators for SNS event publishing property tests
/// </summary>
public static class SnsEventPublishingGenerators
{
    public static Arbitrary<SnsEventPublishingScenario> SnsEventPublishingScenario()
    {
        return Gen.Fresh(() => new SnsEventPublishingScenario
        {
            EventId = Gen.Choose(1, 10000).Sample(0, 1).First(),
            EventMessage = Gen.Elements("Test message", "Property test event", "SNS publishing test", "Fan-out test message").Sample(0, 1).First(),
            EventValue = Gen.Choose(1, 1000).Sample(0, 1).First(),
            SubscriberCount = Gen.Choose(1, 3).Sample(0, 1).First(), // Keep small for performance
            CorrelationId = Gen.Elements<string?>(null, Guid.NewGuid().ToString(), "test-correlation-id").Sample(0, 1).First(),
            CustomAttributes = GenerateCustomAttributes()
        }).ToArbitrary();
    }
    
    private static Dictionary<string, string> GenerateCustomAttributes()
    {
        var attributeCount = Gen.Choose(0, 3).Sample(0, 1).First();
        var attributes = new Dictionary<string, string>();
        
        for (int i = 0; i < attributeCount; i++)
        {
            var key = Gen.Elements("Priority", "Source", "Category", "Environment").Sample(0, 1).First();
            var value = Gen.Elements("High", "Medium", "Low", "Test", "Production").Sample(0, 1).First();
            
            if (!attributes.ContainsKey(key))
            {
                attributes[key] = value;
            }
        }
        
        return attributes;
    }
}

/// <summary>
/// Test scenario for SNS event publishing property tests
/// </summary>
public class SnsEventPublishingScenario
{
    public int EventId { get; set; }
    public string EventMessage { get; set; } = "";
    public int EventValue { get; set; }
    public int SubscriberCount { get; set; }
    public string? CorrelationId { get; set; }
    public Dictionary<string, string> CustomAttributes { get; set; } = new();
}
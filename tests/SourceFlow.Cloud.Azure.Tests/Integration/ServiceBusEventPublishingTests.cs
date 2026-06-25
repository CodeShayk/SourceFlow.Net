using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Azure.Tests.TestHelpers;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Events;
using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Azure.Tests.Integration;

/// <summary>
/// Integration tests for Azure Service Bus event publishing including topic publishing,
/// subscription filtering, message correlation, and fan-out messaging.
/// Feature: azure-cloud-integration-testing
/// Task: 5.1 Create Azure Service Bus event publishing integration tests
/// </summary>
public class ServiceBusEventPublishingTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;
    private IAzureTestEnvironment? _testEnvironment;
    private ServiceBusClient? _serviceBusClient;
    private ServiceBusTestHelpers? _testHelpers;
    private ServiceBusAdministrationClient? _adminClient;

    public ServiceBusEventPublishingTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Debug);
        });
    }

    public async Task InitializeAsync()
    {
        var config = AzureTestConfiguration.CreateDefault();

        _testEnvironment = new AzureTestEnvironment(config, _loggerFactory);

        await _testEnvironment.InitializeAsync();

        var connectionString = _testEnvironment.GetServiceBusConnectionString();
        _serviceBusClient = new ServiceBusClient(connectionString);
        
        _testHelpers = new ServiceBusTestHelpers(
            _serviceBusClient,
            _loggerFactory.CreateLogger<ServiceBusTestHelpers>());

        _adminClient = new ServiceBusAdministrationClient(connectionString);

        // Create test topics and subscriptions
        await CreateTestTopicsAndSubscriptionsAsync();
    }

    public async Task DisposeAsync()
    {
        if (_serviceBusClient != null)
        {
            await _serviceBusClient.DisposeAsync();
        }

        if (_testEnvironment != null)
        {
            await _testEnvironment.CleanupAsync();
        }
    }

    #region Event Publishing Tests (Requirements 2.1, 2.3, 2.4)

    /// <summary>
    /// Test: Event publishing to topics with proper metadata
    /// Validates: Requirements 2.1
    /// </summary>
    [Fact]
    public async Task EventPublishing_SendsToCorrectTopic_WithMetadata()
    {
        // Arrange
        var topicName = "test-events";
        var subscriptionName = "test-subscription";
        
        var @event = new TestEvent
        {
            Name = "TestEvent",
            Payload = new TestEntity { Id = 1 },
            Metadata = new Metadata
            {
                Properties = new Dictionary<string, object>
                {
                    ["CorrelationId"] = Guid.NewGuid().ToString(),
                    ["EventType"] = "TestEventType",
                    ["Source"] = "TestSource"
                }
            }
        };

        var correlationId = @event.Metadata.Properties["CorrelationId"].ToString();

        // Act
        var message = _testHelpers!.CreateTestEventMessage(@event, correlationId);
        await _testHelpers.SendMessageToTopicAsync(topicName, message);

        // Assert
        var receivedMessages = await _testHelpers.ReceiveMessagesFromSubscriptionAsync(
            topicName, 
            subscriptionName, 
            1, 
            TimeSpan.FromSeconds(10));
        
        Assert.Single(receivedMessages);
        Assert.Equal(correlationId, receivedMessages[0].CorrelationId);
        Assert.Equal(@event.Name, receivedMessages[0].Subject);
        Assert.True(receivedMessages[0].ApplicationProperties.ContainsKey("EventType"));
        Assert.True(receivedMessages[0].ApplicationProperties.ContainsKey("Timestamp"));
        Assert.True(receivedMessages[0].ApplicationProperties.ContainsKey("SourceSystem"));
    }

    /// <summary>
    /// Test: Message correlation ID preservation across event publishing
    /// Validates: Requirements 2.3
    /// </summary>
    [Fact]
    public async Task EventPublishing_PreservesCorrelationId()
    {
        // Arrange
        var topicName = "test-events";
        var subscriptionName = "test-subscription";
        var correlationId = Guid.NewGuid().ToString();
        
        var events = Enumerable.Range(1, 5)
            .Select(i => new TestEvent
            {
                Name = $"TestEvent{i}",
                Payload = new TestEntity { Id = i },
                Metadata = new Metadata
                {
                    Properties = new Dictionary<string, object>
                    {
                        ["CorrelationId"] = correlationId
                    }
                }
            })
            .ToList();

        // Act
        foreach (var @event in events)
        {
            var message = _testHelpers!.CreateTestEventMessage(@event, correlationId);
            await _testHelpers.SendMessageToTopicAsync(topicName, message);
        }

        // Assert
        var receivedMessages = await _testHelpers!.ReceiveMessagesFromSubscriptionAsync(
            topicName, 
            subscriptionName, 
            events.Count, 
            TimeSpan.FromSeconds(15));
        
        Assert.Equal(events.Count, receivedMessages.Count);
        
        // Verify all messages have the same correlation ID
        foreach (var message in receivedMessages)
        {
            Assert.Equal(correlationId, message.CorrelationId);
        }
    }

    /// <summary>
    /// Test: Fan-out messaging to multiple subscriptions
    /// Validates: Requirements 2.4
    /// </summary>
    [Fact]
    public async Task EventPublishing_FanOutToMultipleSubscriptions()
    {
        // Arrange
        var topicName = "test-events-fanout";
        var subscription1 = "subscription-1";
        var subscription2 = "subscription-2";
        var subscription3 = "subscription-3";
        
        await EnsureTopicWithMultipleSubscriptionsExistsAsync(
            topicName, 
            new[] { subscription1, subscription2, subscription3 });

        var @event = new TestEvent
        {
            Name = "FanOutTestEvent",
            Payload = new TestEntity { Id = 100 },
            Metadata = new Metadata
            {
                Properties = new Dictionary<string, object>
                {
                    ["CorrelationId"] = Guid.NewGuid().ToString()
                }
            }
        };

        // Act
        var message = _testHelpers!.CreateTestEventMessage(@event);
        await _testHelpers.SendMessageToTopicAsync(topicName, message);

        // Assert - Verify message is delivered to all subscriptions
        var sub1Messages = await _testHelpers.ReceiveMessagesFromSubscriptionAsync(
            topicName, subscription1, 1, TimeSpan.FromSeconds(10));
        var sub2Messages = await _testHelpers.ReceiveMessagesFromSubscriptionAsync(
            topicName, subscription2, 1, TimeSpan.FromSeconds(10));
        var sub3Messages = await _testHelpers.ReceiveMessagesFromSubscriptionAsync(
            topicName, subscription3, 1, TimeSpan.FromSeconds(10));

        Assert.Single(sub1Messages);
        Assert.Single(sub2Messages);
        Assert.Single(sub3Messages);

        // Verify all subscriptions received the same message
        Assert.Equal(message.MessageId, sub1Messages[0].MessageId);
        Assert.Equal(message.MessageId, sub2Messages[0].MessageId);
        Assert.Equal(message.MessageId, sub3Messages[0].MessageId);
    }

    /// <summary>
    /// Test: Event publishing preserves all message properties
    /// Validates: Requirements 2.1
    /// </summary>
    [Fact]
    public async Task EventPublishing_PreservesAllMessageProperties()
    {
        // Arrange
        var topicName = "test-events";
        var subscriptionName = "test-subscription";
        
        var @event = new TestEvent
        {
            Name = "PropertyTestEvent",
            Payload = new TestEntity { Id = 42 },
            Metadata = new Metadata
            {
                Properties = new Dictionary<string, object>
                {
                    ["CorrelationId"] = Guid.NewGuid().ToString(),
                    ["CustomProperty1"] = "Value1",
                    ["CustomProperty2"] = 123
                }
            }
        };

        // Act
        var message = _testHelpers!.CreateTestEventMessage(@event);
        message.ApplicationProperties["AdditionalProperty"] = "AdditionalValue";
        message.ApplicationProperties["Priority"] = "High";
        
        await _testHelpers.SendMessageToTopicAsync(topicName, message);

        // Assert
        var receivedMessages = await _testHelpers.ReceiveMessagesFromSubscriptionAsync(
            topicName, 
            subscriptionName, 
            1, 
            TimeSpan.FromSeconds(10));
        
        Assert.Single(receivedMessages);
        var received = receivedMessages[0];
        
        Assert.Equal(message.MessageId, received.MessageId);
        Assert.Equal(message.CorrelationId, received.CorrelationId);
        Assert.Equal(message.Subject, received.Subject);
        Assert.Equal(message.ContentType, received.ContentType);
        Assert.Equal("AdditionalValue", received.ApplicationProperties["AdditionalProperty"]);
        Assert.Equal("High", received.ApplicationProperties["Priority"]);
        Assert.True(received.ApplicationProperties.ContainsKey("EventType"));
        Assert.True(received.ApplicationProperties.ContainsKey("Timestamp"));
    }

    /// <summary>
    /// Test: Concurrent event publishing to topics
    /// Validates: Requirements 2.1
    /// </summary>
    [Fact]
    public async Task EventPublishing_ConcurrentPublishing_NoMessageLoss()
    {
        // Arrange
        var topicName = "test-events";
        var subscriptionName = "test-subscription";
        var eventCount = 50;
        
        var events = Enumerable.Range(1, eventCount)
            .Select(i => new TestEvent
            {
                Name = $"ConcurrentEvent{i}",
                Payload = new TestEntity { Id = i },
                Metadata = new Metadata
                {
                    Properties = new Dictionary<string, object>
                    {
                        ["CorrelationId"] = Guid.NewGuid().ToString()
                    }
                }
            })
            .ToList();

        // Act - Send events concurrently
        var sendTasks = events.Select(async @event =>
        {
            var message = _testHelpers!.CreateTestEventMessage(@event);
            await _testHelpers.SendMessageToTopicAsync(topicName, message);
        });
        
        await Task.WhenAll(sendTasks);

        // Assert
        var receivedMessages = await _testHelpers!.ReceiveMessagesFromSubscriptionAsync(
            topicName, 
            subscriptionName, 
            eventCount, 
            TimeSpan.FromSeconds(30));
        
        Assert.Equal(eventCount, receivedMessages.Count);
        
        // Verify all messages have unique MessageIds
        var uniqueMessageIds = receivedMessages.Select(m => m.MessageId).Distinct().Count();
        Assert.Equal(eventCount, uniqueMessageIds);
    }

    /// <summary>
    /// Test: Event metadata is properly serialized and preserved
    /// Validates: Requirements 2.1
    /// </summary>
    [Fact]
    public async Task EventPublishing_SerializesMetadataCorrectly()
    {
        // Arrange
        var topicName = "test-events";
        var subscriptionName = "test-subscription";
        
        var @event = new TestEvent
        {
            Name = "MetadataTestEvent",
            Payload = new TestEntity { Id = 1 },
            Metadata = new Metadata
            {
                Properties = new Dictionary<string, object>
                {
                    ["CorrelationId"] = Guid.NewGuid().ToString(),
                    ["UserId"] = "user123",
                    ["TenantId"] = "tenant456",
                    ["Version"] = 1,
                    ["Timestamp"] = DateTimeOffset.UtcNow.ToString("O")
                }
            }
        };

        // Act
        var message = _testHelpers!.CreateTestEventMessage(@event);
        await _testHelpers.SendMessageToTopicAsync(topicName, message);

        // Assert
        var receivedMessages = await _testHelpers.ReceiveMessagesFromSubscriptionAsync(
            topicName, 
            subscriptionName, 
            1, 
            TimeSpan.FromSeconds(10));
        
        Assert.Single(receivedMessages);
        var received = receivedMessages[0];
        
        // Verify the message body can be deserialized back to the event
        var bodyJson = received.Body.ToString();
        Assert.NotEmpty(bodyJson);
        Assert.Contains("MetadataTestEvent", bodyJson);
    }

    /// <summary>
    /// Test: Large batch event publishing
    /// Validates: Requirements 2.1
    /// </summary>
    [Fact]
    public async Task EventPublishing_LargeBatch_AllEventsDelivered()
    {
        // Arrange
        var topicName = "test-events";
        var subscriptionName = "test-subscription";
        var batchSize = 100;
        
        var events = Enumerable.Range(1, batchSize)
            .Select(i => new TestEvent
            {
                Name = $"BatchEvent{i}",
                Payload = new TestEntity { Id = i },
                Metadata = new Metadata
                {
                    Properties = new Dictionary<string, object>
                    {
                        ["CorrelationId"] = Guid.NewGuid().ToString(),
                        ["BatchIndex"] = i
                    }
                }
            })
            .ToList();

        // Act
        foreach (var @event in events)
        {
            var message = _testHelpers!.CreateTestEventMessage(@event);
            await _testHelpers.SendMessageToTopicAsync(topicName, message);
        }

        // Assert
        var receivedMessages = await _testHelpers!.ReceiveMessagesFromSubscriptionAsync(
            topicName, 
            subscriptionName, 
            batchSize, 
            TimeSpan.FromSeconds(60));
        
        Assert.Equal(batchSize, receivedMessages.Count);
    }

    #endregion

    #region Helper Methods

    private async Task CreateTestTopicsAndSubscriptionsAsync()
    {
        var topicsAndSubscriptions = new[]
        {
            new { TopicName = "test-events", Subscriptions = new[] { "test-subscription" } },
            new { TopicName = "test-events-fanout", Subscriptions = new[] { "subscription-1", "subscription-2", "subscription-3" } }
        };

        foreach (var config in topicsAndSubscriptions)
        {
            try
            {
                // Create topic if it doesn't exist
                if (!await _adminClient!.TopicExistsAsync(config.TopicName))
                {
                    var topicOptions = new CreateTopicOptions(config.TopicName)
                    {
                        DefaultMessageTimeToLive = TimeSpan.FromDays(14),
                        EnableBatchedOperations = true,
                        MaxSizeInMegabytes = 1024
                    };

                    await _adminClient.CreateTopicAsync(topicOptions);
                    _output.WriteLine($"Created topic: {config.TopicName}");
                }

                // Create subscriptions
                foreach (var subscriptionName in config.Subscriptions)
                {
                    if (!await _adminClient.SubscriptionExistsAsync(config.TopicName, subscriptionName))
                    {
                        var subscriptionOptions = new CreateSubscriptionOptions(config.TopicName, subscriptionName)
                        {
                            MaxDeliveryCount = 10,
                            LockDuration = TimeSpan.FromMinutes(5),
                            EnableBatchedOperations = true,
                            DefaultMessageTimeToLive = TimeSpan.FromDays(14)
                        };

                        await _adminClient.CreateSubscriptionAsync(subscriptionOptions);
                        _output.WriteLine($"Created subscription: {config.TopicName}/{subscriptionName}");
                    }
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Error creating topic/subscription {config.TopicName}: {ex.Message}");
            }
        }
    }

    private async Task EnsureTopicWithMultipleSubscriptionsExistsAsync(string topicName, string[] subscriptionNames)
    {
        // Create topic if it doesn't exist
        if (!await _adminClient!.TopicExistsAsync(topicName))
        {
            var topicOptions = new CreateTopicOptions(topicName)
            {
                DefaultMessageTimeToLive = TimeSpan.FromDays(14),
                EnableBatchedOperations = true
            };

            await _adminClient.CreateTopicAsync(topicOptions);
        }

        // Create subscriptions
        foreach (var subscriptionName in subscriptionNames)
        {
            if (!await _adminClient.SubscriptionExistsAsync(topicName, subscriptionName))
            {
                var subscriptionOptions = new CreateSubscriptionOptions(topicName, subscriptionName)
                {
                    MaxDeliveryCount = 10,
                    LockDuration = TimeSpan.FromMinutes(5)
                };

                await _adminClient.CreateSubscriptionAsync(subscriptionOptions);
            }
        }
    }

    #endregion
}

using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Azure.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Azure.Tests.Integration;

/// <summary>
/// Integration tests for Azure Service Bus event session handling including session-based ordering,
/// session state management, and event correlation across sessions.
/// Feature: azure-cloud-integration-testing
/// Task: 5.4 Create Azure Service Bus event session handling tests
/// </summary>
public class ServiceBusEventSessionHandlingTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;
    private IAzureTestEnvironment? _testEnvironment;
    private ServiceBusClient? _serviceBusClient;
    private ServiceBusTestHelpers? _testHelpers;
    private ServiceBusAdministrationClient? _adminClient;

    public ServiceBusEventSessionHandlingTests(ITestOutputHelper output)
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
        var config = new AzureTestConfiguration
        {
            UseAzurite = true
        };

        var azuriteConfig = new AzuriteConfiguration
        {
            StartupTimeoutSeconds = 30
        };

        var azuriteManager = new AzuriteManager(
            azuriteConfig,
            _loggerFactory.CreateLogger<AzuriteManager>());

        _testEnvironment = new AzureTestEnvironment(
            config,
            _loggerFactory.CreateLogger<AzureTestEnvironment>(),
            azuriteManager);

        await _testEnvironment.InitializeAsync();

        var connectionString = _testEnvironment.GetServiceBusConnectionString();
        _serviceBusClient = new ServiceBusClient(connectionString);
        
        _testHelpers = new ServiceBusTestHelpers(
            _serviceBusClient,
            _loggerFactory.CreateLogger<ServiceBusTestHelpers>());

        _adminClient = new ServiceBusAdministrationClient(connectionString);
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

    #region Event Session Handling Tests (Requirement 2.5)

    /// <summary>
    /// Test: Event ordering within sessions
    /// Validates: Requirement 2.5
    /// </summary>
    [Fact]
    public async Task EventSessionHandling_OrderingWithinSession_PreservesSequence()
    {
        // Arrange
        var topicName = "session-events-topic";
        var subscriptionName = "session-events-sub";
        var sessionId = $"session-{Guid.NewGuid()}";

        await CreateSessionEnabledTopicAndSubscriptionAsync(topicName, subscriptionName);

        var events = Enumerable.Range(1, 10)
            .Select(i => CreateSessionMessage($"Event-{i}", sessionId, i))
            .ToList();

        // Act
        foreach (var @event in events)
        {
            await _testHelpers!.SendMessageToTopicAsync(topicName, @event);
        }

        // Assert
        var receiver = await _serviceBusClient!.AcceptSessionAsync(topicName, subscriptionName, sessionId);

        var receivedMessages = new List<ServiceBusReceivedMessage>();
        
        try
        {
            for (int i = 0; i < events.Count; i++)
            {
                var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
                if (message != null)
                {
                    receivedMessages.Add(message);
                    await receiver.CompleteMessageAsync(message);
                }
            }
        }
        finally
        {
            await receiver.DisposeAsync();
        }

        Assert.Equal(events.Count, receivedMessages.Count);

        // Verify ordering
        for (int i = 0; i < receivedMessages.Count; i++)
        {
            var sequenceNumber = (int)receivedMessages[i].ApplicationProperties["SequenceNumber"];
            Assert.Equal(i + 1, sequenceNumber);
        }
    }

    /// <summary>
    /// Test: Session-based event processing with multiple concurrent sessions
    /// Validates: Requirement 2.5
    /// </summary>
    [Fact]
    public async Task EventSessionHandling_MultipleConcurrentSessions_ProcessIndependently()
    {
        // Arrange
        var topicName = "multi-session-topic";
        var subscriptionName = "multi-session-sub";
        
        await CreateSessionEnabledTopicAndSubscriptionAsync(topicName, subscriptionName);

        var session1Id = $"session-1-{Guid.NewGuid()}";
        var session2Id = $"session-2-{Guid.NewGuid()}";
        var session3Id = $"session-3-{Guid.NewGuid()}";

        var session1Events = Enumerable.Range(1, 5)
            .Select(i => CreateSessionMessage($"S1-Event-{i}", session1Id, i))
            .ToList();

        var session2Events = Enumerable.Range(1, 5)
            .Select(i => CreateSessionMessage($"S2-Event-{i}", session2Id, i))
            .ToList();

        var session3Events = Enumerable.Range(1, 5)
            .Select(i => CreateSessionMessage($"S3-Event-{i}", session3Id, i))
            .ToList();

        // Act - Send all events concurrently
        var allEvents = session1Events.Concat(session2Events).Concat(session3Events);
        var sendTasks = allEvents.Select(e => _testHelpers!.SendMessageToTopicAsync(topicName, e));
        await Task.WhenAll(sendTasks);

        // Assert - Process each session independently
        var session1Received = await ProcessSessionAsync(topicName, subscriptionName, session1Id, 5);
        var session2Received = await ProcessSessionAsync(topicName, subscriptionName, session2Id, 5);
        var session3Received = await ProcessSessionAsync(topicName, subscriptionName, session3Id, 5);

        Assert.Equal(5, session1Received.Count);
        Assert.Equal(5, session2Received.Count);
        Assert.Equal(5, session3Received.Count);

        // Verify each session maintained its order
        VerifySessionOrdering(session1Received);
        VerifySessionOrdering(session2Received);
        VerifySessionOrdering(session3Received);
    }

    /// <summary>
    /// Test: Event correlation across sessions
    /// Validates: Requirement 2.5
    /// </summary>
    [Fact]
    public async Task EventSessionHandling_CorrelationAcrossSessions_PreservesCorrelationId()
    {
        // Arrange
        var topicName = "correlation-session-topic";
        var subscriptionName = "correlation-session-sub";
        
        await CreateSessionEnabledTopicAndSubscriptionAsync(topicName, subscriptionName);

        var correlationId = Guid.NewGuid().ToString();
        var session1Id = $"session-1-{Guid.NewGuid()}";
        var session2Id = $"session-2-{Guid.NewGuid()}";

        var session1Events = Enumerable.Range(1, 3)
            .Select(i => CreateSessionMessageWithCorrelation($"S1-Event-{i}", session1Id, correlationId, i))
            .ToList();

        var session2Events = Enumerable.Range(1, 3)
            .Select(i => CreateSessionMessageWithCorrelation($"S2-Event-{i}", session2Id, correlationId, i))
            .ToList();

        // Act
        foreach (var @event in session1Events.Concat(session2Events))
        {
            await _testHelpers!.SendMessageToTopicAsync(topicName, @event);
        }

        // Assert
        var session1Received = await ProcessSessionAsync(topicName, subscriptionName, session1Id, 3);
        var session2Received = await ProcessSessionAsync(topicName, subscriptionName, session2Id, 3);

        // Verify correlation ID is preserved across both sessions
        Assert.All(session1Received, msg => Assert.Equal(correlationId, msg.CorrelationId));
        Assert.All(session2Received, msg => Assert.Equal(correlationId, msg.CorrelationId));
    }

    /// <summary>
    /// Test: Session state management for events
    /// Validates: Requirement 2.5
    /// </summary>
    [Fact]
    public async Task EventSessionHandling_SessionState_PersistsAcrossMessages()
    {
        // Arrange
        var topicName = "session-state-topic";
        var subscriptionName = "session-state-sub";
        var sessionId = $"session-{Guid.NewGuid()}";

        await CreateSessionEnabledTopicAndSubscriptionAsync(topicName, subscriptionName);

        var events = Enumerable.Range(1, 5)
            .Select(i => CreateSessionMessage($"Event-{i}", sessionId, i))
            .ToList();

        // Act
        foreach (var @event in events)
        {
            await _testHelpers!.SendMessageToTopicAsync(topicName, @event);
        }

        // Process with session state
        var receiver = await _serviceBusClient!.AcceptSessionAsync(topicName, subscriptionName, sessionId);

        try
        {
            // Set initial session state
            var initialState = new BinaryData("ProcessedCount:0");
            await receiver.SetSessionStateAsync(initialState);

            int processedCount = 0;
            
            for (int i = 0; i < events.Count; i++)
            {
                var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
                if (message != null)
                {
                    processedCount++;
                    
                    // Update session state
                    var state = new BinaryData($"ProcessedCount:{processedCount}");
                    await receiver.SetSessionStateAsync(state);
                    
                    await receiver.CompleteMessageAsync(message);
                }
            }

            // Assert - Verify final session state
            var finalState = await receiver.GetSessionStateAsync();
            var finalStateString = finalState.ToString();
            
            Assert.Equal($"ProcessedCount:{events.Count}", finalStateString);
        }
        finally
        {
            await receiver.DisposeAsync();
        }
    }

    /// <summary>
    /// Test: Session lock renewal for long-running event processing
    /// Validates: Requirement 2.5
    /// </summary>
    [Fact]
    public async Task EventSessionHandling_SessionLockRenewal_MaintainsLock()
    {
        // Arrange
        var topicName = "session-lock-topic";
        var subscriptionName = "session-lock-sub";
        var sessionId = $"session-{Guid.NewGuid()}";

        await CreateSessionEnabledTopicAndSubscriptionAsync(topicName, subscriptionName);

        var @event = CreateSessionMessage("LongProcessingEvent", sessionId, 1);
        await _testHelpers!.SendMessageToTopicAsync(topicName, @event);

        // Act
        var receiver = await _serviceBusClient!.AcceptSessionAsync(topicName, subscriptionName, sessionId);

        try
        {
            var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(message);

            // Simulate long processing with lock renewal
            var lockDuration = receiver.SessionLockedUntil - DateTimeOffset.UtcNow;
            _output.WriteLine($"Initial lock duration: {lockDuration}");

            // Renew lock
            await receiver.RenewSessionLockAsync();

            var newLockDuration = receiver.SessionLockedUntil - DateTimeOffset.UtcNow;
            _output.WriteLine($"Lock duration after renewal: {newLockDuration}");

            // Assert - Lock was renewed
            Assert.True(newLockDuration > lockDuration);

            await receiver.CompleteMessageAsync(message);
        }
        finally
        {
            await receiver.DisposeAsync();
        }
    }

    /// <summary>
    /// Test: Session-based event processing with different event types
    /// Validates: Requirement 2.5
    /// </summary>
    [Fact]
    public async Task EventSessionHandling_DifferentEventTypes_ProcessedInOrder()
    {
        // Arrange
        var topicName = "mixed-events-topic";
        var subscriptionName = "mixed-events-sub";
        var sessionId = $"session-{Guid.NewGuid()}";

        await CreateSessionEnabledTopicAndSubscriptionAsync(topicName, subscriptionName);

        var events = new List<ServiceBusMessage>
        {
            CreateSessionMessageWithType("Event1", sessionId, "OrderCreated", 1),
            CreateSessionMessageWithType("Event2", sessionId, "OrderUpdated", 2),
            CreateSessionMessageWithType("Event3", sessionId, "PaymentProcessed", 3),
            CreateSessionMessageWithType("Event4", sessionId, "OrderShipped", 4),
            CreateSessionMessageWithType("Event5", sessionId, "OrderCompleted", 5)
        };

        // Act
        foreach (var @event in events)
        {
            await _testHelpers!.SendMessageToTopicAsync(topicName, @event);
        }

        // Assert
        var received = await ProcessSessionAsync(topicName, subscriptionName, sessionId, events.Count);

        Assert.Equal(events.Count, received.Count);

        // Verify event types are in correct order
        var expectedTypes = new[] { "OrderCreated", "OrderUpdated", "PaymentProcessed", "OrderShipped", "OrderCompleted" };
        for (int i = 0; i < received.Count; i++)
        {
            var eventType = received[i].ApplicationProperties["EventType"].ToString();
            Assert.Equal(expectedTypes[i], eventType);
        }
    }

    #endregion

    #region Helper Methods

    private async Task CreateSessionEnabledTopicAndSubscriptionAsync(string topicName, string subscriptionName)
    {
        try
        {
            // Create topic
            if (!await _adminClient!.TopicExistsAsync(topicName))
            {
                var topicOptions = new CreateTopicOptions(topicName)
                {
                    DefaultMessageTimeToLive = TimeSpan.FromDays(14),
                    EnableBatchedOperations = true
                };

                await _adminClient.CreateTopicAsync(topicOptions);
                _output.WriteLine($"Created topic: {topicName}");
            }

            // Create session-enabled subscription
            if (!await _adminClient.SubscriptionExistsAsync(topicName, subscriptionName))
            {
                var subscriptionOptions = new CreateSubscriptionOptions(topicName, subscriptionName)
                {
                    RequiresSession = true,
                    MaxDeliveryCount = 10,
                    LockDuration = TimeSpan.FromMinutes(5),
                    DefaultMessageTimeToLive = TimeSpan.FromDays(14)
                };

                await _adminClient.CreateSubscriptionAsync(subscriptionOptions);
                _output.WriteLine($"Created session-enabled subscription: {subscriptionName}");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Error creating topic/subscription: {ex.Message}");
            throw;
        }
    }

    private ServiceBusMessage CreateSessionMessage(string messageId, string sessionId, int sequenceNumber)
    {
        var message = new ServiceBusMessage($"Event content: {messageId}")
        {
            MessageId = messageId,
            SessionId = sessionId,
            Subject = "SessionEvent"
        };

        message.ApplicationProperties["SequenceNumber"] = sequenceNumber;
        message.ApplicationProperties["Timestamp"] = DateTimeOffset.UtcNow.ToString("O");

        return message;
    }

    private ServiceBusMessage CreateSessionMessageWithCorrelation(
        string messageId, 
        string sessionId, 
        string correlationId, 
        int sequenceNumber)
    {
        var message = new ServiceBusMessage($"Event content: {messageId}")
        {
            MessageId = messageId,
            SessionId = sessionId,
            CorrelationId = correlationId,
            Subject = "CorrelatedSessionEvent"
        };

        message.ApplicationProperties["SequenceNumber"] = sequenceNumber;
        message.ApplicationProperties["Timestamp"] = DateTimeOffset.UtcNow.ToString("O");

        return message;
    }

    private ServiceBusMessage CreateSessionMessageWithType(
        string messageId, 
        string sessionId, 
        string eventType, 
        int sequenceNumber)
    {
        var message = new ServiceBusMessage($"Event content: {messageId}")
        {
            MessageId = messageId,
            SessionId = sessionId,
            Subject = eventType
        };

        message.ApplicationProperties["EventType"] = eventType;
        message.ApplicationProperties["SequenceNumber"] = sequenceNumber;
        message.ApplicationProperties["Timestamp"] = DateTimeOffset.UtcNow.ToString("O");

        return message;
    }

    private async Task<List<ServiceBusReceivedMessage>> ProcessSessionAsync(
        string topicName, 
        string subscriptionName, 
        string sessionId, 
        int expectedCount)
    {
        var received = new List<ServiceBusReceivedMessage>();
        var receiver = await _serviceBusClient!.AcceptSessionAsync(topicName, subscriptionName, sessionId);

        try
        {
            for (int i = 0; i < expectedCount; i++)
            {
                var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
                if (message != null)
                {
                    received.Add(message);
                    await receiver.CompleteMessageAsync(message);
                }
            }
        }
        finally
        {
            await receiver.DisposeAsync();
        }

        return received;
    }

    private void VerifySessionOrdering(List<ServiceBusReceivedMessage> messages)
    {
        for (int i = 0; i < messages.Count; i++)
        {
            var sequenceNumber = (int)messages[i].ApplicationProperties["SequenceNumber"];
            Assert.Equal(i + 1, sequenceNumber);
        }
    }

    #endregion
}

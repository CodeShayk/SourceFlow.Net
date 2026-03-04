using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Cloud.Azure.Tests.TestHelpers;

/// <summary>
/// Helper utilities for testing Azure Service Bus functionality including message creation,
/// session handling, duplicate detection, and validation.
/// </summary>
public class ServiceBusTestHelpers
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly ILogger<ServiceBusTestHelpers> _logger;

    public ServiceBusTestHelpers(
        ServiceBusClient serviceBusClient,
        ILogger<ServiceBusTestHelpers> logger)
    {
        _serviceBusClient = serviceBusClient ?? throw new ArgumentNullException(nameof(serviceBusClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Creates a new instance using an Azure test environment.
    /// </summary>
    public ServiceBusTestHelpers(
        IAzureTestEnvironment environment,
        ILoggerFactory loggerFactory)
    {
        if (environment == null) throw new ArgumentNullException(nameof(environment));
        if (loggerFactory == null) throw new ArgumentNullException(nameof(loggerFactory));

        var connectionString = environment.GetServiceBusConnectionString();
        _serviceBusClient = new ServiceBusClient(connectionString);
        _logger = loggerFactory.CreateLogger<ServiceBusTestHelpers>();
    }

    /// <summary>
    /// Creates a test Service Bus message for a command with proper correlation IDs and metadata.
    /// </summary>
    /// <param name="command">The command to create a message for.</param>
    /// <param name="correlationId">Optional correlation ID. If not provided, a new GUID is generated.</param>
    /// <returns>A configured ServiceBusMessage ready for sending.</returns>
    public ServiceBusMessage CreateTestCommandMessage(ICommand command, string? correlationId = null)
    {
        if (command == null)
            throw new ArgumentNullException(nameof(command));

        var serializedCommand = JsonSerializer.Serialize(command, command.GetType());
        
        // Try to get correlation ID from metadata properties
        string? metadataCorrelationId = null;
        if (command.Metadata?.Properties?.ContainsKey("CorrelationId") == true)
        {
            metadataCorrelationId = command.Metadata.Properties["CorrelationId"]?.ToString();
        }
        
        var message = new ServiceBusMessage(serializedCommand)
        {
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = correlationId ?? metadataCorrelationId ?? Guid.NewGuid().ToString(),
            SessionId = command.Entity.ToString(), // For session-based ordering
            Subject = command.Name,
            ContentType = "application/json"
        };

        // Add custom properties for routing and metadata
        message.ApplicationProperties["CommandType"] = command.GetType().AssemblyQualifiedName ?? command.GetType().FullName ?? command.GetType().Name;
        message.ApplicationProperties["EntityId"] = command.Entity.ToString();
        message.ApplicationProperties["Timestamp"] = DateTimeOffset.UtcNow.ToString("O");
        message.ApplicationProperties["SourceSystem"] = "SourceFlow.Tests";

        _logger.LogDebug("Created command message: MessageId={MessageId}, CorrelationId={CorrelationId}, SessionId={SessionId}",
            message.MessageId, message.CorrelationId, message.SessionId);

        return message;
    }

    /// <summary>
    /// Creates a test Service Bus message for an event with proper correlation IDs and metadata.
    /// </summary>
    /// <param name="event">The event to create a message for.</param>
    /// <param name="correlationId">Optional correlation ID. If not provided, a new GUID is generated.</param>
    /// <returns>A configured ServiceBusMessage ready for sending.</returns>
    public ServiceBusMessage CreateTestEventMessage(IEvent @event, string? correlationId = null)
    {
        if (@event == null)
            throw new ArgumentNullException(nameof(@event));

        var serializedEvent = JsonSerializer.Serialize(@event, @event.GetType());
        
        // Try to get correlation ID from metadata properties
        string? metadataCorrelationId = null;
        if (@event.Metadata?.Properties?.ContainsKey("CorrelationId") == true)
        {
            metadataCorrelationId = @event.Metadata.Properties["CorrelationId"]?.ToString();
        }
        
        var message = new ServiceBusMessage(serializedEvent)
        {
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = correlationId ?? metadataCorrelationId ?? Guid.NewGuid().ToString(),
            Subject = @event.Name,
            ContentType = "application/json"
        };

        // Add custom properties for event metadata
        message.ApplicationProperties["EventType"] = @event.GetType().AssemblyQualifiedName ?? @event.GetType().FullName ?? @event.GetType().Name;
        message.ApplicationProperties["Timestamp"] = DateTimeOffset.UtcNow.ToString("O");
        message.ApplicationProperties["SourceSystem"] = "SourceFlow.Tests";

        _logger.LogDebug("Created event message: MessageId={MessageId}, CorrelationId={CorrelationId}",
            message.MessageId, message.CorrelationId);

        return message;
    }

    /// <summary>
    /// Creates a batch of test command messages with the same session ID for ordering validation.
    /// </summary>
    /// <param name="commands">The commands to create messages for.</param>
    /// <param name="sessionId">The session ID to use for all messages.</param>
    /// <param name="correlationId">Optional correlation ID for all messages.</param>
    /// <returns>A list of configured ServiceBusMessage instances.</returns>
    public List<ServiceBusMessage> CreateSessionCommandBatch(
        IEnumerable<ICommand> commands,
        string sessionId,
        string? correlationId = null)
    {
        if (commands == null)
            throw new ArgumentNullException(nameof(commands));
        if (string.IsNullOrEmpty(sessionId))
            throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

        var messages = new List<ServiceBusMessage>();
        var batchCorrelationId = correlationId ?? Guid.NewGuid().ToString();

        foreach (var command in commands)
        {
            var message = CreateTestCommandMessage(command, batchCorrelationId);
            message.SessionId = sessionId; // Override with batch session ID
            messages.Add(message);
        }

        _logger.LogInformation("Created session command batch: SessionId={SessionId}, MessageCount={Count}",
            sessionId, messages.Count);

        return messages;
    }

    /// <summary>
    /// Validates that commands are processed in order within a session.
    /// </summary>
    /// <param name="queueName">The queue name to test.</param>
    /// <param name="commands">The commands to send in order.</param>
    /// <param name="timeout">Maximum time to wait for processing.</param>
    /// <returns>True if commands were processed in order, false otherwise.</returns>
    public async Task<bool> ValidateSessionOrderingAsync(
        string queueName,
        List<ICommand> commands,
        TimeSpan? timeout = null)
    {
        if (string.IsNullOrEmpty(queueName))
            throw new ArgumentException("Queue name cannot be null or empty", nameof(queueName));
        if (commands == null || commands.Count == 0)
            throw new ArgumentException("Commands list cannot be null or empty", nameof(commands));

        var testTimeout = timeout ?? TimeSpan.FromSeconds(30);
        var sessionId = Guid.NewGuid().ToString();
        var receivedCommands = new ConcurrentBag<ICommand>();
        var processedCount = 0;

        // Create session processor
        var processor = _serviceBusClient.CreateSessionProcessor(queueName, new ServiceBusSessionProcessorOptions
        {
            MaxConcurrentSessions = 1,
            MaxConcurrentCallsPerSession = 1,
            AutoCompleteMessages = false,
            SessionIdleTimeout = TimeSpan.FromSeconds(5)
        });

        processor.ProcessMessageAsync += async args =>
        {
            try
            {
                var commandJson = args.Message.Body.ToString();
                var commandTypeName = args.Message.ApplicationProperties["CommandType"].ToString();
                var commandType = Type.GetType(commandTypeName!);

                if (commandType == null)
                {
                    _logger.LogError("Could not resolve command type: {CommandType}", commandTypeName);
                    await args.AbandonMessageAsync(args.Message);
                    return;
                }

                var command = (ICommand?)JsonSerializer.Deserialize(commandJson, commandType);
                if (command != null)
                {
                    receivedCommands.Add(command);
                    Interlocked.Increment(ref processedCount);

                    _logger.LogDebug("Processed command {CommandType} in session {SessionId}",
                        command.GetType().Name, args.SessionId);
                }

                await args.CompleteMessageAsync(args.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message in session {SessionId}", args.SessionId);
                await args.AbandonMessageAsync(args.Message);
            }
        };

        processor.ProcessErrorAsync += args =>
        {
            _logger.LogError(args.Exception, "Error in session processor: {ErrorSource}", args.ErrorSource);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync();

        try
        {
            // Send commands with same session ID
            var sender = _serviceBusClient.CreateSender(queueName);
            try
            {
                var messages = CreateSessionCommandBatch(commands, sessionId);
                foreach (var message in messages)
                {
                    await sender.SendMessageAsync(message);
                    _logger.LogDebug("Sent command to queue {QueueName} with session {SessionId}",
                        queueName, sessionId);
                }
            }
            finally
            {
                await sender.DisposeAsync();
            }

            // Wait for processing with timeout
            var stopwatch = Stopwatch.StartNew();
            while (processedCount < commands.Count && stopwatch.Elapsed < testTimeout)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            if (processedCount < commands.Count)
            {
                _logger.LogWarning("Timeout: Only processed {ProcessedCount} of {TotalCount} commands",
                    processedCount, commands.Count);
                return false;
            }

            // Validate order
            return ValidateCommandOrder(commands, receivedCommands.ToList());
        }
        finally
        {
            await processor.StopProcessingAsync();
        }
    }

    /// <summary>
    /// Validates that duplicate messages are properly detected and deduplicated.
    /// </summary>
    /// <param name="queueName">The queue name to test (must have duplicate detection enabled).</param>
    /// <param name="command">The command to send multiple times.</param>
    /// <param name="sendCount">Number of times to send the same message.</param>
    /// <param name="timeout">Maximum time to wait for processing.</param>
    /// <returns>True if only one message was delivered, false otherwise.</returns>
    public async Task<bool> ValidateDuplicateDetectionAsync(
        string queueName,
        ICommand command,
        int sendCount,
        TimeSpan? timeout = null)
    {
        if (string.IsNullOrEmpty(queueName))
            throw new ArgumentException("Queue name cannot be null or empty", nameof(queueName));
        if (command == null)
            throw new ArgumentNullException(nameof(command));
        if (sendCount < 2)
            throw new ArgumentException("Send count must be at least 2 for duplicate detection testing", nameof(sendCount));

        var testTimeout = timeout ?? TimeSpan.FromSeconds(10);
        var sender = _serviceBusClient.CreateSender(queueName);

        try
        {
            // Create a message with a fixed MessageId for duplicate detection
            var message = CreateTestCommandMessage(command);
            var fixedMessageId = message.MessageId;

            // Send the same message multiple times with the same MessageId
            for (int i = 0; i < sendCount; i++)
            {
                var duplicateMessage = CreateTestCommandMessage(command);
                duplicateMessage.MessageId = fixedMessageId; // Use same MessageId for deduplication
                
                await sender.SendMessageAsync(duplicateMessage);
                _logger.LogDebug("Sent duplicate message {MessageId} (attempt {Attempt})",
                    fixedMessageId, i + 1);
            }

            // Receive messages and verify only one was delivered
            var receiver = _serviceBusClient.CreateReceiver(queueName);
            try
            {
                var receivedCount = 0;
                var stopwatch = Stopwatch.StartNew();

                while (stopwatch.Elapsed < testTimeout)
                {
                    var receivedMessage = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(1));
                    if (receivedMessage != null)
                    {
                        receivedCount++;
                        await receiver.CompleteMessageAsync(receivedMessage);
                        _logger.LogDebug("Received message {MessageId}", receivedMessage.MessageId);
                    }
                    else
                    {
                        break; // No more messages
                    }
                }

                var success = receivedCount == 1;
                _logger.LogInformation(
                    "Duplicate detection validation: sent {SentCount}, received {ReceivedCount}, success: {Success}",
                    sendCount, receivedCount, success);

                return success;
            }
            finally
            {
                await receiver.DisposeAsync();
            }
        }
        finally
        {
            await sender.DisposeAsync();
        }
    }

    /// <summary>
    /// Sends a batch of messages to a queue.
    /// </summary>
    /// <param name="queueName">The queue name to send to.</param>
    /// <param name="messages">The messages to send.</param>
    public async Task SendMessageBatchAsync(string queueName, IEnumerable<ServiceBusMessage> messages)
    {
        if (string.IsNullOrEmpty(queueName))
            throw new ArgumentException("Queue name cannot be null or empty", nameof(queueName));
        if (messages == null)
            throw new ArgumentNullException(nameof(messages));

        var sender = _serviceBusClient.CreateSender(queueName);
        try
        {
            var messageList = messages.ToList();
            foreach (var message in messageList)
            {
                await sender.SendMessageAsync(message);
            }

            _logger.LogInformation("Sent {Count} messages to queue {QueueName}", messageList.Count, queueName);
        }
        finally
        {
            await sender.DisposeAsync();
        }
    }

    /// <summary>
    /// Receives messages from a queue with a timeout.
    /// </summary>
    /// <param name="queueName">The queue name to receive from.</param>
    /// <param name="maxMessages">Maximum number of messages to receive.</param>
    /// <param name="timeout">Maximum time to wait for messages.</param>
    /// <returns>List of received messages.</returns>
    public async Task<List<ServiceBusReceivedMessage>> ReceiveMessagesAsync(
        string queueName,
        int maxMessages,
        TimeSpan? timeout = null)
    {
        if (string.IsNullOrEmpty(queueName))
            throw new ArgumentException("Queue name cannot be null or empty", nameof(queueName));
        if (maxMessages < 1)
            throw new ArgumentException("Max messages must be at least 1", nameof(maxMessages));

        var testTimeout = timeout ?? TimeSpan.FromSeconds(10);
        var receiver = _serviceBusClient.CreateReceiver(queueName);
        var receivedMessages = new List<ServiceBusReceivedMessage>();

        try
        {
            var stopwatch = Stopwatch.StartNew();

            while (receivedMessages.Count < maxMessages && stopwatch.Elapsed < testTimeout)
            {
                var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(1));
                if (message != null)
                {
                    receivedMessages.Add(message);
                    await receiver.CompleteMessageAsync(message);
                    _logger.LogDebug("Received message {MessageId} from queue {QueueName}",
                        message.MessageId, queueName);
                }
                else
                {
                    break; // No more messages
                }
            }

            _logger.LogInformation("Received {Count} messages from queue {QueueName}",
                receivedMessages.Count, queueName);

            return receivedMessages;
        }
        finally
        {
            await receiver.DisposeAsync();
        }
    }

    /// <summary>
    /// Sends a message to a topic.
    /// </summary>
    /// <param name="topicName">The topic name to send to.</param>
    /// <param name="message">The message to send.</param>
    public async Task SendMessageToTopicAsync(string topicName, ServiceBusMessage message)
    {
        if (string.IsNullOrEmpty(topicName))
            throw new ArgumentException("Topic name cannot be null or empty", nameof(topicName));
        if (message == null)
            throw new ArgumentNullException(nameof(message));

        var sender = _serviceBusClient.CreateSender(topicName);
        try
        {
            await sender.SendMessageAsync(message);
            _logger.LogInformation("Sent message {MessageId} to topic {TopicName}", message.MessageId, topicName);
        }
        finally
        {
            await sender.DisposeAsync();
        }
    }

    /// <summary>
    /// Receives messages from a topic subscription with a timeout.
    /// </summary>
    /// <param name="topicName">The topic name.</param>
    /// <param name="subscriptionName">The subscription name to receive from.</param>
    /// <param name="maxMessages">Maximum number of messages to receive.</param>
    /// <param name="timeout">Maximum time to wait for messages.</param>
    /// <returns>List of received messages.</returns>
    public async Task<List<ServiceBusReceivedMessage>> ReceiveMessagesFromSubscriptionAsync(
        string topicName,
        string subscriptionName,
        int maxMessages,
        TimeSpan? timeout = null)
    {
        if (string.IsNullOrEmpty(topicName))
            throw new ArgumentException("Topic name cannot be null or empty", nameof(topicName));
        if (string.IsNullOrEmpty(subscriptionName))
            throw new ArgumentException("Subscription name cannot be null or empty", nameof(subscriptionName));
        if (maxMessages < 1)
            throw new ArgumentException("Max messages must be at least 1", nameof(maxMessages));

        var testTimeout = timeout ?? TimeSpan.FromSeconds(10);
        var receiver = _serviceBusClient.CreateReceiver(topicName, subscriptionName);
        var receivedMessages = new List<ServiceBusReceivedMessage>();

        try
        {
            var stopwatch = Stopwatch.StartNew();

            while (receivedMessages.Count < maxMessages && stopwatch.Elapsed < testTimeout)
            {
                var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(1));
                if (message != null)
                {
                    receivedMessages.Add(message);
                    await receiver.CompleteMessageAsync(message);
                    _logger.LogDebug("Received message {MessageId} from subscription {TopicName}/{SubscriptionName}",
                        message.MessageId, topicName, subscriptionName);
                }
                else
                {
                    break; // No more messages
                }
            }

            _logger.LogInformation("Received {Count} messages from subscription {TopicName}/{SubscriptionName}",
                receivedMessages.Count, topicName, subscriptionName);

            return receivedMessages;
        }
        finally
        {
            await receiver.DisposeAsync();
        }
    }

    /// <summary>
    /// Validates that the received commands match the sent commands in order.
    /// </summary>
    private bool ValidateCommandOrder(List<ICommand> sent, List<ICommand> received)
    {
        if (sent.Count != received.Count)
        {
            _logger.LogError("Command count mismatch: sent {SentCount}, received {ReceivedCount}",
                sent.Count, received.Count);
            return false;
        }

        for (int i = 0; i < sent.Count; i++)
        {
            if (sent[i].GetType() != received[i].GetType() ||
                sent[i].Entity.ToString() != received[i].Entity.ToString())
            {
                _logger.LogError("Command order mismatch at index {Index}: expected {Expected}, got {Actual}",
                    i, sent[i].GetType().Name, received[i].GetType().Name);
                return false;
            }
        }

        _logger.LogInformation("Command order validation successful");
        return true;
    }
}

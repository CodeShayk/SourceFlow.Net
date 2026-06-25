using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Azure.Tests.TestHelpers;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;
using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Azure.Tests.Integration;

/// <summary>
/// Integration tests for Azure Service Bus command dispatching including routing,
/// session handling, duplicate detection, and dead letter queue processing.
/// Feature: azure-cloud-integration-testing
/// </summary>
public class ServiceBusCommandDispatchingTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;
    private IAzureTestEnvironment? _testEnvironment;
    private ServiceBusClient? _serviceBusClient;
    private ServiceBusTestHelpers? _testHelpers;
    private ServiceBusAdministrationClient? _adminClient;

    public ServiceBusCommandDispatchingTests(ITestOutputHelper output)
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

        // Create test queues
        await CreateTestQueuesAsync();
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

    #region Command Routing Tests (Requirements 1.1, 1.5)

    /// <summary>
    /// Test: Command routing to correct queues with correlation IDs
    /// Validates: Requirements 1.1
    /// </summary>
    [Fact]
    public async Task CommandRouting_SendsToCorrectQueue_WithCorrelationId()
    {
        // Arrange
        var queueName = "test-commands";
        var command = new TestCommand
        {
            Entity = new EntityRef { Id = 1 },
            Name = "TestCommand",
            Payload = new TestPayload { Data = "Test data" },
            Metadata = new Metadata
            {
                Properties = new Dictionary<string, object>
                {
                    ["CorrelationId"] = Guid.NewGuid().ToString()
                }
            }
        };

        var correlationId = command.Metadata.Properties["CorrelationId"].ToString();

        // Act
        var message = _testHelpers!.CreateTestCommandMessage(command, correlationId);
        await _testHelpers.SendMessageBatchAsync(queueName, new[] { message });

        // Assert
        var receivedMessages = await _testHelpers.ReceiveMessagesAsync(queueName, 1, TimeSpan.FromSeconds(10));
        
        Assert.Single(receivedMessages);
        Assert.Equal(correlationId, receivedMessages[0].CorrelationId);
        Assert.Equal(command.Name, receivedMessages[0].Subject);
        Assert.True(receivedMessages[0].ApplicationProperties.ContainsKey("CommandType"));
        Assert.True(receivedMessages[0].ApplicationProperties.ContainsKey("EntityId"));
    }

    /// <summary>
    /// Test: Concurrent command processing without message loss
    /// Validates: Requirements 1.5
    /// </summary>
    [Fact]
    public async Task CommandRouting_ConcurrentProcessing_NoMessageLoss()
    {
        // Arrange
        var queueName = "test-commands";
        var commandCount = 50;
        var commands = Enumerable.Range(1, commandCount)
            .Select(i => new TestCommand
            {
                Entity = new EntityRef { Id = i },
                Name = $"TestCommand{i}",
                Payload = new TestPayload { Data = $"Test data {i}" }
            })
            .ToList();

        // Act
        var messages = commands.Select(cmd => _testHelpers!.CreateTestCommandMessage(cmd)).ToList();
        
        // Send messages concurrently
        var sendTasks = messages.Select(msg => 
            _testHelpers!.SendMessageBatchAsync(queueName, new[] { msg }));
        await Task.WhenAll(sendTasks);

        // Assert
        var receivedMessages = await _testHelpers!.ReceiveMessagesAsync(
            queueName, 
            commandCount, 
            TimeSpan.FromSeconds(30));
        
        Assert.Equal(commandCount, receivedMessages.Count);
        
        // Verify all messages have unique MessageIds
        var uniqueMessageIds = receivedMessages.Select(m => m.MessageId).Distinct().Count();
        Assert.Equal(commandCount, uniqueMessageIds);
    }

    /// <summary>
    /// Test: Command routing preserves all message properties
    /// Validates: Requirements 1.1
    /// </summary>
    [Fact]
    public async Task CommandRouting_PreservesMessageProperties()
    {
        // Arrange
        var queueName = "test-commands";
        var command = new TestCommand
        {
            Entity = new EntityRef { Id = 42 },
            Name = "TestCommand",
            Payload = new TestPayload { Data = "Test data", Value = 123 }
        };

        // Act
        var message = _testHelpers!.CreateTestCommandMessage(command);
        message.ApplicationProperties["CustomProperty"] = "CustomValue";
        message.ApplicationProperties["Timestamp"] = DateTimeOffset.UtcNow.ToString("O");
        
        await _testHelpers.SendMessageBatchAsync(queueName, new[] { message });

        // Assert
        var receivedMessages = await _testHelpers.ReceiveMessagesAsync(queueName, 1, TimeSpan.FromSeconds(10));
        
        Assert.Single(receivedMessages);
        var received = receivedMessages[0];
        
        Assert.Equal(message.MessageId, received.MessageId);
        Assert.Equal(message.CorrelationId, received.CorrelationId);
        Assert.Equal(message.Subject, received.Subject);
        Assert.Equal("CustomValue", received.ApplicationProperties["CustomProperty"]);
        Assert.True(received.ApplicationProperties.ContainsKey("Timestamp"));
        Assert.Equal("42", received.ApplicationProperties["EntityId"]);
    }

    #endregion

    #region Session Handling Tests (Requirements 1.2)

    /// <summary>
    /// Test: Session-based ordering with multiple concurrent sessions
    /// Validates: Requirements 1.2
    /// </summary>
    [Fact]
    public async Task SessionHandling_PreservesOrderWithinSession()
    {
        // Arrange
        var queueName = "test-commands.fifo";
        await EnsureSessionQueueExistsAsync(queueName);

        var commands = Enumerable.Range(1, 10)
            .Select(i => new TestCommand
            {
                Entity = new EntityRef { Id = 1 }, // Same entity for session ordering
                Name = $"TestCommand{i}",
                Payload = new TestPayload { Data = "Sequence", Value = i }
            })
            .Cast<ICommand>()
            .ToList();

        // Act & Assert
        var result = await _testHelpers!.ValidateSessionOrderingAsync(queueName, commands, TimeSpan.FromSeconds(30));
        
        Assert.True(result, "Commands should be processed in order within session");
    }

    /// <summary>
    /// Test: Multiple concurrent sessions process independently
    /// Validates: Requirements 1.2
    /// </summary>
    [Fact]
    public async Task SessionHandling_MultipleSessions_ProcessIndependently()
    {
        // Arrange
        var queueName = "test-commands.fifo";
        await EnsureSessionQueueExistsAsync(queueName);

        var session1Commands = Enumerable.Range(1, 5)
            .Select(i => new TestCommand
            {
                Entity = new EntityRef { Id = 1 },
                Name = $"Session1Command{i}",
                Payload = new TestPayload { Data = "Session1", Value = i }
            })
            .Cast<ICommand>()
            .ToList();

        var session2Commands = Enumerable.Range(1, 5)
            .Select(i => new TestCommand
            {
                Entity = new EntityRef { Id = 2 },
                Name = $"Session2Command{i}",
                Payload = new TestPayload { Data = "Session2", Value = i }
            })
            .Cast<ICommand>()
            .ToList();

        // Act
        var session1Task = _testHelpers!.ValidateSessionOrderingAsync(queueName, session1Commands);
        var session2Task = _testHelpers.ValidateSessionOrderingAsync(queueName, session2Commands);

        var results = await Task.WhenAll(session1Task, session2Task);

        // Assert
        Assert.True(results[0], "Session 1 commands should be processed in order");
        Assert.True(results[1], "Session 2 commands should be processed in order");
    }

    /// <summary>
    /// Test: Session state management across failures
    /// Validates: Requirements 1.2
    /// </summary>
    [Fact]
    public async Task SessionHandling_MaintainsStateAcrossFailures()
    {
        // Arrange
        var queueName = "test-commands.fifo";
        await EnsureSessionQueueExistsAsync(queueName);

        var sessionId = Guid.NewGuid().ToString();
        var commands = Enumerable.Range(1, 3)
            .Select(i => new TestCommand
            {
                Entity = new EntityRef { Id = 1 },
                Name = $"TestCommand{i}",
                Payload = new TestPayload { Data = "Sequence", Value = i }
            })
            .ToList();

        var messages = _testHelpers!.CreateSessionCommandBatch(commands, sessionId);

        // Act
        await _testHelpers.SendMessageBatchAsync(queueName, messages);

        // Create processor that abandons first message to simulate failure
        var processor = _serviceBusClient!.CreateSessionProcessor(queueName, new ServiceBusSessionProcessorOptions
        {
            MaxConcurrentSessions = 1,
            MaxConcurrentCallsPerSession = 1,
            AutoCompleteMessages = false
        });

        var processedCount = 0;
        var firstMessageAbandoned = false;

        processor.ProcessMessageAsync += async args =>
        {
            if (!firstMessageAbandoned)
            {
                firstMessageAbandoned = true;
                await args.AbandonMessageAsync(args.Message);
                return;
            }

            processedCount++;
            await args.CompleteMessageAsync(args.Message);
        };

        processor.ProcessErrorAsync += args => Task.CompletedTask;

        await processor.StartProcessingAsync();
        await Task.Delay(TimeSpan.FromSeconds(10));
        await processor.StopProcessingAsync();

        // Assert
        Assert.Equal(commands.Count, processedCount);
    }

    #endregion

    #region Duplicate Detection Tests (Requirements 1.3)

    /// <summary>
    /// Test: Automatic deduplication of identical commands
    /// Validates: Requirements 1.3
    /// </summary>
    [Fact]
    public async Task DuplicateDetection_DeduplicatesIdenticalCommands()
    {
        // Arrange
        var queueName = "test-commands-dedup";
        await EnsureDuplicateDetectionQueueExistsAsync(queueName);

        var command = new TestCommand
        {
            Entity = new EntityRef { Id = 1 },
            Name = "TestCommand",
            Payload = new TestPayload { Data = "Test data" }
        };

        // Act & Assert
        var result = await _testHelpers!.ValidateDuplicateDetectionAsync(
            queueName, 
            command, 
            sendCount: 5, 
            TimeSpan.FromSeconds(15));
        
        Assert.True(result, "Only one message should be delivered despite sending 5 duplicates");
    }

    /// <summary>
    /// Test: Duplicate detection window behavior
    /// Validates: Requirements 1.3
    /// </summary>
    [Fact]
    public async Task DuplicateDetection_RespectsDuplicationWindow()
    {
        // Arrange
        var queueName = "test-commands-dedup";
        await EnsureDuplicateDetectionQueueExistsAsync(queueName);

        var command = new TestCommand
        {
            Entity = new EntityRef { Id = 1 },
            Name = "TestCommand",
            Payload = new TestPayload { Data = "Test data" }
        };

        var message = _testHelpers!.CreateTestCommandMessage(command);
        var sender = _serviceBusClient!.CreateSender(queueName);

        try
        {
            // Act - Send first message
            await sender.SendMessageAsync(message);

            // Wait briefly and send duplicate
            await Task.Delay(TimeSpan.FromSeconds(1));
            
            var duplicateMessage = _testHelpers.CreateTestCommandMessage(command);
            duplicateMessage.MessageId = message.MessageId; // Same MessageId for deduplication
            await sender.SendMessageAsync(duplicateMessage);

            // Assert - Should receive only one message
            var receivedMessages = await _testHelpers.ReceiveMessagesAsync(
                queueName, 
                2, 
                TimeSpan.FromSeconds(10));
            
            Assert.Single(receivedMessages);
        }
        finally
        {
            await sender.DisposeAsync();
        }
    }

    /// <summary>
    /// Test: Message ID-based deduplication
    /// Validates: Requirements 1.3
    /// </summary>
    [Fact]
    public async Task DuplicateDetection_UsesMessageIdForDeduplication()
    {
        // Arrange
        var queueName = "test-commands-dedup";
        await EnsureDuplicateDetectionQueueExistsAsync(queueName);

        var command1 = new TestCommand
        {
            Entity = new EntityRef { Id = 1 },
            Name = "TestCommand1",
            Payload = new TestPayload { Data = "Data 1" }
        };

        var command2 = new TestCommand
        {
            Entity = new EntityRef { Id = 2 },
            Name = "TestCommand2",
            Payload = new TestPayload { Data = "Data 2" }
        };

        var message1 = _testHelpers!.CreateTestCommandMessage(command1);
        var message2 = _testHelpers.CreateTestCommandMessage(command2);
        message2.MessageId = message1.MessageId; // Same MessageId despite different content

        var sender = _serviceBusClient!.CreateSender(queueName);

        try
        {
            // Act
            await sender.SendMessageAsync(message1);
            await sender.SendMessageAsync(message2); // Should be deduplicated

            // Assert
            var receivedMessages = await _testHelpers.ReceiveMessagesAsync(
                queueName, 
                2, 
                TimeSpan.FromSeconds(10));
            
            Assert.Single(receivedMessages);
            Assert.Equal(message1.MessageId, receivedMessages[0].MessageId);
        }
        finally
        {
            await sender.DisposeAsync();
        }
    }

    #endregion

    #region Dead Letter Queue Tests (Requirements 1.4)

    /// <summary>
    /// Test: Failed command capture with complete metadata
    /// Validates: Requirements 1.4
    /// </summary>
    [Fact]
    public async Task DeadLetterQueue_CapturesFailedCommandsWithMetadata()
    {
        // Arrange
        var queueName = "test-commands";
        var command = new TestCommand
        {
            Entity = new EntityRef { Id = 1 },
            Name = "FailingCommand",
            Payload = new TestPayload { Data = "This will fail" }
        };

        var message = _testHelpers!.CreateTestCommandMessage(command);
        await _testHelpers.SendMessageBatchAsync(queueName, new[] { message });

        // Act - Process and explicitly dead letter the message
        var receiver = _serviceBusClient!.CreateReceiver(queueName);
        try
        {
            var receivedMessage = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(receivedMessage);

            // Dead letter with reason and description
            await receiver.DeadLetterMessageAsync(
                receivedMessage,
                deadLetterReason: "ProcessingFailed",
                deadLetterErrorDescription: "Command processing threw an exception");
        }
        finally
        {
            await receiver.DisposeAsync();
        }

        // Assert - Check dead letter queue
        var dlqReceiver = _serviceBusClient.CreateReceiver(queueName, new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter
        });

        try
        {
            var dlqMessage = await dlqReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(dlqMessage);
            Assert.Equal(message.MessageId, dlqMessage.MessageId);
            Assert.Equal("ProcessingFailed", dlqMessage.DeadLetterReason);
            Assert.Equal("Command processing threw an exception", dlqMessage.DeadLetterErrorDescription);
            Assert.True(dlqMessage.ApplicationProperties.ContainsKey("CommandType"));
            Assert.True(dlqMessage.ApplicationProperties.ContainsKey("EntityId"));
        }
        finally
        {
            await dlqReceiver.DisposeAsync();
        }
    }

    /// <summary>
    /// Test: Dead letter queue processing and resubmission
    /// Validates: Requirements 1.4
    /// </summary>
    [Fact]
    public async Task DeadLetterQueue_SupportsResubmission()
    {
        // Arrange
        var queueName = "test-commands";
        var command = new TestCommand
        {
            Entity = new EntityRef { Id = 1 },
            Name = "ResubmitCommand",
            Payload = new TestPayload { Data = "Resubmit test" }
        };

        var message = _testHelpers!.CreateTestCommandMessage(command);
        await _testHelpers.SendMessageBatchAsync(queueName, new[] { message });

        // Act - Dead letter the message
        var receiver = _serviceBusClient!.CreateReceiver(queueName);
        ServiceBusReceivedMessage? originalMessage = null;
        
        try
        {
            originalMessage = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(originalMessage);
            await receiver.DeadLetterMessageAsync(originalMessage, "TestReason", "Test resubmission");
        }
        finally
        {
            await receiver.DisposeAsync();
        }

        // Retrieve from dead letter queue and resubmit
        var dlqReceiver = _serviceBusClient.CreateReceiver(queueName, new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter
        });

        try
        {
            var dlqMessage = await dlqReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(dlqMessage);

            // Resubmit to main queue
            var resubmitMessage = new ServiceBusMessage(dlqMessage.Body)
            {
                MessageId = Guid.NewGuid().ToString(), // New MessageId for resubmission
                CorrelationId = dlqMessage.CorrelationId,
                Subject = dlqMessage.Subject,
                ContentType = dlqMessage.ContentType
            };

            foreach (var prop in dlqMessage.ApplicationProperties)
            {
                resubmitMessage.ApplicationProperties[prop.Key] = prop.Value;
            }
            resubmitMessage.ApplicationProperties["Resubmitted"] = true;
            resubmitMessage.ApplicationProperties["OriginalDeadLetterReason"] = dlqMessage.DeadLetterReason;

            var sender = _serviceBusClient.CreateSender(queueName);
            try
            {
                await sender.SendMessageAsync(resubmitMessage);
            }
            finally
            {
                await sender.DisposeAsync();
            }

            await dlqReceiver.CompleteMessageAsync(dlqMessage);
        }
        finally
        {
            await dlqReceiver.DisposeAsync();
        }

        // Assert - Verify resubmitted message is in main queue
        var finalReceiver = _serviceBusClient.CreateReceiver(queueName);
        try
        {
            var resubmittedMessage = await finalReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(resubmittedMessage);
            Assert.True(resubmittedMessage.ApplicationProperties.ContainsKey("Resubmitted"));
            Assert.Equal(true, resubmittedMessage.ApplicationProperties["Resubmitted"]);
            Assert.Equal("TestReason", resubmittedMessage.ApplicationProperties["OriginalDeadLetterReason"]);
        }
        finally
        {
            await finalReceiver.DisposeAsync();
        }
    }

    /// <summary>
    /// Test: Poison message handling
    /// Validates: Requirements 1.4
    /// </summary>
    [Fact]
    public async Task DeadLetterQueue_HandlesPoisonMessages()
    {
        // Arrange
        var queueName = "test-commands";
        var command = new TestCommand
        {
            Entity = new EntityRef { Id = 1 },
            Name = "PoisonCommand",
            Payload = new TestPayload { Data = "Poison message" }
        };

        var message = _testHelpers!.CreateTestCommandMessage(command);
        await _testHelpers.SendMessageBatchAsync(queueName, new[] { message });

        // Act - Abandon message multiple times to exceed max delivery count
        var receiver = _serviceBusClient!.CreateReceiver(queueName);
        
        try
        {
            for (int i = 0; i < 11; i++) // Default MaxDeliveryCount is 10
            {
                var receivedMessage = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
                if (receivedMessage != null)
                {
                    await receiver.AbandonMessageAsync(receivedMessage);
                }
                else
                {
                    break; // Message moved to DLQ
                }
            }
        }
        finally
        {
            await receiver.DisposeAsync();
        }

        // Assert - Message should be in dead letter queue
        var dlqReceiver = _serviceBusClient.CreateReceiver(queueName, new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter
        });

        try
        {
            var dlqMessage = await dlqReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(dlqMessage);
            Assert.Equal(message.MessageId, dlqMessage.MessageId);
            Assert.NotNull(dlqMessage.DeadLetterReason);
        }
        finally
        {
            await dlqReceiver.DisposeAsync();
        }
    }

    #endregion

    #region Helper Methods

    private async Task CreateTestQueuesAsync()
    {
        var queues = new[]
        {
            new { Name = "test-commands", RequiresSession = false, DuplicateDetection = false },
            new { Name = "test-commands.fifo", RequiresSession = true, DuplicateDetection = false },
            new { Name = "test-commands-dedup", RequiresSession = false, DuplicateDetection = true }
        };

        foreach (var queue in queues)
        {
            try
            {
                if (!await _adminClient!.QueueExistsAsync(queue.Name))
                {
                    var options = new CreateQueueOptions(queue.Name)
                    {
                        RequiresSession = queue.RequiresSession,
                        RequiresDuplicateDetection = queue.DuplicateDetection,
                        MaxDeliveryCount = 10,
                        LockDuration = TimeSpan.FromMinutes(5),
                        DefaultMessageTimeToLive = TimeSpan.FromDays(14),
                        EnableBatchedOperations = true
                    };

                    if (queue.DuplicateDetection)
                    {
                        options.DuplicateDetectionHistoryTimeWindow = TimeSpan.FromMinutes(10);
                    }

                    await _adminClient.CreateQueueAsync(options);
                    _output.WriteLine($"Created queue: {queue.Name}");
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Error creating queue {queue.Name}: {ex.Message}");
            }
        }
    }

    private async Task EnsureSessionQueueExistsAsync(string queueName)
    {
        if (!await _adminClient!.QueueExistsAsync(queueName))
        {
            var options = new CreateQueueOptions(queueName)
            {
                RequiresSession = true,
                MaxDeliveryCount = 10,
                LockDuration = TimeSpan.FromMinutes(5)
            };

            await _adminClient.CreateQueueAsync(options);
        }
    }

    private async Task EnsureDuplicateDetectionQueueExistsAsync(string queueName)
    {
        if (!await _adminClient!.QueueExistsAsync(queueName))
        {
            var options = new CreateQueueOptions(queueName)
            {
                RequiresDuplicateDetection = true,
                DuplicateDetectionHistoryTimeWindow = TimeSpan.FromMinutes(10),
                MaxDeliveryCount = 10
            };

            await _adminClient.CreateQueueAsync(options);
        }
    }

    #endregion
}





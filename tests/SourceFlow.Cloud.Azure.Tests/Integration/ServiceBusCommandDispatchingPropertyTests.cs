using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Azure.Tests.TestHelpers;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;
using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Azure.Tests.Integration;

/// <summary>
/// Property-based tests for Azure Service Bus command dispatching.
/// Feature: azure-cloud-integration-testing
/// </summary>
public class ServiceBusCommandDispatchingPropertyTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;
    private IAzureTestEnvironment? _testEnvironment;
    private ServiceBusClient? _serviceBusClient;
    private ServiceBusTestHelpers? _testHelpers;
    private ServiceBusAdministrationClient? _adminClient;

    public ServiceBusCommandDispatchingPropertyTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddDebug();
            builder.AddXUnit(output);
            builder.SetMinimumLevel(LogLevel.Information);
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

    #region Property 1: Azure Service Bus Message Routing Correctness

    /// <summary>
    /// Property 1: Azure Service Bus Message Routing Correctness
    /// 
    /// For any valid command or event and any Azure Service Bus queue or topic configuration,
    /// when a message is dispatched through Azure Service Bus, it should be routed to the
    /// correct destination and maintain all message properties including correlation IDs,
    /// session IDs, and custom metadata.
    /// 
    /// **Validates: Requirements 1.1, 2.1**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(CommandGenerators) })]
    public Property AzureServiceBusMessageRouting_RoutesToCorrectDestination_WithAllProperties(
        TestCommand command)
    {
        return Prop.ForAll(
            Arb.From(Gen.Constant(command)),
            cmd =>
            {
                try
                {
                    // Arrange
                    var queueName = "test-commands";
                    var correlationId = Guid.NewGuid().ToString();
                    var message = _testHelpers!.CreateTestCommandMessage(cmd, correlationId);
                    
                    // Add custom metadata
                    message.ApplicationProperties["CustomProperty"] = "TestValue";
                    message.ApplicationProperties["TestTimestamp"] = DateTimeOffset.UtcNow.ToString("O");

                    // Act
                    _testHelpers.SendMessageBatchAsync(queueName, new[] { message }).GetAwaiter().GetResult();

                    // Assert
                    var receivedMessages = _testHelpers.ReceiveMessagesAsync(
                        queueName, 
                        1, 
                        TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();

                    if (receivedMessages.Count != 1)
                    {
                        _output.WriteLine($"Expected 1 message, received {receivedMessages.Count}");
                        return false;
                    }

                    var received = receivedMessages[0];

                    // Verify routing - message reached correct queue
                    if (received.MessageId != message.MessageId)
                    {
                        _output.WriteLine($"Message ID mismatch: expected {message.MessageId}, got {received.MessageId}");
                        return false;
                    }

                    // Verify correlation ID preservation
                    if (received.CorrelationId != correlationId)
                    {
                        _output.WriteLine($"Correlation ID mismatch: expected {correlationId}, got {received.CorrelationId}");
                        return false;
                    }

                    // Verify session ID preservation (entity-based)
                    if (received.SessionId != cmd.Entity.ToString())
                    {
                        _output.WriteLine($"Session ID mismatch: expected {cmd.Entity}, got {received.SessionId}");
                        return false;
                    }

                    // Verify custom metadata preservation
                    if (!received.ApplicationProperties.ContainsKey("CustomProperty") ||
                        received.ApplicationProperties["CustomProperty"].ToString() != "TestValue")
                    {
                        _output.WriteLine("Custom property not preserved");
                        return false;
                    }

                    // Verify command-specific properties
                    if (!received.ApplicationProperties.ContainsKey("CommandType"))
                    {
                        _output.WriteLine("CommandType property missing");
                        return false;
                    }

                    if (!received.ApplicationProperties.ContainsKey("EntityId") ||
                        received.ApplicationProperties["EntityId"].ToString() != cmd.Entity.ToString())
                    {
                        _output.WriteLine($"EntityId mismatch: expected {cmd.Entity}, got {received.ApplicationProperties.GetValueOrDefault("EntityId")}");
                        return false;
                    }

                    _output.WriteLine($"✓ Message routing validated for command {cmd.Name}");
                    return true;
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Property test failed with exception: {ex.Message}");
                    return false;
                }
            });
    }

    #endregion

    #region Property 2: Azure Service Bus Session Ordering Preservation

    /// <summary>
    /// Property 2: Azure Service Bus Session Ordering Preservation
    /// 
    /// For any sequence of commands or events with the same session ID, when processed through
    /// Azure Service Bus, they should be received and processed in the exact order they were sent,
    /// regardless of concurrent processing of other sessions.
    /// 
    /// **Validates: Requirements 1.2, 2.5**
    /// </summary>
    [Property(MaxTest = 15, Arbitrary = new[] { typeof(CommandGenerators) })]
    public Property AzureServiceBusSessionOrdering_PreservesOrder_WithinSession(
        NonEmptyArray<TestCommand> commands)
    {
        return Prop.ForAll(
            Arb.From(Gen.Constant(commands.Get)),
            cmds =>
            {
                try
                {
                    // Arrange
                    var queueName = "test-commands.fifo";
                    var commandList = cmds.ToList();

                    // Ensure all commands have the same entity for session ordering
                    var sessionEntity = new EntityRef { Id = 1 };
                    foreach (var cmd in commandList)
                    {
                        cmd.Entity = sessionEntity;
                    }

                    // Act & Assert
                    var result = _testHelpers!.ValidateSessionOrderingAsync(
                        queueName, 
                        commandList.Cast<ICommand>().ToList(), 
                        TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();

                    if (!result)
                    {
                        _output.WriteLine($"Session ordering validation failed for {commandList.Count} commands");
                        return false;
                    }

                    _output.WriteLine($"✓ Session ordering preserved for {commandList.Count} commands");
                    return true;
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Property test failed with exception: {ex.Message}");
                    return false;
                }
            });
    }

    #endregion

    #region Property 3: Azure Service Bus Duplicate Detection Effectiveness

    /// <summary>
    /// Property 3: Azure Service Bus Duplicate Detection Effectiveness
    /// 
    /// For any command or event sent multiple times with the same message ID within the duplicate
    /// detection window, Azure Service Bus should automatically deduplicate and deliver only one
    /// instance to consumers.
    /// 
    /// **Validates: Requirements 1.3**
    /// </summary>
    [Property(MaxTest = 15, Arbitrary = new[] { typeof(CommandGenerators) })]
    public Property AzureServiceBusDuplicateDetection_DeduplicatesMessages_WithinWindow(
        TestCommand command,
        PositiveInt sendCount)
    {
        return Prop.ForAll(
            Arb.From(Gen.Constant((command, Math.Min(sendCount.Get, 10)))), // Limit to 10 sends
            tuple =>
            {
                try
                {
                    // Arrange
                    var (cmd, count) = tuple;
                    var queueName = "test-commands-dedup";

                    // Ensure at least 2 sends for duplicate detection
                    var actualSendCount = Math.Max(2, count);

                    // Act & Assert
                    var result = _testHelpers!.ValidateDuplicateDetectionAsync(
                        queueName,
                        cmd,
                        actualSendCount,
                        TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();

                    if (!result)
                    {
                        _output.WriteLine($"Duplicate detection failed: sent {actualSendCount} duplicates but received more than 1");
                        return false;
                    }

                    _output.WriteLine($"✓ Duplicate detection validated: sent {actualSendCount}, received 1");
                    return true;
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Property test failed with exception: {ex.Message}");
                    return false;
                }
            });
    }

    #endregion

    #region Property 12: Azure Dead Letter Queue Handling Completeness

    /// <summary>
    /// Property 12: Azure Dead Letter Queue Handling Completeness
    /// 
    /// For any message that fails processing in Azure Service Bus, it should be captured in the
    /// appropriate dead letter queue with complete failure metadata including error details,
    /// retry count, and original message properties.
    /// 
    /// **Validates: Requirements 1.4**
    /// </summary>
    [Property(MaxTest = 15, Arbitrary = new[] { typeof(CommandGenerators) })]
    public Property AzureDeadLetterQueue_CapturesFailedMessages_WithCompleteMetadata(
        TestCommand command)
    {
        return Prop.ForAll(
            Arb.From(Gen.Constant(command)),
            cmd =>
            {
                try
                {
                    // Arrange
                    var queueName = "test-commands";
                    var message = _testHelpers!.CreateTestCommandMessage(cmd);
                    var deadLetterReason = "PropertyTestFailure";
                    var deadLetterDescription = $"Testing dead letter handling for command {cmd.Name}";

                    // Act - Send message and explicitly dead letter it
                    _testHelpers.SendMessageBatchAsync(queueName, new[] { message }).GetAwaiter().GetResult();

                    var receiver = _serviceBusClient!.CreateReceiver(queueName);
                    ServiceBusReceivedMessage? receivedMessage = null;
                    
                    try
                    {
                        receivedMessage = receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                        if (receivedMessage == null)
                        {
                            _output.WriteLine("Failed to receive message from main queue");
                            return false;
                        }

                        // Dead letter the message with metadata
                        receiver.DeadLetterMessageAsync(
                            receivedMessage,
                            deadLetterReason,
                            deadLetterDescription).GetAwaiter().GetResult();
                    }
                    finally
                    {
                        receiver.DisposeAsync().GetAwaiter().GetResult();
                    }

                    // Assert - Verify message is in dead letter queue with complete metadata
                    var dlqReceiver = _serviceBusClient.CreateReceiver(queueName, new ServiceBusReceiverOptions
                    {
                        SubQueue = SubQueue.DeadLetter
                    });

                    try
                    {
                        var dlqMessage = dlqReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                        
                        if (dlqMessage == null)
                        {
                            _output.WriteLine("Message not found in dead letter queue");
                            return false;
                        }

                        // Verify original message ID preserved
                        if (dlqMessage.MessageId != message.MessageId)
                        {
                            _output.WriteLine($"Message ID mismatch in DLQ: expected {message.MessageId}, got {dlqMessage.MessageId}");
                            return false;
                        }

                        // Verify dead letter reason
                        if (dlqMessage.DeadLetterReason != deadLetterReason)
                        {
                            _output.WriteLine($"Dead letter reason mismatch: expected {deadLetterReason}, got {dlqMessage.DeadLetterReason}");
                            return false;
                        }

                        // Verify dead letter description
                        if (dlqMessage.DeadLetterErrorDescription != deadLetterDescription)
                        {
                            _output.WriteLine($"Dead letter description mismatch");
                            return false;
                        }

                        // Verify original properties preserved
                        if (!dlqMessage.ApplicationProperties.ContainsKey("CommandType"))
                        {
                            _output.WriteLine("CommandType property not preserved in DLQ");
                            return false;
                        }

                        if (!dlqMessage.ApplicationProperties.ContainsKey("EntityId"))
                        {
                            _output.WriteLine("EntityId property not preserved in DLQ");
                            return false;
                        }

                        // Complete the DLQ message to clean up
                        dlqReceiver.CompleteMessageAsync(dlqMessage).GetAwaiter().GetResult();

                        _output.WriteLine($"✓ Dead letter queue handling validated for command {cmd.Name}");
                        return true;
                    }
                    finally
                    {
                        dlqReceiver.DisposeAsync().GetAwaiter().GetResult();
                    }
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Property test failed with exception: {ex.Message}");
                    return false;
                }
            });
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
                        DeadLetteringOnMessageExpiration = true,
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

    #endregion
}

/// <summary>
/// FsCheck generators for test commands.
/// </summary>
public static class CommandGenerators
{
    /// <summary>
    /// Generates arbitrary test commands for property-based testing.
    /// </summary>
    public static Arbitrary<TestCommand> TestCommand()
    {
        var commandGen = from entityId in Gen.Choose(1, 1000)
                        from name in Gen.Elements("CreateOrder", "UpdateOrder", "CancelOrder", "ProcessPayment", "AdjustInventory")
                        from dataValue in Gen.Choose(1, 100)
                        select new TestCommand
                        {
                            Entity = new EntityRef { Id = entityId },
                            Name = name,
                            Payload = new TestPayload 
                            { 
                                Data = $"Test data {dataValue}", 
                                Value = dataValue 
                            },
                            Metadata = new Metadata
                            {
                                Properties = new Dictionary<string, object>
                                {
                                    ["CorrelationId"] = Guid.NewGuid().ToString(),
                                    ["Timestamp"] = DateTimeOffset.UtcNow.ToString("O")
                                }
                            }
                        };

        return Arb.From(commandGen);
    }

    /// <summary>
    /// Generates non-empty arrays of test commands for batch testing.
    /// </summary>
    public static Arbitrary<NonEmptyArray<TestCommand>> TestCommandBatch()
    {
        var batchGen = from count in Gen.Choose(2, 10)
                      from commands in Gen.ListOf(count, TestCommand().Generator)
                      select NonEmptyArray<TestCommand>.NewNonEmptyArray(commands.ToArray());

        return Arb.From(batchGen);
    }
}

/// <summary>
/// Test command for property-based testing.
/// </summary>
public class TestCommand : ICommand
{
    public EntityRef Entity { get; set; } = new EntityRef { Id = 1 };
    public string Name { get; set; } = string.Empty;
    public IPayload Payload { get; set; } = new TestPayload();
    public Metadata Metadata { get; set; } = new Metadata();
}

/// <summary>
/// Test payload for property-based testing.
/// </summary>
public class TestPayload : IPayload
{
    public string Data { get; set; } = string.Empty;
    public int Value { get; set; }
}

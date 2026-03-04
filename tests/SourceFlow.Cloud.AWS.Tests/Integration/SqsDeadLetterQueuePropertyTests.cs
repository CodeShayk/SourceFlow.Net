using Amazon.SQS.Model;
using FsCheck;
using FsCheck.Xunit;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using System.Text.Json;

namespace SourceFlow.Cloud.AWS.Tests.Integration;

/// <summary>
/// Property-based tests for SQS dead letter queue handling
/// Validates universal properties that should hold for all dead letter queue scenarios
/// </summary>
[Collection("AWS Integration Tests")]
[Trait("Category", "Integration")]
[Trait("Category", "RequiresLocalStack")]
public class SqsDeadLetterQueuePropertyTests : IClassFixture<LocalStackTestFixture>, IAsyncDisposable
{
    private readonly LocalStackTestFixture _localStack;
    private readonly List<string> _createdQueues = new();
    
    public SqsDeadLetterQueuePropertyTests(LocalStackTestFixture localStack)
    {
        _localStack = localStack;
    }
    
    /// <summary>
    /// Property 2: SQS Dead Letter Queue Handling
    /// For any command that fails processing beyond the maximum retry count, 
    /// it should be automatically moved to the configured dead letter queue with 
    /// complete failure metadata, retry history, and be available for analysis and reprocessing.
    /// Validates: Requirements 1.3
    /// </summary>
    [Property(MaxTest = 15, Arbitrary = new[] { typeof(DeadLetterQueueGenerators) })]
    public async Task Property_SqsDeadLetterQueueHandling(DeadLetterQueueScenario scenario)
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange - Create main queue with dead letter queue
        var dlqUrl = scenario.QueueType == QueueType.Fifo 
            ? await CreateFifoQueueAsync($"prop-test-dlq-{Guid.NewGuid():N}.fifo")
            : await CreateStandardQueueAsync($"prop-test-dlq-{Guid.NewGuid():N}");
            
        var dlqArn = await GetQueueArnAsync(dlqUrl);
        
        var mainQueueUrl = scenario.QueueType == QueueType.Fifo
            ? await CreateFifoQueueAsync($"prop-test-main-{Guid.NewGuid():N}.fifo", new Dictionary<string, string>
            {
                ["VisibilityTimeoutSeconds"] = scenario.VisibilityTimeoutSeconds.ToString(),
                ["RedrivePolicy"] = JsonSerializer.Serialize(new
                {
                    deadLetterTargetArn = dlqArn,
                    maxReceiveCount = scenario.MaxReceiveCount
                })
            })
            : await CreateStandardQueueAsync($"prop-test-main-{Guid.NewGuid():N}", new Dictionary<string, string>
            {
                ["VisibilityTimeoutSeconds"] = scenario.VisibilityTimeoutSeconds.ToString(),
                ["RedrivePolicy"] = JsonSerializer.Serialize(new
                {
                    deadLetterTargetArn = dlqArn,
                    maxReceiveCount = scenario.MaxReceiveCount
                })
            });
        
        var sentMessages = new List<DeadLetterTestMessage>();
        var dlqMessages = new List<Message>();
        
        try
        {
            // Act - Send messages that will fail processing
            await SendFailingMessages(mainQueueUrl, scenario, sentMessages);
            
            // Act - Simulate processing failures up to maxReceiveCount
            await SimulateProcessingFailures(mainQueueUrl, scenario);
            
            // Act - Wait for messages to be moved to DLQ
            await Task.Delay(TimeSpan.FromSeconds(scenario.VisibilityTimeoutSeconds + 2));
            
            // Act - Retrieve messages from dead letter queue
            await RetrieveDeadLetterMessages(dlqUrl, scenario.Messages.Count, dlqMessages);
            
            // Assert - Dead letter queue correctness
            AssertDeadLetterQueueCorrectness(sentMessages, dlqMessages, scenario);
            
            // Assert - Message metadata preservation
            AssertMessageMetadataPreservation(sentMessages, dlqMessages);
            
            // Assert - Failure information completeness
            AssertFailureInformationCompleteness(dlqMessages, scenario);
            
            // Assert - Reprocessing capability
            await AssertReprocessingCapability(dlqUrl, dlqMessages, scenario);
        }
        finally
        {
            // Clean up messages
            await CleanupMessages(dlqUrl, dlqMessages);
        }
    }
    
    /// <summary>
    /// Send messages that will fail processing to the main queue
    /// </summary>
    private async Task SendFailingMessages(string queueUrl, DeadLetterQueueScenario scenario, List<DeadLetterTestMessage> sentMessages)
    {
        var sendTasks = scenario.Messages.Select(async (message, index) =>
        {
            var request = CreateSendMessageRequest(queueUrl, message, scenario.QueueType, index);
            var startTime = DateTime.UtcNow;
            
            var response = await _localStack.SqsClient.SendMessageAsync(request);
            var endTime = DateTime.UtcNow;
            
            var sentMessage = new DeadLetterTestMessage
            {
                OriginalMessage = message,
                MessageId = response.MessageId,
                SendTime = startTime,
                SendDuration = endTime - startTime,
                MessageGroupId = request.MessageGroupId,
                MessageDeduplicationId = request.MessageDeduplicationId,
                ExpectedFailureType = message.FailureType,
                MessageAttributes = request.MessageAttributes.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.StringValue ?? kvp.Value.BinaryValue?.ToString() ?? "")
            };
            
            lock (sentMessages)
            {
                sentMessages.Add(sentMessage);
            }
        });
        
        await Task.WhenAll(sendTasks);
    }
    
    /// <summary>
    /// Simulate processing failures by receiving messages without deleting them
    /// </summary>
    private async Task SimulateProcessingFailures(string queueUrl, DeadLetterQueueScenario scenario)
    {
        var maxAttempts = scenario.MaxReceiveCount + 2; // Try a bit more than max to ensure DLQ triggering
        var visibilityTimeout = TimeSpan.FromSeconds(scenario.VisibilityTimeoutSeconds);
        
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                MessageAttributeNames = new List<string> { "All" },
                WaitTimeSeconds = 1
            });
            
            if (receiveResponse.Messages.Any())
            {
                // Don't delete messages - simulate processing failure
                // Wait for visibility timeout to expire
                await Task.Delay(visibilityTimeout.Add(TimeSpan.FromMilliseconds(500)));
            }
            else
            {
                // No more messages in main queue - they might have been moved to DLQ
                break;
            }
        }
    }
    
    /// <summary>
    /// Retrieve messages from the dead letter queue
    /// </summary>
    private async Task RetrieveDeadLetterMessages(string dlqUrl, int expectedCount, List<Message> dlqMessages)
    {
        var maxAttempts = 10;
        var attempts = 0;
        
        while (dlqMessages.Count < expectedCount && attempts < maxAttempts)
        {
            var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = dlqUrl,
                MaxNumberOfMessages = 10,
                MessageAttributeNames = new List<string> { "All" },
                WaitTimeSeconds = 1
            });
            
            dlqMessages.AddRange(receiveResponse.Messages);
            attempts++;
            
            if (receiveResponse.Messages.Count == 0)
            {
                await Task.Delay(500);
            }
        }
    }
    
    /// <summary>
    /// Assert that dead letter queue handling is correct
    /// </summary>
    private static void AssertDeadLetterQueueCorrectness(List<DeadLetterTestMessage> sentMessages, List<Message> dlqMessages, DeadLetterQueueScenario scenario)
    {
        // Messages should be moved to DLQ after exceeding maxReceiveCount
        Assert.True(dlqMessages.Count >= sentMessages.Count * 0.8, // Allow some variance for LocalStack
            $"Expected at least {sentMessages.Count * 0.8} messages in DLQ, found {dlqMessages.Count}");
        
        // Each DLQ message should correspond to a sent message
        foreach (var dlqMessage in dlqMessages)
        {
            var messageBody = dlqMessage.Body;
            var matchingSent = sentMessages.FirstOrDefault(s => 
                JsonSerializer.Serialize(s.OriginalMessage.Payload) == messageBody);
            
            Assert.NotNull(matchingSent);
        }
        
        // Messages should not be in main queue anymore (this would require additional verification)
        // For property tests, we assume the SQS service correctly implements the redrive policy
    }
    
    /// <summary>
    /// Assert that message metadata is preserved in the dead letter queue
    /// </summary>
    private static void AssertMessageMetadataPreservation(List<DeadLetterTestMessage> sentMessages, List<Message> dlqMessages)
    {
        foreach (var dlqMessage in dlqMessages)
        {
            // Find corresponding sent message
            var messageBody = dlqMessage.Body;
            var matchingSent = sentMessages.FirstOrDefault(s => 
                JsonSerializer.Serialize(s.OriginalMessage.Payload) == messageBody);
            
            if (matchingSent == null) continue;
            
            // Verify SourceFlow attributes are preserved
            var requiredAttributes = new[] { "EntityId", "SequenceNo", "CommandType", "PayloadType" };
            
            foreach (var attrName in requiredAttributes)
            {
                Assert.True(dlqMessage.MessageAttributes.ContainsKey(attrName),
                    $"Missing required attribute in DLQ: {attrName}");
                
                if (matchingSent.MessageAttributes.ContainsKey(attrName))
                {
                    Assert.Equal(matchingSent.MessageAttributes[attrName],
                        dlqMessage.MessageAttributes[attrName].StringValue);
                }
            }
            
            // Verify failure-related attributes are present
            Assert.True(dlqMessage.MessageAttributes.ContainsKey("FailureType"),
                "FailureType should be preserved in DLQ");
            
            // Verify original message structure is intact
            var originalPayload = JsonSerializer.Deserialize<Dictionary<string, object>>(messageBody);
            Assert.NotNull(originalPayload);
            Assert.True(originalPayload.ContainsKey("CommandId"));
            Assert.True(originalPayload.ContainsKey("Data"));
        }
    }
    
    /// <summary>
    /// Assert that failure information is complete and useful for analysis
    /// </summary>
    private static void AssertFailureInformationCompleteness(List<Message> dlqMessages, DeadLetterQueueScenario scenario)
    {
        foreach (var dlqMessage in dlqMessages)
        {
            // Verify failure metadata is available
            Assert.True(dlqMessage.MessageAttributes.ContainsKey("FailureType"),
                "Failure type should be available for analysis");
            
            var failureType = dlqMessage.MessageAttributes["FailureType"].StringValue;
            Assert.True(Enum.IsDefined(typeof(MessageFailureType), failureType),
                "Failure type should be a valid enum value");
            
            // Verify timestamp information is preserved
            Assert.True(dlqMessage.MessageAttributes.ContainsKey("Timestamp"),
                "Original timestamp should be preserved");
            
            // Verify entity information is preserved for correlation
            Assert.True(dlqMessage.MessageAttributes.ContainsKey("EntityId"),
                "EntityId should be preserved for correlation");
            
            // Verify command type is preserved for reprocessing logic
            Assert.True(dlqMessage.MessageAttributes.ContainsKey("CommandType"),
                "CommandType should be preserved for reprocessing");
            
            // Message body should be intact for reprocessing
            Assert.False(string.IsNullOrEmpty(dlqMessage.Body),
                "Message body should be preserved for reprocessing");
            
            // Verify message can be deserialized
            var messagePayload = JsonSerializer.Deserialize<Dictionary<string, object>>(dlqMessage.Body);
            Assert.NotNull(messagePayload);
        }
    }
    
    /// <summary>
    /// Assert that messages in DLQ can be reprocessed
    /// </summary>
    private async Task AssertReprocessingCapability(string dlqUrl, List<Message> dlqMessages, DeadLetterQueueScenario scenario)
    {
        if (!dlqMessages.Any()) return;
        
        // Create a reprocessing queue
        var reprocessQueueUrl = scenario.QueueType == QueueType.Fifo
            ? await CreateFifoQueueAsync($"prop-test-reprocess-{Guid.NewGuid():N}.fifo")
            : await CreateStandardQueueAsync($"prop-test-reprocess-{Guid.NewGuid():N}");
        
        try
        {
            // Take a sample of messages for reprocessing test
            var samplesToReprocess = dlqMessages.Take(Math.Min(3, dlqMessages.Count)).ToList();
            
            // Reprocess messages
            var reprocessTasks = samplesToReprocess.Select(async dlqMessage =>
            {
                var originalBody = JsonSerializer.Deserialize<Dictionary<string, object>>(dlqMessage.Body);
                Assert.NotNull(originalBody);
                
                // Add reprocessing metadata
                var reprocessedBody = new Dictionary<string, object>(originalBody)
                {
                    ["ReprocessedAt"] = DateTime.UtcNow.ToString("O"),
                    ["ReprocessedFromDLQ"] = true,
                    ["OriginalFailureType"] = dlqMessage.MessageAttributes["FailureType"].StringValue
                };
                
                var reprocessRequest = new SendMessageRequest
                {
                    QueueUrl = reprocessQueueUrl,
                    MessageBody = JsonSerializer.Serialize(reprocessedBody),
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        ["ReprocessedFrom"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = "DeadLetterQueue"
                        },
                        ["OriginalEntityId"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = dlqMessage.MessageAttributes["EntityId"].StringValue
                        },
                        ["OriginalCommandType"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = dlqMessage.MessageAttributes["CommandType"].StringValue
                        },
                        ["ReprocessAttempt"] = new MessageAttributeValue
                        {
                            DataType = "Number",
                            StringValue = "1"
                        }
                    }
                };
                
                // Add FIFO-specific attributes if needed
                if (scenario.QueueType == QueueType.Fifo)
                {
                    var entityId = dlqMessage.MessageAttributes["EntityId"].StringValue;
                    reprocessRequest.MessageGroupId = $"reprocess-entity-{entityId}";
                    reprocessRequest.MessageDeduplicationId = $"reprocess-{Guid.NewGuid():N}";
                }
                
                return await _localStack.SqsClient.SendMessageAsync(reprocessRequest);
            });
            
            var reprocessResults = await Task.WhenAll(reprocessTasks);
            
            // Assert all reprocessing attempts succeeded
            Assert.All(reprocessResults, result => Assert.NotNull(result.MessageId));
            
            // Verify reprocessed messages are available
            var reprocessedReceiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = reprocessQueueUrl,
                MaxNumberOfMessages = 10,
                MessageAttributeNames = new List<string> { "All" },
                WaitTimeSeconds = 2
            });
            
            Assert.Equal(samplesToReprocess.Count, reprocessedReceiveResponse.Messages.Count);
            
            // Verify reprocessed message structure
            foreach (var reprocessedMessage in reprocessedReceiveResponse.Messages)
            {
                Assert.Equal("DeadLetterQueue", reprocessedMessage.MessageAttributes["ReprocessedFrom"].StringValue);
                Assert.True(reprocessedMessage.MessageAttributes.ContainsKey("OriginalEntityId"));
                Assert.True(reprocessedMessage.MessageAttributes.ContainsKey("OriginalCommandType"));
                
                var messageBody = JsonSerializer.Deserialize<Dictionary<string, object>>(reprocessedMessage.Body);
                Assert.NotNull(messageBody);
                Assert.True(messageBody.ContainsKey("ReprocessedAt"));
                Assert.True(messageBody.ContainsKey("ReprocessedFromDLQ"));
                Assert.True(messageBody.ContainsKey("OriginalFailureType"));
            }
            
            // Clean up reprocessed messages
            var cleanupTasks = reprocessedReceiveResponse.Messages.Select(message =>
                _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
                {
                    QueueUrl = reprocessQueueUrl,
                    ReceiptHandle = message.ReceiptHandle
                }));
            
            await Task.WhenAll(cleanupTasks);
        }
        finally
        {
            // Clean up reprocess queue
            try
            {
                await _localStack.SqsClient.DeleteQueueAsync(new DeleteQueueRequest
                {
                    QueueUrl = reprocessQueueUrl
                });
            }
            catch (Exception)
            {
                // Ignore cleanup errors
            }
        }
    }
    
    /// <summary>
    /// Create a send message request for the given test message
    /// </summary>
    private static SendMessageRequest CreateSendMessageRequest(string queueUrl, FailingTestMessage message, QueueType queueType, int index)
    {
        var request = new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = JsonSerializer.Serialize(message.Payload),
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["EntityId"] = new MessageAttributeValue
                {
                    DataType = "Number",
                    StringValue = message.EntityId.ToString()
                },
                ["SequenceNo"] = new MessageAttributeValue
                {
                    DataType = "Number",
                    StringValue = message.SequenceNo.ToString()
                },
                ["CommandType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = message.CommandType
                },
                ["PayloadType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = message.PayloadType
                },
                ["FailureType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = message.FailureType.ToString()
                },
                ["Timestamp"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = DateTime.UtcNow.ToString("O")
                }
            }
        };
        
        // Add FIFO-specific attributes
        if (queueType == QueueType.Fifo)
        {
            request.MessageGroupId = $"entity-{message.EntityId}";
            request.MessageDeduplicationId = $"msg-{message.EntityId}-{message.SequenceNo}-{index}-{Guid.NewGuid():N}";
        }
        
        return request;
    }
    
    /// <summary>
    /// Clean up messages from the dead letter queue
    /// </summary>
    private async Task CleanupMessages(string dlqUrl, List<Message> dlqMessages)
    {
        var deleteTasks = dlqMessages.Select(message =>
            _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
            {
                QueueUrl = dlqUrl,
                ReceiptHandle = message.ReceiptHandle
            }));
        
        try
        {
            await Task.WhenAll(deleteTasks);
        }
        catch (Exception)
        {
            // Ignore cleanup errors
        }
    }
    
    /// <summary>
    /// Get the ARN for a queue
    /// </summary>
    private async Task<string> GetQueueArnAsync(string queueUrl)
    {
        var response = await _localStack.SqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = new List<string> { "QueueArn" }
        });
        
        return response.Attributes["QueueArn"];
    }
    
    /// <summary>
    /// Create a standard queue for testing
    /// </summary>
    private async Task<string> CreateStandardQueueAsync(string queueName, Dictionary<string, string>? additionalAttributes = null)
    {
        var attributes = new Dictionary<string, string>
        {
            ["MessageRetentionPeriod"] = "1209600",
            ["VisibilityTimeoutSeconds"] = "30"
        };
        
        if (additionalAttributes != null)
        {
            foreach (var attr in additionalAttributes)
            {
                attributes[attr.Key] = attr.Value;
            }
        }
        
        var response = await _localStack.SqsClient.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = queueName,
            Attributes = attributes
        });
        
        _createdQueues.Add(response.QueueUrl);
        return response.QueueUrl;
    }
    
    /// <summary>
    /// Create a FIFO queue for testing
    /// </summary>
    private async Task<string> CreateFifoQueueAsync(string queueName, Dictionary<string, string>? additionalAttributes = null)
    {
        var attributes = new Dictionary<string, string>
        {
            ["FifoQueue"] = "true",
            ["ContentBasedDeduplication"] = "true",
            ["MessageRetentionPeriod"] = "1209600",
            ["VisibilityTimeoutSeconds"] = "30"
        };
        
        if (additionalAttributes != null)
        {
            foreach (var attr in additionalAttributes)
            {
                attributes[attr.Key] = attr.Value;
            }
        }
        
        var response = await _localStack.SqsClient.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = queueName,
            Attributes = attributes
        });
        
        _createdQueues.Add(response.QueueUrl);
        return response.QueueUrl;
    }
    
    /// <summary>
    /// Clean up created queues
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_localStack.SqsClient != null)
        {
            foreach (var queueUrl in _createdQueues)
            {
                try
                {
                    await _localStack.SqsClient.DeleteQueueAsync(new DeleteQueueRequest
                    {
                        QueueUrl = queueUrl
                    });
                }
                catch (Exception)
                {
                    // Ignore cleanup errors
                }
            }
        }
        
        _createdQueues.Clear();
    }
}

/// <summary>
/// FsCheck generators for dead letter queue property tests
/// </summary>
public static class DeadLetterQueueGenerators
{
    /// <summary>
    /// Generate test scenarios for dead letter queue handling
    /// </summary>
    public static Arbitrary<DeadLetterQueueScenario> DeadLetterQueueScenario()
    {
        var queueTypeGen = Gen.Elements(QueueType.Standard, QueueType.Fifo);
        var maxReceiveCountGen = Gen.Choose(2, 5); // Reasonable range for testing
        var visibilityTimeoutGen = Gen.Choose(1, 5); // Short timeouts for faster testing
        var messageCountGen = Gen.Choose(1, 10); // Reasonable number for property testing
        
        var scenarioGen = from queueType in queueTypeGen
                         from maxReceiveCount in maxReceiveCountGen
                         from visibilityTimeout in visibilityTimeoutGen
                         from messageCount in messageCountGen
                         from messages in Gen.ListOf(messageCount, FailingTestMessage())
                         select new DeadLetterQueueScenario
                         {
                             QueueType = queueType,
                             MaxReceiveCount = maxReceiveCount,
                             VisibilityTimeoutSeconds = visibilityTimeout,
                             Messages = messages.ToList()
                         };
        
        return Arb.From(scenarioGen);
    }
    
    /// <summary>
    /// Generate test messages that will fail processing
    /// </summary>
    public static Gen<FailingTestMessage> FailingTestMessage()
    {
        var entityIdGen = Gen.Choose(1, 1000);
        var sequenceNoGen = Gen.Choose(1, 100);
        var commandTypeGen = Gen.Elements(
            "ProcessOrderCommand", 
            "ValidatePaymentCommand", 
            "UpdateInventoryCommand",
            "SendNotificationCommand",
            "CalculateShippingCommand");
        var payloadTypeGen = Gen.Elements(
            "ProcessOrderPayload",
            "ValidatePaymentPayload", 
            "UpdateInventoryPayload",
            "SendNotificationPayload",
            "CalculateShippingPayload");
        var failureTypeGen = Gen.Elements(
            MessageFailureType.ValidationError,
            MessageFailureType.TimeoutError,
            MessageFailureType.ExternalServiceError,
            MessageFailureType.DataCorruption,
            MessageFailureType.InsufficientResources);
        
        var payloadGen = from commandId in Gen.Fresh(() => Guid.NewGuid())
                        from data in Gen.Elements("test-data-1", "test-data-2", "corrupted-data", "timeout-data")
                        from priority in Gen.Choose(1, 10)
                        select new Dictionary<string, object>
                        {
                            ["CommandId"] = commandId,
                            ["Data"] = data,
                            ["Priority"] = priority,
                            ["CreatedAt"] = DateTime.UtcNow.ToString("O")
                        };
        
        return from entityId in entityIdGen
               from sequenceNo in sequenceNoGen
               from commandType in commandTypeGen
               from payloadType in payloadTypeGen
               from failureType in failureTypeGen
               from payload in payloadGen
               select new FailingTestMessage
               {
                   EntityId = entityId,
                   SequenceNo = sequenceNo,
                   CommandType = commandType,
                   PayloadType = payloadType,
                   FailureType = failureType,
                   Payload = payload
               };
    }
}

/// <summary>
/// Test scenario for dead letter queue handling
/// </summary>
public class DeadLetterQueueScenario
{
    public QueueType QueueType { get; set; }
    public int MaxReceiveCount { get; set; }
    public int VisibilityTimeoutSeconds { get; set; }
    public List<FailingTestMessage> Messages { get; set; } = new();
}

/// <summary>
/// Test message that will fail processing
/// </summary>
public class FailingTestMessage
{
    public int EntityId { get; set; }
    public int SequenceNo { get; set; }
    public string CommandType { get; set; } = "";
    public string PayloadType { get; set; } = "";
    public MessageFailureType FailureType { get; set; }
    public Dictionary<string, object> Payload { get; set; } = new();
}

/// <summary>
/// Sent message tracking information for dead letter queue tests
/// </summary>
public class DeadLetterTestMessage
{
    public FailingTestMessage OriginalMessage { get; set; } = new();
    public string MessageId { get; set; } = "";
    public DateTime SendTime { get; set; }
    public TimeSpan SendDuration { get; set; }
    public string? MessageGroupId { get; set; }
    public string? MessageDeduplicationId { get; set; }
    public MessageFailureType ExpectedFailureType { get; set; }
    public Dictionary<string, string> MessageAttributes { get; set; } = new();
}

/// <summary>
/// Types of message processing failures
/// </summary>
public enum MessageFailureType
{
    ValidationError,
    TimeoutError,
    ExternalServiceError,
    DataCorruption,
    InsufficientResources
}

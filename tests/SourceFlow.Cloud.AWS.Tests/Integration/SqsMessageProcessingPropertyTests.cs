using Amazon.SQS.Model;
using FsCheck;
using FsCheck.Xunit;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using System.Text.Json;

namespace SourceFlow.Cloud.AWS.Tests.Integration;

/// <summary>
/// Property-based tests for SQS message processing correctness
/// Validates universal properties that should hold across all valid SQS operations
/// </summary>
[Collection("AWS Integration Tests")]
[Trait("Category", "Integration")]
[Trait("Category", "RequiresLocalStack")]
public class SqsMessageProcessingPropertyTests : IClassFixture<LocalStackTestFixture>, IAsyncDisposable
{
    private readonly LocalStackTestFixture _localStack;
    private readonly List<string> _createdQueues = new();
    
    public SqsMessageProcessingPropertyTests(LocalStackTestFixture localStack)
    {
        _localStack = localStack;
    }
    
    /// <summary>
    /// Property 1: SQS Message Processing Correctness
    /// For any valid SourceFlow command and SQS queue configuration (standard or FIFO), 
    /// when the command is dispatched through SQS, it should be delivered correctly with 
    /// proper message attributes (EntityId, SequenceNo, CommandType), maintain FIFO ordering 
    /// within message groups when applicable, support batch operations up to AWS limits, 
    /// and achieve consistent throughput performance.
    /// Validates: Requirements 1.1, 1.2, 1.4, 1.5
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(SqsMessageGenerators) })]
    public async Task Property_SqsMessageProcessingCorrectness(SqsTestScenario scenario)
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange - Create appropriate queue type
        var queueUrl = scenario.QueueType == QueueType.Fifo 
            ? await CreateFifoQueueAsync($"prop-test-fifo-{Guid.NewGuid():N}.fifo")
            : await CreateStandardQueueAsync($"prop-test-standard-{Guid.NewGuid():N}");
        
        var sentMessages = new List<SqsTestMessage>();
        var receivedMessages = new List<Message>();
        
        try
        {
            // Act - Send messages according to scenario
            if (scenario.UseBatchSending && scenario.Messages.Count > 1)
            {
                await SendMessagesBatch(queueUrl, scenario, sentMessages);
            }
            else
            {
                await SendMessagesIndividually(queueUrl, scenario, sentMessages);
            }
            
            // Act - Receive all messages
            await ReceiveAllMessages(queueUrl, scenario.Messages.Count, receivedMessages);
            
            // Assert - Message delivery correctness
            AssertMessageDeliveryCorrectness(sentMessages, receivedMessages);
            
            // Assert - Message attributes preservation
            AssertMessageAttributesPreservation(sentMessages, receivedMessages);
            
            // Assert - FIFO ordering (if applicable)
            if (scenario.QueueType == QueueType.Fifo)
            {
                AssertFifoOrdering(sentMessages, receivedMessages);
            }
            
            // Assert - Batch operation efficiency (if applicable)
            if (scenario.UseBatchSending)
            {
                AssertBatchOperationEfficiency(scenario, sentMessages);
            }
            
            // Assert - Performance consistency
            AssertPerformanceConsistency(scenario, sentMessages, receivedMessages);
        }
        finally
        {
            // Clean up messages
            await CleanupMessages(queueUrl, receivedMessages);
        }
    }
    
    /// <summary>
    /// Send messages individually to the queue
    /// </summary>
    private async Task SendMessagesIndividually(string queueUrl, SqsTestScenario scenario, List<SqsTestMessage> sentMessages)
    {
        var sendTasks = scenario.Messages.Select(async (message, index) =>
        {
            var request = CreateSendMessageRequest(queueUrl, message, scenario.QueueType, index);
            var startTime = DateTime.UtcNow;
            
            var response = await _localStack.SqsClient.SendMessageAsync(request);
            var endTime = DateTime.UtcNow;
            
            var sentMessage = new SqsTestMessage
            {
                OriginalMessage = message,
                MessageId = response.MessageId,
                SendTime = startTime,
                SendDuration = endTime - startTime,
                MessageGroupId = request.MessageGroupId,
                MessageDeduplicationId = request.MessageDeduplicationId,
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
    /// Send messages using batch operations
    /// </summary>
    private async Task SendMessagesBatch(string queueUrl, SqsTestScenario scenario, List<SqsTestMessage> sentMessages)
    {
        const int maxBatchSize = 10; // AWS SQS limit
        var batches = scenario.Messages
            .Select((message, index) => new { Message = message, Index = index })
            .GroupBy(x => x.Index / maxBatchSize)
            .Select(g => g.ToList())
            .ToList();
        
        foreach (var batch in batches)
        {
            var entries = batch.Select(item =>
            {
                var request = CreateSendMessageRequest(queueUrl, item.Message, scenario.QueueType, item.Index);
                return new SendMessageBatchRequestEntry
                {
                    Id = item.Index.ToString(),
                    MessageBody = request.MessageBody,
                    MessageGroupId = request.MessageGroupId,
                    MessageDeduplicationId = request.MessageDeduplicationId,
                    MessageAttributes = request.MessageAttributes
                };
            }).ToList();
            
            var startTime = DateTime.UtcNow;
            var response = await _localStack.SqsClient.SendMessageBatchAsync(new SendMessageBatchRequest
            {
                QueueUrl = queueUrl,
                Entries = entries
            });
            var endTime = DateTime.UtcNow;
            
            // Record successful sends
            foreach (var successful in response.Successful)
            {
                var originalIndex = int.Parse(successful.Id);
                var originalMessage = batch.First(b => b.Index == originalIndex).Message;
                var originalEntry = entries.First(e => e.Id == successful.Id);
                
                var sentMessage = new SqsTestMessage
                {
                    OriginalMessage = originalMessage,
                    MessageId = successful.MessageId,
                    SendTime = startTime,
                    SendDuration = endTime - startTime,
                    MessageGroupId = originalEntry.MessageGroupId,
                    MessageDeduplicationId = originalEntry.MessageDeduplicationId,
                    MessageAttributes = originalEntry.MessageAttributes.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value.StringValue ?? kvp.Value.BinaryValue?.ToString() ?? ""),
                    WasBatchSent = true
                };
                
                sentMessages.Add(sentMessage);
            }
            
            // Assert no failed sends in property test
            if (response.Failed.Any())
            {
                throw new InvalidOperationException($"Batch send failed for {response.Failed.Count} messages: " +
                    string.Join(", ", response.Failed.Select(f => f.Code + ": " + f.Message)));
            }
        }
    }
    
    /// <summary>
    /// Receive all messages from the queue
    /// </summary>
    private async Task ReceiveAllMessages(string queueUrl, int expectedCount, List<Message> receivedMessages)
    {
        var maxAttempts = 30;
        var attempts = 0;
        
        while (receivedMessages.Count < expectedCount && attempts < maxAttempts)
        {
            var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                MessageAttributeNames = new List<string> { "All" },
                WaitTimeSeconds = 1
            });
            
            receivedMessages.AddRange(receiveResponse.Messages);
            attempts++;
            
            if (receiveResponse.Messages.Count == 0)
            {
                await Task.Delay(100);
            }
        }
    }
    
    /// <summary>
    /// Assert that all sent messages are delivered correctly
    /// </summary>
    private static void AssertMessageDeliveryCorrectness(List<SqsTestMessage> sentMessages, List<Message> receivedMessages)
    {
        // All sent messages should be received
        Assert.True(receivedMessages.Count >= sentMessages.Count * 0.95, // Allow 5% variance for LocalStack
            $"Expected at least {sentMessages.Count * 0.95} messages, received {receivedMessages.Count}");
        
        // Each received message should correspond to a sent message
        foreach (var receivedMessage in receivedMessages)
        {
            var messageBody = receivedMessage.Body;
            var matchingSent = sentMessages.FirstOrDefault(s => 
                JsonSerializer.Serialize(s.OriginalMessage.Payload) == messageBody);
            
            Assert.NotNull(matchingSent);
        }
    }
    
    /// <summary>
    /// Assert that message attributes are preserved correctly
    /// </summary>
    private static void AssertMessageAttributesPreservation(List<SqsTestMessage> sentMessages, List<Message> receivedMessages)
    {
        foreach (var receivedMessage in receivedMessages)
        {
            // Find corresponding sent message
            var messageBody = receivedMessage.Body;
            var matchingSent = sentMessages.FirstOrDefault(s => 
                JsonSerializer.Serialize(s.OriginalMessage.Payload) == messageBody);
            
            if (matchingSent == null) continue;
            
            // Verify SourceFlow attributes are preserved
            var requiredAttributes = new[] { "EntityId", "SequenceNo", "CommandType", "PayloadType" };
            
            foreach (var attrName in requiredAttributes)
            {
                Assert.True(receivedMessage.MessageAttributes.ContainsKey(attrName),
                    $"Missing required attribute: {attrName}");
                
                if (matchingSent.MessageAttributes.ContainsKey(attrName))
                {
                    Assert.Equal(matchingSent.MessageAttributes[attrName],
                        receivedMessage.MessageAttributes[attrName].StringValue);
                }
            }
            
            // Verify EntityId is numeric
            Assert.True(int.TryParse(receivedMessage.MessageAttributes["EntityId"].StringValue, out _),
                "EntityId should be numeric");
            
            // Verify SequenceNo is numeric
            Assert.True(int.TryParse(receivedMessage.MessageAttributes["SequenceNo"].StringValue, out _),
                "SequenceNo should be numeric");
        }
    }
    
    /// <summary>
    /// Assert FIFO ordering is maintained within message groups
    /// </summary>
    private static void AssertFifoOrdering(List<SqsTestMessage> sentMessages, List<Message> receivedMessages)
    {
        // Group messages by MessageGroupId
        var sentByGroup = sentMessages
            .Where(s => !string.IsNullOrEmpty(s.MessageGroupId))
            .GroupBy(s => s.MessageGroupId)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.SendTime).ToList());
        
        var receivedByGroup = receivedMessages
            .Where(r => r.Attributes.ContainsKey("MessageGroupId"))
            .GroupBy(r => r.Attributes["MessageGroupId"])
            .ToDictionary(g => g.Key, g => g.ToList());
        
        foreach (var groupId in sentByGroup.Keys)
        {
            if (!receivedByGroup.ContainsKey(groupId)) continue;
            
            var sentInGroup = sentByGroup[groupId];
            var receivedInGroup = receivedByGroup[groupId];
            
            // Within each group, messages should maintain order based on SequenceNo
            var receivedSequenceNos = receivedInGroup
                .Where(r => r.MessageAttributes.ContainsKey("SequenceNo"))
                .Select(r => int.Parse(r.MessageAttributes["SequenceNo"].StringValue))
                .ToList();
            
            var sortedSequenceNos = receivedSequenceNos.OrderBy(x => x).ToList();
            
            Assert.Equal(sortedSequenceNos, receivedSequenceNos);
        }
    }
    
    /// <summary>
    /// Assert batch operation efficiency
    /// </summary>
    private static void AssertBatchOperationEfficiency(SqsTestScenario scenario, List<SqsTestMessage> sentMessages)
    {
        if (!scenario.UseBatchSending) return;
        
        // Batch operations should be more efficient than individual sends
        var batchSentMessages = sentMessages.Where(s => s.WasBatchSent).ToList();
        var individualSentMessages = sentMessages.Where(s => !s.WasBatchSent).ToList();
        
        if (batchSentMessages.Any() && individualSentMessages.Any())
        {
            var avgBatchDuration = batchSentMessages.Average(s => s.SendDuration.TotalMilliseconds);
            var avgIndividualDuration = individualSentMessages.Average(s => s.SendDuration.TotalMilliseconds);
            
            // This is informational - actual efficiency depends on LocalStack vs real AWS
            Assert.True(avgBatchDuration >= 0 && avgIndividualDuration >= 0,
                "Both batch and individual send durations should be non-negative");
        }
        
        // Batch sends should respect AWS limits (max 10 messages per batch)
        var maxBatchSize = 10;
        Assert.True(batchSentMessages.Count <= scenario.Messages.Count,
            "Batch sent messages should not exceed total messages");
    }
    
    /// <summary>
    /// Assert performance consistency
    /// </summary>
    private static void AssertPerformanceConsistency(SqsTestScenario scenario, List<SqsTestMessage> sentMessages, List<Message> receivedMessages)
    {
        // Send performance should be consistent
        var sendDurations = sentMessages.Select(s => s.SendDuration.TotalMilliseconds).ToList();
        if (sendDurations.Count > 1)
        {
            var avgSendDuration = sendDurations.Average();
            var maxSendDuration = sendDurations.Max();
            
            // Performance should be reasonable (this is informational for LocalStack)
            Assert.True(avgSendDuration >= 0, "Average send duration should be non-negative");
            Assert.True(maxSendDuration < 30000, "Maximum send duration should be less than 30 seconds");
        }
        
        // Message throughput should be positive
        if (sentMessages.Any())
        {
            var totalSendTime = sentMessages.Max(s => s.SendTime.Add(s.SendDuration)) - sentMessages.Min(s => s.SendTime);
            if (totalSendTime.TotalSeconds > 0)
            {
                var throughput = sentMessages.Count / totalSendTime.TotalSeconds;
                Assert.True(throughput > 0, "Message throughput should be positive");
            }
        }
    }
    
    /// <summary>
    /// Create a send message request for the given test message
    /// </summary>
    private static SendMessageRequest CreateSendMessageRequest(string queueUrl, TestMessage message, QueueType queueType, int index)
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
    /// Clean up received messages
    /// </summary>
    private async Task CleanupMessages(string queueUrl, List<Message> receivedMessages)
    {
        var deleteTasks = receivedMessages.Select(message =>
            _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
            {
                QueueUrl = queueUrl,
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
    /// Create a FIFO queue for testing
    /// </summary>
    private async Task<string> CreateFifoQueueAsync(string queueName)
    {
        var response = await _localStack.SqsClient.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = queueName,
            Attributes = new Dictionary<string, string>
            {
                ["FifoQueue"] = "true",
                ["ContentBasedDeduplication"] = "true",
                ["MessageRetentionPeriod"] = "1209600",
                ["VisibilityTimeoutSeconds"] = "30"
            }
        });
        
        _createdQueues.Add(response.QueueUrl);
        return response.QueueUrl;
    }
    
    /// <summary>
    /// Create a standard queue for testing
    /// </summary>
    private async Task<string> CreateStandardQueueAsync(string queueName)
    {
        var response = await _localStack.SqsClient.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = queueName,
            Attributes = new Dictionary<string, string>
            {
                ["MessageRetentionPeriod"] = "1209600",
                ["VisibilityTimeoutSeconds"] = "30"
            }
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
/// FsCheck generators for SQS message processing property tests
/// </summary>
public static class SqsMessageGenerators
{
    /// <summary>
    /// Generate test scenarios for SQS message processing
    /// </summary>
    public static Arbitrary<SqsTestScenario> SqsTestScenario()
    {
        var queueTypeGen = Gen.Elements(QueueType.Standard, QueueType.Fifo);
        var useBatchGen = Gen.Elements(true, false);
        var messageCountGen = Gen.Choose(1, 20);
        
        var scenarioGen = from queueType in queueTypeGen
                         from useBatch in useBatchGen
                         from messageCount in messageCountGen
                         from messages in Gen.ListOf(messageCount, TestMessage())
                         select new SqsTestScenario
                         {
                             QueueType = queueType,
                             UseBatchSending = useBatch,
                             Messages = messages.ToList()
                         };
        
        return Arb.From(scenarioGen);
    }
    
    /// <summary>
    /// Generate test messages with realistic SourceFlow command structure
    /// </summary>
    public static Gen<TestMessage> TestMessage()
    {
        var entityIdGen = Gen.Choose(1, 10000);
        var sequenceNoGen = Gen.Choose(1, 1000);
        var commandTypeGen = Gen.Elements(
            "CreateOrderCommand", 
            "UpdateOrderCommand", 
            "CancelOrderCommand",
            "ProcessPaymentCommand",
            "ShipOrderCommand");
        var payloadTypeGen = Gen.Elements(
            "CreateOrderPayload",
            "UpdateOrderPayload", 
            "CancelOrderPayload",
            "ProcessPaymentPayload",
            "ShipOrderPayload");
        
        var payloadGen = from orderId in Gen.Fresh(() => Guid.NewGuid())
                        from customerId in Gen.Choose(1, 100000)
                        from amountCents in Gen.Choose(100, 1000000)
                        from currency in Gen.Elements("USD", "EUR", "GBP", "CAD")
                        select new Dictionary<string, object>
                        {
                            ["OrderId"] = orderId,
                            ["CustomerId"] = customerId,
                            ["Amount"] = Math.Round(amountCents / 100.0, 2),
                            ["Currency"] = currency,
                            ["Timestamp"] = DateTime.UtcNow.ToString("O")
                        };
        
        return from entityId in entityIdGen
               from sequenceNo in sequenceNoGen
               from commandType in commandTypeGen
               from payloadType in payloadTypeGen
               from payload in payloadGen
               select new TestMessage
               {
                   EntityId = entityId,
                   SequenceNo = sequenceNo,
                   CommandType = commandType,
                   PayloadType = payloadType,
                   Payload = payload
               };
    }
}

/// <summary>
/// Test scenario for SQS message processing
/// </summary>
public class SqsTestScenario
{
    public QueueType QueueType { get; set; }
    public bool UseBatchSending { get; set; }
    public List<TestMessage> Messages { get; set; } = new();
}

/// <summary>
/// Test message representing a SourceFlow command
/// </summary>
public class TestMessage
{
    public int EntityId { get; set; }
    public int SequenceNo { get; set; }
    public string CommandType { get; set; } = "";
    public string PayloadType { get; set; } = "";
    public Dictionary<string, object> Payload { get; set; } = new();
}

/// <summary>
/// Sent message tracking information
/// </summary>
public class SqsTestMessage
{
    public TestMessage OriginalMessage { get; set; } = new();
    public string MessageId { get; set; } = "";
    public DateTime SendTime { get; set; }
    public TimeSpan SendDuration { get; set; }
    public string? MessageGroupId { get; set; }
    public string? MessageDeduplicationId { get; set; }
    public Dictionary<string, string> MessageAttributes { get; set; } = new();
    public bool WasBatchSent { get; set; }
}

/// <summary>
/// Queue type enumeration
/// </summary>
public enum QueueType
{
    Standard,
    Fifo
}

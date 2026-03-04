using Amazon.SQS.Model;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using SourceFlow.Messaging.Commands;
using System.Text.Json;

namespace SourceFlow.Cloud.AWS.Tests.Integration;

/// <summary>
/// Comprehensive integration tests for SQS FIFO queue functionality
/// Tests message ordering, deduplication, EntityId-based grouping, and FIFO-specific behaviors
/// </summary>
[Collection("AWS Integration Tests")]
[Trait("Category", "Integration")]
[Trait("Category", "RequiresLocalStack")]
public class SqsFifoIntegrationTests : IClassFixture<LocalStackTestFixture>, IAsyncDisposable
{
    private readonly LocalStackTestFixture _localStack;
    private readonly List<string> _createdQueues = new();
    
    public SqsFifoIntegrationTests(LocalStackTestFixture localStack)
    {
        _localStack = localStack;
    }
    
    [Fact]
    public async Task FifoQueue_ShouldMaintainMessageOrderingWithinMessageGroups()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-fifo-ordering-{Guid.NewGuid():N}.fifo";
        var queueUrl = await CreateFifoQueueAsync(queueName);
        
        var messageGroupId = "test-group-1";
        var messages = new List<string>();
        
        // Act - Send multiple messages in sequence to the same message group
        for (int i = 0; i < 5; i++)
        {
            var messageBody = $"Message {i:D2} - {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}";
            messages.Add(messageBody);
            
            await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = messageBody,
                MessageGroupId = messageGroupId,
                MessageDeduplicationId = $"dedup-{i}-{Guid.NewGuid():N}"
            });
            
            // Small delay to ensure ordering
            await Task.Delay(10);
        }
        
        // Act - Receive messages
        var receivedMessages = new List<string>();
        var maxAttempts = 10;
        var attempts = 0;
        
        while (receivedMessages.Count < messages.Count && attempts < maxAttempts)
        {
            var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 1,
                AttributeNames = new List<string> { "All" }
            });
            
            foreach (var message in receiveResponse.Messages)
            {
                receivedMessages.Add(message.Body);
                
                // Delete message to acknowledge processing
                await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
                {
                    QueueUrl = queueUrl,
                    ReceiptHandle = message.ReceiptHandle
                });
            }
            
            attempts++;
        }
        
        // Assert - Messages should be received in the same order they were sent
        Assert.Equal(messages.Count, receivedMessages.Count);
        for (int i = 0; i < messages.Count; i++)
        {
            Assert.Equal(messages[i], receivedMessages[i]);
        }
    }
    
    [Fact]
    public async Task FifoQueue_ShouldHandleContentBasedDeduplication()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-fifo-dedup-{Guid.NewGuid():N}.fifo";
        var queueUrl = await CreateFifoQueueAsync(queueName, new Dictionary<string, string>
        {
            ["ContentBasedDeduplication"] = "true"
        });
        
        var messageGroupId = "dedup-test-group";
        var duplicateMessageBody = $"Duplicate message content - {DateTime.UtcNow:yyyy-MM-dd}";
        
        // Act - Send the same message multiple times (should be deduplicated)
        var sendTasks = new List<Task<SendMessageResponse>>();
        for (int i = 0; i < 3; i++)
        {
            sendTasks.Add(_localStack.SqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = duplicateMessageBody,
                MessageGroupId = messageGroupId
                // No MessageDeduplicationId - using content-based deduplication
            }));
        }
        
        var sendResponses = await Task.WhenAll(sendTasks);
        
        // Wait a moment for deduplication to take effect
        await Task.Delay(1000);
        
        // Act - Receive messages
        var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 2
        });
        
        // Assert - Only one message should be received due to deduplication
        Assert.Single(receiveResponse.Messages);
        Assert.Equal(duplicateMessageBody, receiveResponse.Messages[0].Body);
        
        // All send operations should have succeeded (deduplication happens server-side)
        Assert.All(sendResponses, response => Assert.NotNull(response.MessageId));
    }
    
    [Fact]
    public async Task FifoQueue_ShouldSupportEntityIdBasedMessageGrouping()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-fifo-entity-grouping-{Guid.NewGuid():N}.fifo";
        var queueUrl = await CreateFifoQueueAsync(queueName);
        
        var entity1Id = 1001;
        var entity2Id = 1002;
        var messagesPerEntity = 3;
        
        // Act - Send messages for different entities (should be processed in parallel)
        var sendTasks = new List<Task>();
        
        for (int i = 0; i < messagesPerEntity; i++)
        {
            // Messages for Entity 1
            sendTasks.Add(_localStack.SqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = $"Entity {entity1Id} - Message {i}",
                MessageGroupId = $"entity-{entity1Id}",
                MessageDeduplicationId = $"entity-{entity1Id}-msg-{i}-{Guid.NewGuid():N}",
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["EntityId"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = entity1Id.ToString()
                    },
                    ["SequenceNo"] = new MessageAttributeValue
                    {
                        DataType = "Number", 
                        StringValue = i.ToString()
                    }
                }
            }));
            
            // Messages for Entity 2
            sendTasks.Add(_localStack.SqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = $"Entity {entity2Id} - Message {i}",
                MessageGroupId = $"entity-{entity2Id}",
                MessageDeduplicationId = $"entity-{entity2Id}-msg-{i}-{Guid.NewGuid():N}",
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["EntityId"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = entity2Id.ToString()
                    },
                    ["SequenceNo"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = i.ToString()
                    }
                }
            }));
        }
        
        await Task.WhenAll(sendTasks);
        
        // Act - Receive all messages
        var allMessages = new List<Message>();
        var maxAttempts = 10;
        var attempts = 0;
        
        while (allMessages.Count < messagesPerEntity * 2 && attempts < maxAttempts)
        {
            var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                MessageAttributeNames = new List<string> { "All" },
                WaitTimeSeconds = 1
            });
            
            allMessages.AddRange(receiveResponse.Messages);
            
            // Delete received messages
            foreach (var message in receiveResponse.Messages)
            {
                await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
                {
                    QueueUrl = queueUrl,
                    ReceiptHandle = message.ReceiptHandle
                });
            }
            
            attempts++;
        }
        
        // Assert - Should receive all messages
        Assert.Equal(messagesPerEntity * 2, allMessages.Count);
        
        // Group messages by EntityId
        var entity1Messages = allMessages
            .Where(m => m.MessageAttributes.ContainsKey("EntityId") && 
                       m.MessageAttributes["EntityId"].StringValue == entity1Id.ToString())
            .OrderBy(m => int.Parse(m.MessageAttributes["SequenceNo"].StringValue))
            .ToList();
            
        var entity2Messages = allMessages
            .Where(m => m.MessageAttributes.ContainsKey("EntityId") && 
                       m.MessageAttributes["EntityId"].StringValue == entity2Id.ToString())
            .OrderBy(m => int.Parse(m.MessageAttributes["SequenceNo"].StringValue))
            .ToList();
        
        // Assert - Each entity should have received all its messages in order
        Assert.Equal(messagesPerEntity, entity1Messages.Count);
        Assert.Equal(messagesPerEntity, entity2Messages.Count);
        
        for (int i = 0; i < messagesPerEntity; i++)
        {
            Assert.Contains($"Entity {entity1Id} - Message {i}", entity1Messages[i].Body);
            Assert.Contains($"Entity {entity2Id} - Message {i}", entity2Messages[i].Body);
        }
    }
    
    [Fact]
    public async Task FifoQueue_ShouldValidateFifoSpecificAttributes()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-fifo-attributes-{Guid.NewGuid():N}.fifo";
        var queueUrl = await CreateFifoQueueAsync(queueName, new Dictionary<string, string>
        {
            ["ContentBasedDeduplication"] = "true",
            ["DeduplicationScope"] = "messageGroup",
            ["FifoThroughputLimit"] = "perMessageGroupId"
        });
        
        // Act - Get queue attributes
        var attributesResponse = await _localStack.SqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = new List<string> { "All" }
        });
        
        // Assert - FIFO-specific attributes should be set correctly
        Assert.True(attributesResponse.Attributes.ContainsKey("FifoQueue"));
        Assert.Equal("true", attributesResponse.Attributes["FifoQueue"]);
        
        Assert.True(attributesResponse.Attributes.ContainsKey("ContentBasedDeduplication"));
        Assert.Equal("true", attributesResponse.Attributes["ContentBasedDeduplication"]);
        
        // Test that MessageGroupId is required for FIFO queues
        var exception = await Assert.ThrowsAsync<Amazon.SQS.AmazonSQSException>(async () =>
        {
            await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = "Test message without MessageGroupId"
                // Missing MessageGroupId - should fail
            });
        });
        
        Assert.Contains("MessageGroupId", exception.Message);
    }
    
    [Fact]
    public async Task FifoQueue_ShouldHandleSourceFlowCommandMetadata()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-fifo-sourceflow-{Guid.NewGuid():N}.fifo";
        var queueUrl = await CreateFifoQueueAsync(queueName);
        
        var entityId = 12345;
        var sequenceNo = 42;
        var commandType = "CreateOrderCommand";
        var payloadType = "CreateOrderPayload";
        
        var commandPayload = new
        {
            OrderId = Guid.NewGuid(),
            CustomerId = 67890,
            Amount = 99.99m,
            Currency = "USD"
        };
        
        var commandMetadata = new Dictionary<string, object>
        {
            ["CorrelationId"] = Guid.NewGuid().ToString(),
            ["UserId"] = "test-user-123",
            ["Timestamp"] = DateTime.UtcNow.ToString("O")
        };
        
        // Act - Send message with SourceFlow command metadata
        var sendResponse = await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = JsonSerializer.Serialize(commandPayload),
            MessageGroupId = $"entity-{entityId}",
            MessageDeduplicationId = $"cmd-{entityId}-{sequenceNo}-{Guid.NewGuid():N}",
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["EntityId"] = new MessageAttributeValue
                {
                    DataType = "Number",
                    StringValue = entityId.ToString()
                },
                ["SequenceNo"] = new MessageAttributeValue
                {
                    DataType = "Number",
                    StringValue = sequenceNo.ToString()
                },
                ["CommandType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = commandType
                },
                ["PayloadType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = payloadType
                },
                ["Metadata"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = JsonSerializer.Serialize(commandMetadata)
                }
            }
        });
        
        Assert.NotNull(sendResponse.MessageId);
        
        // Act - Receive and validate message
        var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
            MessageAttributeNames = new List<string> { "All" },
            WaitTimeSeconds = 2
        });
        
        // Assert - Message should contain all SourceFlow metadata
        Assert.Single(receiveResponse.Messages);
        var message = receiveResponse.Messages[0];
        
        Assert.Equal(entityId.ToString(), message.MessageAttributes["EntityId"].StringValue);
        Assert.Equal(sequenceNo.ToString(), message.MessageAttributes["SequenceNo"].StringValue);
        Assert.Equal(commandType, message.MessageAttributes["CommandType"].StringValue);
        Assert.Equal(payloadType, message.MessageAttributes["PayloadType"].StringValue);
        
        var receivedMetadata = JsonSerializer.Deserialize<Dictionary<string, object>>(
            message.MessageAttributes["Metadata"].StringValue);
        Assert.NotNull(receivedMetadata);
        Assert.True(receivedMetadata.ContainsKey("CorrelationId"));
        Assert.True(receivedMetadata.ContainsKey("UserId"));
        Assert.True(receivedMetadata.ContainsKey("Timestamp"));
        
        var receivedPayload = JsonSerializer.Deserialize<Dictionary<string, object>>(message.Body);
        Assert.NotNull(receivedPayload);
        Assert.True(receivedPayload.ContainsKey("OrderId"));
        Assert.True(receivedPayload.ContainsKey("CustomerId"));
        Assert.True(receivedPayload.ContainsKey("Amount"));
    }
    
    [Fact]
    public async Task FifoQueue_ShouldHandleHighThroughputScenario()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-fifo-throughput-{Guid.NewGuid():N}.fifo";
        var queueUrl = await CreateFifoQueueAsync(queueName, new Dictionary<string, string>
        {
            ["FifoThroughputLimit"] = "perMessageGroupId",
            ["DeduplicationScope"] = "messageGroup"
        });
        
        var messageGroups = 5;
        var messagesPerGroup = 20;
        var totalMessages = messageGroups * messagesPerGroup;
        
        // Act - Send messages across multiple message groups for higher throughput
        var sendTasks = new List<Task<SendMessageResponse>>();
        
        for (int groupId = 0; groupId < messageGroups; groupId++)
        {
            for (int msgId = 0; msgId < messagesPerGroup; msgId++)
            {
                sendTasks.Add(_localStack.SqsClient.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl = queueUrl,
                    MessageBody = $"Group {groupId} - Message {msgId} - {DateTime.UtcNow:HH:mm:ss.fff}",
                    MessageGroupId = $"group-{groupId}",
                    MessageDeduplicationId = $"group-{groupId}-msg-{msgId}-{Guid.NewGuid():N}",
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        ["GroupId"] = new MessageAttributeValue
                        {
                            DataType = "Number",
                            StringValue = groupId.ToString()
                        },
                        ["MessageId"] = new MessageAttributeValue
                        {
                            DataType = "Number",
                            StringValue = msgId.ToString()
                        }
                    }
                }));
            }
        }
        
        var startTime = DateTime.UtcNow;
        var sendResponses = await Task.WhenAll(sendTasks);
        var sendDuration = DateTime.UtcNow - startTime;
        
        // Assert - All messages should be sent successfully
        Assert.Equal(totalMessages, sendResponses.Length);
        Assert.All(sendResponses, response => Assert.NotNull(response.MessageId));
        
        // Act - Receive all messages
        var receivedMessages = new List<Message>();
        var maxAttempts = 20;
        var attempts = 0;
        
        startTime = DateTime.UtcNow;
        while (receivedMessages.Count < totalMessages && attempts < maxAttempts)
        {
            var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                MessageAttributeNames = new List<string> { "All" },
                WaitTimeSeconds = 1
            });
            
            receivedMessages.AddRange(receiveResponse.Messages);
            
            // Delete received messages
            foreach (var message in receiveResponse.Messages)
            {
                await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
                {
                    QueueUrl = queueUrl,
                    ReceiptHandle = message.ReceiptHandle
                });
            }
            
            attempts++;
        }
        var receiveDuration = DateTime.UtcNow - startTime;
        
        // Assert - All messages should be received
        Assert.Equal(totalMessages, receivedMessages.Count);
        
        // Verify ordering within each message group
        var messagesByGroup = receivedMessages
            .GroupBy(m => m.MessageAttributes["GroupId"].StringValue)
            .ToDictionary(g => int.Parse(g.Key), g => g.OrderBy(m => int.Parse(m.MessageAttributes["MessageId"].StringValue)).ToList());
        
        Assert.Equal(messageGroups, messagesByGroup.Count);
        
        foreach (var group in messagesByGroup)
        {
            Assert.Equal(messagesPerGroup, group.Value.Count);
            
            for (int i = 0; i < messagesPerGroup; i++)
            {
                Assert.Contains($"Group {group.Key} - Message {i}", group.Value[i].Body);
            }
        }
        
        // Log performance metrics
        var sendThroughput = totalMessages / sendDuration.TotalSeconds;
        var receiveThroughput = totalMessages / receiveDuration.TotalSeconds;
        
        // These are informational - actual thresholds would depend on LocalStack vs real AWS
        Assert.True(sendThroughput > 0, $"Send throughput: {sendThroughput:F2} messages/second");
        Assert.True(receiveThroughput > 0, $"Receive throughput: {receiveThroughput:F2} messages/second");
    }
    
    /// <summary>
    /// Create a FIFO queue with the specified name and attributes
    /// </summary>
    private async Task<string> CreateFifoQueueAsync(string queueName, Dictionary<string, string>? additionalAttributes = null)
    {
        var attributes = new Dictionary<string, string>
        {
            ["FifoQueue"] = "true",
            ["ContentBasedDeduplication"] = "true",
            ["MessageRetentionPeriod"] = "1209600", // 14 days
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

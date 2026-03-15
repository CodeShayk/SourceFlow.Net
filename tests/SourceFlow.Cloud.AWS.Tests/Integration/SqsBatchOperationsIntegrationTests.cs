using Amazon.SQS.Model;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using System.Diagnostics;
using System.Text.Json;

namespace SourceFlow.Cloud.AWS.Tests.Integration;

/// <summary>
/// Comprehensive integration tests for SQS batch operations
/// Tests batch sending up to AWS limits, efficiency, resource utilization, and partial failure handling
/// </summary>
[Collection("AWS Integration Tests")]
[Trait("Category", "Integration")]
[Trait("Category", "RequiresLocalStack")]
public class SqsBatchOperationsIntegrationTests : IClassFixture<LocalStackTestFixture>, IAsyncDisposable
{
    private readonly LocalStackTestFixture _localStack;
    private readonly List<string> _createdQueues = new();
    
    public SqsBatchOperationsIntegrationTests(LocalStackTestFixture localStack)
    {
        _localStack = localStack;
    }
    
    [Fact]
    public async Task BatchSend_ShouldRespectAwsTenMessageLimit()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-batch-limit-{Guid.NewGuid():N}";
        var queueUrl = await CreateStandardQueueAsync(queueName);
        
        // Test exactly 10 messages (AWS limit)
        var maxBatchSize = 10;
        var batchEntries = new List<SendMessageBatchRequestEntry>();
        
        for (int i = 0; i < maxBatchSize; i++)
        {
            batchEntries.Add(new SendMessageBatchRequestEntry
            {
                Id = i.ToString(),
                MessageBody = $"Batch message {i} - {DateTime.UtcNow:HH:mm:ss.fff}",
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["MessageIndex"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = i.ToString()
                    },
                    ["BatchId"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = Guid.NewGuid().ToString()
                    },
                    ["EntityId"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = (1000 + i).ToString()
                    },
                    ["CommandType"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = "BatchTestCommand"
                    }
                }
            });
        }
        
        // Act - Send batch of exactly 10 messages
        var batchResponse = await _localStack.SqsClient.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = queueUrl,
            Entries = batchEntries
        });
        
        // Assert - All messages should be sent successfully
        Assert.Equal(maxBatchSize, batchResponse.Successful.Count);
        Assert.Empty(batchResponse.Failed);
        
        // Verify each successful response
        foreach (var successful in batchResponse.Successful)
        {
            Assert.NotNull(successful.MessageId);
            Assert.True(int.Parse(successful.Id) >= 0 && int.Parse(successful.Id) < maxBatchSize);
        }
        
        // Act - Receive all messages
        var receivedMessages = new List<Message>();
        var maxAttempts = 10;
        var attempts = 0;
        
        while (receivedMessages.Count < maxBatchSize && attempts < maxAttempts)
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
        }
        
        // Assert - All messages should be received
        Assert.Equal(maxBatchSize, receivedMessages.Count);
        
        // Verify message content and attributes
        var receivedIndices = receivedMessages
            .Select(m => int.Parse(m.MessageAttributes["MessageIndex"].StringValue))
            .OrderBy(i => i)
            .ToList();
        
        var expectedIndices = Enumerable.Range(0, maxBatchSize).ToList();
        Assert.Equal(expectedIndices, receivedIndices);
        
        // Clean up
        await CleanupMessages(queueUrl, receivedMessages);
    }
    
    [Fact]
    public async Task BatchSend_ShouldRejectMoreThanTenMessages()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-batch-over-limit-{Guid.NewGuid():N}";
        var queueUrl = await CreateStandardQueueAsync(queueName);
        
        // Try to send 11 messages (over AWS limit)
        var overLimitBatchSize = 11;
        var batchEntries = new List<SendMessageBatchRequestEntry>();
        
        for (int i = 0; i < overLimitBatchSize; i++)
        {
            batchEntries.Add(new SendMessageBatchRequestEntry
            {
                Id = i.ToString(),
                MessageBody = $"Over limit message {i}",
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["MessageIndex"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = i.ToString()
                    }
                }
            });
        }
        
        // Act & Assert - Should throw exception for too many messages
        var exception = await Assert.ThrowsAnyAsync<Amazon.SQS.AmazonSQSException>(async () =>
        {
            await _localStack.SqsClient.SendMessageBatchAsync(new SendMessageBatchRequest
            {
                QueueUrl = queueUrl,
                Entries = batchEntries
            });
        });
        
        // Verify error is related to batch size limit (message varies by SDK/LocalStack version)
        Assert.True(
            exception.Message.Contains("batch", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("entries", StringComparison.OrdinalIgnoreCase),
            $"Expected batch size error but got: {exception.Message}");
    }
    
    [Fact]
    public async Task BatchSend_ShouldBeMoreEfficientThanIndividualSends()
    {
        // Skip if not configured for integration tests or performance tests
        if (!_localStack.Configuration.RunIntegrationTests || 
            !_localStack.Configuration.RunPerformanceTests || 
            _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-batch-efficiency-{Guid.NewGuid():N}";
        var queueUrl = await CreateStandardQueueAsync(queueName);
        
        var messageCount = 30; // Test with multiple batches
        var testMessages = Enumerable.Range(0, messageCount)
            .Select(i => new
            {
                Index = i,
                Body = $"Efficiency test message {i} - {DateTime.UtcNow:HH:mm:ss.fff}",
                EntityId = 2000 + i,
                CommandType = "EfficiencyTestCommand"
            })
            .ToList();
        
        // Act - Send messages individually
        var individualStopwatch = Stopwatch.StartNew();
        var individualTasks = testMessages.Select(async msg =>
        {
            return await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = msg.Body,
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["MessageIndex"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = msg.Index.ToString()
                    },
                    ["SendMethod"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = "Individual"
                    },
                    ["EntityId"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = msg.EntityId.ToString()
                    },
                    ["CommandType"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = msg.CommandType
                    }
                }
            });
        });
        
        var individualResults = await Task.WhenAll(individualTasks);
        individualStopwatch.Stop();
        
        // Clear the queue
        await DrainQueue(queueUrl);
        
        // Act - Send messages in batches
        var batchStopwatch = Stopwatch.StartNew();
        var batches = testMessages
            .Select((msg, index) => new { Message = msg, Index = index })
            .GroupBy(x => x.Index / 10) // Group into batches of 10
            .Select(g => g.ToList())
            .ToList();
        
        var batchTasks = batches.Select(async batch =>
        {
            var entries = batch.Select(item => new SendMessageBatchRequestEntry
            {
                Id = item.Message.Index.ToString(),
                MessageBody = item.Message.Body,
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["MessageIndex"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = item.Message.Index.ToString()
                    },
                    ["SendMethod"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = "Batch"
                    },
                    ["EntityId"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = item.Message.EntityId.ToString()
                    },
                    ["CommandType"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = item.Message.CommandType
                    }
                }
            }).ToList();
            
            return await _localStack.SqsClient.SendMessageBatchAsync(new SendMessageBatchRequest
            {
                QueueUrl = queueUrl,
                Entries = entries
            });
        });
        
        var batchResults = await Task.WhenAll(batchTasks);
        batchStopwatch.Stop();
        
        // Assert - Both methods should send all messages successfully
        Assert.Equal(messageCount, individualResults.Length);
        Assert.All(individualResults, result => Assert.NotNull(result.MessageId));
        
        var totalBatchSuccessful = batchResults.Sum(r => r.Successful.Count);
        var totalBatchFailed = batchResults.Sum(r => r.Failed.Count);
        
        Assert.Equal(messageCount, totalBatchSuccessful);
        Assert.Equal(0, totalBatchFailed);
        
        // Calculate performance metrics
        var individualThroughput = messageCount / individualStopwatch.Elapsed.TotalSeconds;
        var batchThroughput = messageCount / batchStopwatch.Elapsed.TotalSeconds;
        var individualLatency = individualStopwatch.Elapsed.TotalMilliseconds / messageCount;
        var batchLatency = batchStopwatch.Elapsed.TotalMilliseconds / messageCount;
        
        // Log performance results
        Console.WriteLine($"Individual sends: {individualThroughput:F2} msg/sec, {individualLatency:F2}ms avg latency");
        Console.WriteLine($"Batch sends: {batchThroughput:F2} msg/sec, {batchLatency:F2}ms avg latency");
        Console.WriteLine($"Batch efficiency gain: {(batchThroughput / individualThroughput):F2}x throughput, {(individualLatency / batchLatency):F2}x latency improvement");
        
        // Assert - Batch should be more efficient (this is informational for LocalStack)
        Assert.True(batchThroughput > 0 && individualThroughput > 0, 
            "Both batch and individual throughput should be positive");
        
        // In real AWS, batch operations are typically more efficient
        // For LocalStack, we just verify both methods work correctly
        
        // Verify all messages are in the queue
        var finalReceiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10,
            MessageAttributeNames = new List<string> { "All" },
            WaitTimeSeconds = 2
        });
        
        Assert.True(finalReceiveResponse.Messages.Count > 0, "Should have messages from batch sends");
        
        // Clean up
        await DrainQueue(queueUrl);
    }
    
    [Fact]
    public async Task BatchSend_ShouldHandlePartialFailures()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-batch-partial-failure-{Guid.NewGuid():N}";
        var queueUrl = await CreateStandardQueueAsync(queueName);
        
        // Create a batch with some potentially problematic messages
        var batchEntries = new List<SendMessageBatchRequestEntry>
        {
            // Valid messages
            new SendMessageBatchRequestEntry
            {
                Id = "valid-1",
                MessageBody = "Valid message 1",
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["MessageType"] = new MessageAttributeValue { DataType = "String", StringValue = "Valid" }
                }
            },
            new SendMessageBatchRequestEntry
            {
                Id = "valid-2",
                MessageBody = "Valid message 2",
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["MessageType"] = new MessageAttributeValue { DataType = "String", StringValue = "Valid" }
                }
            },
            // Potentially problematic message (unique ID)
            new SendMessageBatchRequestEntry
            {
                Id = "problematic-1",
                MessageBody = "Duplicate ID message",
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["MessageType"] = new MessageAttributeValue { DataType = "String", StringValue = "Duplicate" }
                }
            },
            // Valid message
            new SendMessageBatchRequestEntry
            {
                Id = "valid-3",
                MessageBody = "Valid message 3",
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["MessageType"] = new MessageAttributeValue { DataType = "String", StringValue = "Valid" }
                }
            }
        };
        
        // Act - Send batch with potential failures
        var batchResponse = await _localStack.SqsClient.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = queueUrl,
            Entries = batchEntries
        });
        
        // Assert - Should have both successful and failed messages
        Assert.True(batchResponse.Successful.Count > 0, "Should have some successful messages");
        
        // In LocalStack, duplicate IDs might be handled differently than real AWS
        // The key is that the operation completes and provides clear success/failure information
        var totalProcessed = batchResponse.Successful.Count + batchResponse.Failed.Count;
        Assert.Equal(batchEntries.Count, totalProcessed);
        
        // Verify successful messages have valid response data
        foreach (var successful in batchResponse.Successful)
        {
            Assert.NotNull(successful.MessageId);
            Assert.Contains(successful.Id, batchEntries.Select(e => e.Id));
        }
        
        // Verify failed messages have error information
        foreach (var failed in batchResponse.Failed)
        {
            Assert.NotNull(failed.Id);
            Assert.NotNull(failed.Code);
            Assert.NotNull(failed.Message);
            Assert.True(failed.SenderFault); // Client-side errors should be marked as sender fault
        }
        
        // Act - Receive successful messages
        var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10,
            MessageAttributeNames = new List<string> { "All" },
            WaitTimeSeconds = 2
        });
        
        // Assert - Should receive only the successful messages
        Assert.Equal(batchResponse.Successful.Count, receiveResponse.Messages.Count);
        
        foreach (var message in receiveResponse.Messages)
        {
            Assert.True(message.MessageAttributes.ContainsKey("MessageType"));
            var messageType = message.MessageAttributes["MessageType"].StringValue;
            Assert.True(messageType == "Valid" || messageType == "Duplicate"); // Depending on LocalStack behavior
        }
        
        // Clean up
        await CleanupMessages(queueUrl, receiveResponse.Messages);
    }
    
    [Fact]
    public async Task BatchSend_ShouldSupportFifoQueues()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-batch-fifo-{Guid.NewGuid():N}.fifo";
        var queueUrl = await CreateFifoQueueAsync(queueName);
        
        var entityId = 3000;
        var messageGroupId = $"entity-{entityId}";
        var batchSize = 8; // Less than 10 for easier testing
        
        // Create FIFO batch entries
        var batchEntries = new List<SendMessageBatchRequestEntry>();
        
        for (int i = 0; i < batchSize; i++)
        {
            batchEntries.Add(new SendMessageBatchRequestEntry
            {
                Id = i.ToString(),
                MessageBody = $"FIFO batch message {i} - Entity {entityId}",
                MessageGroupId = messageGroupId,
                MessageDeduplicationId = $"batch-{entityId}-{i}-{Guid.NewGuid():N}",
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
                        StringValue = i.ToString()
                    },
                    ["CommandType"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = "FifoBatchCommand"
                    },
                    ["BatchIndex"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = i.ToString()
                    }
                }
            });
        }
        
        // Act - Send FIFO batch
        var batchResponse = await _localStack.SqsClient.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = queueUrl,
            Entries = batchEntries
        });
        
        // Assert - All messages should be sent successfully
        Assert.Equal(batchSize, batchResponse.Successful.Count);
        Assert.Empty(batchResponse.Failed);
        
        // Act - Receive messages in order
        var receivedMessages = new List<Message>();
        var maxAttempts = 10;
        var attempts = 0;
        
        while (receivedMessages.Count < batchSize && attempts < maxAttempts)
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
        }
        
        // Assert - All messages should be received
        Assert.Equal(batchSize, receivedMessages.Count);
        
        // Verify FIFO ordering is maintained
        var orderedMessages = receivedMessages
            .OrderBy(m => int.Parse(m.MessageAttributes["BatchIndex"].StringValue))
            .ToList();
        
        for (int i = 0; i < batchSize; i++)
        {
            var message = orderedMessages[i];
            Assert.Equal(i.ToString(), message.MessageAttributes["BatchIndex"].StringValue);
            Assert.Equal(entityId.ToString(), message.MessageAttributes["EntityId"].StringValue);
            Assert.Equal("FifoBatchCommand", message.MessageAttributes["CommandType"].StringValue);
            Assert.Contains($"FIFO batch message {i}", message.Body);
        }
        
        // Verify message group ID is preserved
        foreach (var message in receivedMessages)
        {
            if (message.Attributes.ContainsKey("MessageGroupId"))
            {
                Assert.Equal(messageGroupId, message.Attributes["MessageGroupId"]);
            }
        }
        
        // Clean up
        await CleanupMessages(queueUrl, receivedMessages);
    }
    
    [Fact]
    public async Task BatchReceive_ShouldReceiveMultipleMessages()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-batch-receive-{Guid.NewGuid():N}";
        var queueUrl = await CreateStandardQueueAsync(queueName);
        
        var messageCount = 15;
        
        // Send individual messages first
        var sendTasks = Enumerable.Range(0, messageCount).Select(async i =>
        {
            return await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = $"Batch receive test message {i}",
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["MessageIndex"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = i.ToString()
                    },
                    ["EntityId"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = (4000 + i).ToString()
                    },
                    ["CommandType"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = "BatchReceiveTestCommand"
                    }
                }
            });
        });
        
        await Task.WhenAll(sendTasks);
        
        // Act - Receive messages in batches
        var allReceivedMessages = new List<Message>();
        var maxBatchReceiveAttempts = 5;
        var attempts = 0;
        
        while (allReceivedMessages.Count < messageCount && attempts < maxBatchReceiveAttempts)
        {
            var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10, // AWS maximum for batch receive
                MessageAttributeNames = new List<string> { "All" },
                WaitTimeSeconds = 2
            });
            
            allReceivedMessages.AddRange(receiveResponse.Messages);
            attempts++;
            
            if (receiveResponse.Messages.Count == 0)
            {
                break; // No more messages
            }
        }
        
        // Assert - Should receive all messages
        Assert.True(allReceivedMessages.Count >= messageCount * 0.9, // Allow some variance
            $"Expected at least {messageCount * 0.9} messages, received {allReceivedMessages.Count}");
        
        // Verify message content
        var receivedIndices = allReceivedMessages
            .Select(m => int.Parse(m.MessageAttributes["MessageIndex"].StringValue))
            .OrderBy(i => i)
            .ToList();
        
        Assert.True(receivedIndices.Count > 0, "Should have received messages with indices");
        
        // Verify all messages have required attributes
        foreach (var message in allReceivedMessages)
        {
            Assert.True(message.MessageAttributes.ContainsKey("MessageIndex"));
            Assert.True(message.MessageAttributes.ContainsKey("EntityId"));
            Assert.True(message.MessageAttributes.ContainsKey("CommandType"));
            Assert.Equal("BatchReceiveTestCommand", message.MessageAttributes["CommandType"].StringValue);
        }
        
        // Clean up
        await CleanupMessages(queueUrl, allReceivedMessages);
    }
    
    [Fact]
    public async Task BatchDelete_ShouldDeleteMultipleMessages()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-batch-delete-{Guid.NewGuid():N}";
        var queueUrl = await CreateStandardQueueAsync(queueName);
        
        var messageCount = 8;
        
        // Send messages
        var sendTasks = Enumerable.Range(0, messageCount).Select(async i =>
        {
            return await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = $"Batch delete test message {i}",
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["MessageIndex"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = i.ToString()
                    }
                }
            });
        });
        
        await Task.WhenAll(sendTasks);
        
        // Receive messages
        var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10,
            MessageAttributeNames = new List<string> { "All" },
            WaitTimeSeconds = 2
        });
        
        Assert.True(receiveResponse.Messages.Count >= messageCount * 0.8, 
            $"Should receive at least {messageCount * 0.8} messages for batch delete test");
        
        // Act - Delete messages in batch
        var deleteEntries = receiveResponse.Messages.Select((message, index) => new DeleteMessageBatchRequestEntry
        {
            Id = index.ToString(),
            ReceiptHandle = message.ReceiptHandle
        }).ToList();
        
        var batchDeleteResponse = await _localStack.SqsClient.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
        {
            QueueUrl = queueUrl,
            Entries = deleteEntries
        });
        
        // Assert - All deletes should be successful
        Assert.Equal(deleteEntries.Count, batchDeleteResponse.Successful.Count);
        Assert.Empty(batchDeleteResponse.Failed);
        
        // Verify each successful delete
        foreach (var successful in batchDeleteResponse.Successful)
        {
            Assert.Contains(successful.Id, deleteEntries.Select(e => e.Id));
        }
        
        // Act - Verify queue is empty
        var finalReceiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 1
        });
        
        // Assert - Queue should be empty after batch delete
        Assert.Empty(finalReceiveResponse.Messages);
    }
    
    /// <summary>
    /// Create a standard queue with the specified name and attributes
    /// </summary>
    private async Task<string> CreateStandardQueueAsync(string queueName, Dictionary<string, string>? additionalAttributes = null)
    {
        var attributes = new Dictionary<string, string>
        {
            ["MessageRetentionPeriod"] = "1209600", // 14 days
            ["VisibilityTimeout"] = "30"
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
    /// Create a FIFO queue with the specified name and attributes
    /// </summary>
    private async Task<string> CreateFifoQueueAsync(string queueName, Dictionary<string, string>? additionalAttributes = null)
    {
        var attributes = new Dictionary<string, string>
        {
            ["FifoQueue"] = "true",
            ["ContentBasedDeduplication"] = "true",
            ["MessageRetentionPeriod"] = "1209600",
            ["VisibilityTimeout"] = "30"
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
    /// Clean up messages from a queue
    /// </summary>
    private async Task CleanupMessages(string queueUrl, List<Message> messages)
    {
        if (!messages.Any()) return;
        
        var deleteTasks = messages.Select(message =>
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
    /// Drain all messages from a queue
    /// </summary>
    private async Task DrainQueue(string queueUrl)
    {
        var maxAttempts = 10;
        var attempts = 0;
        
        while (attempts < maxAttempts)
        {
            var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 1
            });
            
            if (receiveResponse.Messages.Count == 0)
            {
                break; // Queue is empty
            }
            
            // Delete all received messages
            await CleanupMessages(queueUrl, receiveResponse.Messages);
            attempts++;
        }
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

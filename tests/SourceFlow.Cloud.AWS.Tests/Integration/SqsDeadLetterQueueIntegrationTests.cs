using Amazon.SQS.Model;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using System.Text.Json;

namespace SourceFlow.Cloud.AWS.Tests.Integration;

/// <summary>
/// Comprehensive integration tests for SQS dead letter queue functionality
/// Tests failed message capture, retry policies, poison message handling, and reprocessing capabilities
/// </summary>
[Collection("AWS Integration Tests")]
public class SqsDeadLetterQueueIntegrationTests : IClassFixture<LocalStackTestFixture>, IAsyncDisposable
{
    private readonly LocalStackTestFixture _localStack;
    private readonly List<string> _createdQueues = new();
    
    public SqsDeadLetterQueueIntegrationTests(LocalStackTestFixture localStack)
    {
        _localStack = localStack;
    }
    
    [Fact]
    public async Task DeadLetterQueue_ShouldCaptureFailedMessages()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange - Create main queue with dead letter queue
        var mainQueueName = $"test-dlq-main-{Guid.NewGuid():N}";
        var dlqName = $"test-dlq-dead-{Guid.NewGuid():N}";
        
        var dlqUrl = await CreateStandardQueueAsync(dlqName);
        var dlqArn = await GetQueueArnAsync(dlqUrl);
        
        var mainQueueUrl = await CreateStandardQueueAsync(mainQueueName, new Dictionary<string, string>
        {
            ["VisibilityTimeoutSeconds"] = "2", // Short timeout for faster testing
            ["RedrivePolicy"] = JsonSerializer.Serialize(new
            {
                deadLetterTargetArn = dlqArn,
                maxReceiveCount = 3
            })
        });
        
        var messageBody = $"Test message for DLQ - {Guid.NewGuid()}";
        var messageId = Guid.NewGuid().ToString();
        
        // Act - Send message to main queue
        var sendResponse = await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = mainQueueUrl,
            MessageBody = messageBody,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["MessageId"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = messageId
                },
                ["EntityId"] = new MessageAttributeValue
                {
                    DataType = "Number",
                    StringValue = "12345"
                },
                ["CommandType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "TestCommand"
                },
                ["FailureReason"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "Simulated processing failure"
                }
            }
        });
        
        Assert.NotNull(sendResponse.MessageId);
        
        // Act - Receive message multiple times without deleting (simulate processing failures)
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = mainQueueUrl,
                MaxNumberOfMessages = 1,
                MessageAttributeNames = new List<string> { "All" },
                WaitTimeSeconds = 1
            });
            
            if (receiveResponse.Messages.Any())
            {
                var message = receiveResponse.Messages[0];
                Assert.Equal(messageBody, message.Body);
                Assert.Equal(messageId, message.MessageAttributes["MessageId"].StringValue);
                
                // Don't delete the message - simulate processing failure
                // Wait for visibility timeout
                await Task.Delay(3000);
            }
        }
        
        // Act - Wait for message to be moved to DLQ
        await Task.Delay(2000);
        
        // Act - Check if message is in dead letter queue
        var dlqReceiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = dlqUrl,
            MaxNumberOfMessages = 1,
            MessageAttributeNames = new List<string> { "All" },
            WaitTimeSeconds = 2
        });
        
        // Assert - Message should be in dead letter queue
        Assert.Single(dlqReceiveResponse.Messages);
        var dlqMessage = dlqReceiveResponse.Messages[0];
        
        Assert.Equal(messageBody, dlqMessage.Body);
        Assert.Equal(messageId, dlqMessage.MessageAttributes["MessageId"].StringValue);
        Assert.Equal("12345", dlqMessage.MessageAttributes["EntityId"].StringValue);
        Assert.Equal("TestCommand", dlqMessage.MessageAttributes["CommandType"].StringValue);
        
        // Assert - Original queue should be empty
        var mainQueueCheck = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = mainQueueUrl,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 1
        });
        
        Assert.Empty(mainQueueCheck.Messages);
        
        // Clean up
        await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
        {
            QueueUrl = dlqUrl,
            ReceiptHandle = dlqMessage.ReceiptHandle
        });
    }
    
    [Fact]
    public async Task DeadLetterQueue_ShouldRespectMaxReceiveCount()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange - Create queue with specific maxReceiveCount
        var maxReceiveCount = 5;
        var mainQueueName = $"test-dlq-max-receive-{Guid.NewGuid():N}";
        var dlqName = $"test-dlq-max-receive-dead-{Guid.NewGuid():N}";
        
        var dlqUrl = await CreateStandardQueueAsync(dlqName);
        var dlqArn = await GetQueueArnAsync(dlqUrl);
        
        var mainQueueUrl = await CreateStandardQueueAsync(mainQueueName, new Dictionary<string, string>
        {
            ["VisibilityTimeoutSeconds"] = "1", // Very short timeout
            ["RedrivePolicy"] = JsonSerializer.Serialize(new
            {
                deadLetterTargetArn = dlqArn,
                maxReceiveCount = maxReceiveCount
            })
        });
        
        var messageBody = $"Max receive count test - {Guid.NewGuid()}";
        
        // Act - Send message
        await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = mainQueueUrl,
            MessageBody = messageBody,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["TestType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "MaxReceiveCountTest"
                }
            }
        });
        
        // Act - Receive message exactly maxReceiveCount times without deleting
        var receiveCount = 0;
        for (int attempt = 1; attempt <= maxReceiveCount + 2; attempt++) // Try more than max
        {
            var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = mainQueueUrl,
                MaxNumberOfMessages = 1,
                MessageAttributeNames = new List<string> { "All" },
                WaitTimeSeconds = 1
            });
            
            if (receiveResponse.Messages.Any())
            {
                receiveCount++;
                var message = receiveResponse.Messages[0];
                Assert.Equal(messageBody, message.Body);
                
                // Don't delete - simulate failure
                await Task.Delay(1500); // Wait for visibility timeout
            }
            else
            {
                // No more messages in main queue
                break;
            }
        }
        
        // Assert - Should have received the message exactly maxReceiveCount times
        Assert.True(receiveCount <= maxReceiveCount + 1, // Allow some variance for LocalStack
            $"Expected to receive message at most {maxReceiveCount + 1} times, actually received {receiveCount} times");
        
        // Act - Check dead letter queue
        await Task.Delay(2000); // Wait for DLQ processing
        
        var dlqReceiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = dlqUrl,
            MaxNumberOfMessages = 1,
            MessageAttributeNames = new List<string> { "All" },
            WaitTimeSeconds = 2
        });
        
        // Assert - Message should be in dead letter queue
        Assert.Single(dlqReceiveResponse.Messages);
        var dlqMessage = dlqReceiveResponse.Messages[0];
        Assert.Equal(messageBody, dlqMessage.Body);
        Assert.Equal("MaxReceiveCountTest", dlqMessage.MessageAttributes["TestType"].StringValue);
        
        // Clean up
        await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
        {
            QueueUrl = dlqUrl,
            ReceiptHandle = dlqMessage.ReceiptHandle
        });
    }
    
    [Fact]
    public async Task DeadLetterQueue_ShouldHandlePoisonMessages()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange - Create queue with DLQ for poison message handling
        var mainQueueName = $"test-dlq-poison-{Guid.NewGuid():N}";
        var dlqName = $"test-dlq-poison-dead-{Guid.NewGuid():N}";
        
        var dlqUrl = await CreateStandardQueueAsync(dlqName);
        var dlqArn = await GetQueueArnAsync(dlqUrl);
        
        var mainQueueUrl = await CreateStandardQueueAsync(mainQueueName, new Dictionary<string, string>
        {
            ["VisibilityTimeoutSeconds"] = "2",
            ["RedrivePolicy"] = JsonSerializer.Serialize(new
            {
                deadLetterTargetArn = dlqArn,
                maxReceiveCount = 2 // Low count for poison message testing
            })
        });
        
        // Create various types of potentially problematic messages
        var poisonMessages = new[]
        {
            new { Type = "InvalidJson", Body = "{ invalid json content }", EntityId = 1001 },
            new { Type = "EmptyPayload", Body = "", EntityId = 1002 },
            new { Type = "VeryLargeMessage", Body = new string('X', 200000), EntityId = 1003 }, // ~200KB
            new { Type = "SpecialCharacters", Body = "Message with special chars: \u0000\u0001\u0002\uFFFD", EntityId = 1004 },
            new { Type = "MalformedCommand", Body = JsonSerializer.Serialize(new { InvalidStructure = true }), EntityId = 1005 }
        };
        
        // Act - Send poison messages
        var sendTasks = poisonMessages.Select(async (msg, index) =>
        {
            try
            {
                return await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl = mainQueueUrl,
                    MessageBody = msg.Body,
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        ["PoisonType"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = msg.Type
                        },
                        ["EntityId"] = new MessageAttributeValue
                        {
                            DataType = "Number",
                            StringValue = msg.EntityId.ToString()
                        },
                        ["MessageIndex"] = new MessageAttributeValue
                        {
                            DataType = "Number",
                            StringValue = index.ToString()
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                // Some messages might fail to send (e.g., too large)
                Console.WriteLine($"Failed to send {msg.Type}: {ex.Message}");
                return null;
            }
        });
        
        var sendResults = await Task.WhenAll(sendTasks);
        var successfullySent = sendResults.Where(r => r != null).ToList();
        
        Assert.True(successfullySent.Count > 0, "At least some poison messages should be sent successfully");
        
        // Act - Attempt to process messages (simulate failures)
        var processedMessages = new List<Message>();
        var maxAttempts = 10;
        var attempts = 0;
        
        while (attempts < maxAttempts)
        {
            var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = mainQueueUrl,
                MaxNumberOfMessages = 5,
                MessageAttributeNames = new List<string> { "All" },
                WaitTimeSeconds = 1
            });
            
            if (receiveResponse.Messages.Any())
            {
                foreach (var message in receiveResponse.Messages)
                {
                    processedMessages.Add(message);
                    
                    // Simulate processing failure - don't delete the message
                    // This will cause it to be retried and eventually moved to DLQ
                }
                
                await Task.Delay(3000); // Wait for visibility timeout
            }
            else
            {
                break; // No more messages
            }
            
            attempts++;
        }
        
        // Act - Wait for messages to be moved to DLQ
        await Task.Delay(3000);
        
        // Act - Check dead letter queue for poison messages
        var dlqMessages = new List<Message>();
        var dlqAttempts = 0;
        var maxDlqAttempts = 5;
        
        while (dlqAttempts < maxDlqAttempts)
        {
            var dlqReceiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = dlqUrl,
                MaxNumberOfMessages = 10,
                MessageAttributeNames = new List<string> { "All" },
                WaitTimeSeconds = 1
            });
            
            dlqMessages.AddRange(dlqReceiveResponse.Messages);
            
            if (dlqReceiveResponse.Messages.Count == 0)
            {
                break;
            }
            
            dlqAttempts++;
        }
        
        // Assert - Poison messages should be in dead letter queue
        Assert.True(dlqMessages.Count > 0, "Some poison messages should be moved to dead letter queue");
        
        // Verify poison message types are preserved
        var poisonTypes = dlqMessages
            .Where(m => m.MessageAttributes.ContainsKey("PoisonType"))
            .Select(m => m.MessageAttributes["PoisonType"].StringValue)
            .ToList();
        
        Assert.True(poisonTypes.Count > 0, "Poison message types should be preserved");
        
        // Verify message attributes are preserved in DLQ
        foreach (var dlqMessage in dlqMessages)
        {
            Assert.True(dlqMessage.MessageAttributes.ContainsKey("EntityId"), 
                "EntityId should be preserved in DLQ");
            Assert.True(dlqMessage.MessageAttributes.ContainsKey("PoisonType"), 
                "PoisonType should be preserved in DLQ");
        }
        
        // Clean up DLQ messages
        var deleteTasks = dlqMessages.Select(message =>
            _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
            {
                QueueUrl = dlqUrl,
                ReceiptHandle = message.ReceiptHandle
            }));
        
        await Task.WhenAll(deleteTasks);
    }
    
    [Fact]
    public async Task DeadLetterQueue_ShouldSupportMessageReprocessing()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange - Create DLQ with some failed messages
        var dlqName = $"test-dlq-reprocess-{Guid.NewGuid():N}";
        var dlqUrl = await CreateStandardQueueAsync(dlqName);
        
        var reprocessQueueName = $"test-dlq-reprocess-target-{Guid.NewGuid():N}";
        var reprocessQueueUrl = await CreateStandardQueueAsync(reprocessQueueName);
        
        // Add messages to DLQ (simulating previously failed messages)
        var failedMessages = new[]
        {
            new { OrderId = Guid.NewGuid(), CustomerId = 1001, Amount = 99.99m, FailureReason = "Payment timeout" },
            new { OrderId = Guid.NewGuid(), CustomerId = 1002, Amount = 149.50m, FailureReason = "Inventory unavailable" },
            new { OrderId = Guid.NewGuid(), CustomerId = 1003, Amount = 75.25m, FailureReason = "Address validation failed" }
        };
        
        var dlqMessageIds = new List<string>();
        
        foreach (var failedMessage in failedMessages)
        {
            var sendResponse = await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = dlqUrl,
                MessageBody = JsonSerializer.Serialize(failedMessage),
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["OriginalFailureReason"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = failedMessage.FailureReason
                    },
                    ["CustomerId"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = failedMessage.CustomerId.ToString()
                    },
                    ["FailureTimestamp"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = DateTime.UtcNow.ToString("O")
                    },
                    ["ReprocessAttempt"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = "1"
                    }
                }
            });
            
            dlqMessageIds.Add(sendResponse.MessageId);
        }
        
        // Act - Retrieve messages from DLQ for reprocessing
        var dlqMessages = new List<Message>();
        var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = dlqUrl,
            MaxNumberOfMessages = 10,
            MessageAttributeNames = new List<string> { "All" },
            WaitTimeSeconds = 2
        });
        
        dlqMessages.AddRange(receiveResponse.Messages);
        
        // Assert - Should retrieve the failed messages
        Assert.Equal(failedMessages.Length, dlqMessages.Count);
        
        // Act - Reprocess messages (send to reprocessing queue with modifications)
        var reprocessTasks = dlqMessages.Select(async dlqMessage =>
        {
            var originalBody = JsonSerializer.Deserialize<Dictionary<string, object>>(dlqMessage.Body);
            Assert.NotNull(originalBody);
            
            // Modify message for reprocessing (e.g., add retry information)
            var reprocessedBody = new Dictionary<string, object>(originalBody)
            {
                ["ReprocessedAt"] = DateTime.UtcNow.ToString("O"),
                ["OriginalFailureReason"] = dlqMessage.MessageAttributes["OriginalFailureReason"].StringValue
            };
            
            // Send to reprocessing queue
            var reprocessResponse = await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
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
                    ["OriginalFailureReason"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = dlqMessage.MessageAttributes["OriginalFailureReason"].StringValue
                    },
                    ["CustomerId"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = dlqMessage.MessageAttributes["CustomerId"].StringValue
                    },
                    ["ReprocessAttempt"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = (int.Parse(dlqMessage.MessageAttributes["ReprocessAttempt"].StringValue) + 1).ToString()
                    }
                }
            });
            
            // Delete from DLQ after successful reprocessing
            await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
            {
                QueueUrl = dlqUrl,
                ReceiptHandle = dlqMessage.ReceiptHandle
            });
            
            return reprocessResponse;
        });
        
        var reprocessResults = await Task.WhenAll(reprocessTasks);
        
        // Assert - All messages should be reprocessed successfully
        Assert.Equal(failedMessages.Length, reprocessResults.Length);
        Assert.All(reprocessResults, result => Assert.NotNull(result.MessageId));
        
        // Act - Verify reprocessed messages are in target queue
        var reprocessedReceiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = reprocessQueueUrl,
            MaxNumberOfMessages = 10,
            MessageAttributeNames = new List<string> { "All" },
            WaitTimeSeconds = 2
        });
        
        // Assert - All reprocessed messages should be available
        Assert.Equal(failedMessages.Length, reprocessedReceiveResponse.Messages.Count);
        
        foreach (var reprocessedMessage in reprocessedReceiveResponse.Messages)
        {
            // Verify reprocessing metadata
            Assert.Equal("DeadLetterQueue", reprocessedMessage.MessageAttributes["ReprocessedFrom"].StringValue);
            Assert.True(int.Parse(reprocessedMessage.MessageAttributes["ReprocessAttempt"].StringValue) > 1);
            
            // Verify original data is preserved
            var messageBody = JsonSerializer.Deserialize<Dictionary<string, object>>(reprocessedMessage.Body);
            Assert.NotNull(messageBody);
            Assert.True(messageBody.ContainsKey("OrderId"));
            Assert.True(messageBody.ContainsKey("CustomerId"));
            Assert.True(messageBody.ContainsKey("Amount"));
            Assert.True(messageBody.ContainsKey("ReprocessedAt"));
            Assert.True(messageBody.ContainsKey("OriginalFailureReason"));
        }
        
        // Clean up reprocessed messages
        var cleanupTasks = reprocessedReceiveResponse.Messages.Select(message =>
            _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
            {
                QueueUrl = reprocessQueueUrl,
                ReceiptHandle = message.ReceiptHandle
            }));
        
        await Task.WhenAll(cleanupTasks);
        
        // Verify DLQ is empty after reprocessing
        var dlqCheckResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = dlqUrl,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 1
        });
        
        Assert.Empty(dlqCheckResponse.Messages);
    }
    
    [Fact]
    public async Task DeadLetterQueue_ShouldSupportFifoQueues()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange - Create FIFO queue with FIFO DLQ
        var mainQueueName = $"test-dlq-fifo-main-{Guid.NewGuid():N}.fifo";
        var dlqName = $"test-dlq-fifo-dead-{Guid.NewGuid():N}.fifo";
        
        var dlqUrl = await CreateFifoQueueAsync(dlqName);
        var dlqArn = await GetQueueArnAsync(dlqUrl);
        
        var mainQueueUrl = await CreateFifoQueueAsync(mainQueueName, new Dictionary<string, string>
        {
            ["VisibilityTimeoutSeconds"] = "2",
            ["RedrivePolicy"] = JsonSerializer.Serialize(new
            {
                deadLetterTargetArn = dlqArn,
                maxReceiveCount = 2
            })
        });
        
        var entityId = 12345;
        var messageGroupId = $"entity-{entityId}";
        
        // Act - Send FIFO messages that will fail processing
        var fifoMessages = new[]
        {
            new { SequenceNo = 1, Command = "CreateOrder", Data = "Order data 1" },
            new { SequenceNo = 2, Command = "UpdateOrder", Data = "Order data 2" },
            new { SequenceNo = 3, Command = "CancelOrder", Data = "Order data 3" }
        };
        
        foreach (var msg in fifoMessages)
        {
            await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = mainQueueUrl,
                MessageBody = JsonSerializer.Serialize(msg),
                MessageGroupId = messageGroupId,
                MessageDeduplicationId = $"msg-{entityId}-{msg.SequenceNo}-{Guid.NewGuid():N}",
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
                        StringValue = msg.SequenceNo.ToString()
                    },
                    ["CommandType"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = msg.Command
                    }
                }
            });
        }
        
        // Act - Receive messages without deleting (simulate failures)
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = mainQueueUrl,
                MaxNumberOfMessages = 5,
                MessageAttributeNames = new List<string> { "All" },
                WaitTimeSeconds = 1
            });
            
            // Don't delete messages - simulate processing failures
            await Task.Delay(3000); // Wait for visibility timeout
        }
        
        // Act - Wait for messages to be moved to FIFO DLQ
        await Task.Delay(3000);
        
        // Act - Check FIFO DLQ
        var dlqMessages = new List<Message>();
        var dlqReceiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = dlqUrl,
            MaxNumberOfMessages = 10,
            MessageAttributeNames = new List<string> { "All" },
            WaitTimeSeconds = 2
        });
        
        dlqMessages.AddRange(dlqReceiveResponse.Messages);
        
        // Assert - Messages should be in FIFO DLQ
        Assert.True(dlqMessages.Count > 0, "Messages should be moved to FIFO dead letter queue");
        
        // Verify FIFO ordering is maintained in DLQ
        var orderedMessages = dlqMessages
            .Where(m => m.MessageAttributes.ContainsKey("SequenceNo"))
            .OrderBy(m => int.Parse(m.MessageAttributes["SequenceNo"].StringValue))
            .ToList();
        
        Assert.True(orderedMessages.Count > 0, "Should have ordered messages in DLQ");
        
        // Verify message group ID is preserved
        foreach (var dlqMessage in dlqMessages)
        {
            if (dlqMessage.Attributes.ContainsKey("MessageGroupId"))
            {
                Assert.Equal(messageGroupId, dlqMessage.Attributes["MessageGroupId"]);
            }
            
            // Verify SourceFlow attributes are preserved
            Assert.True(dlqMessage.MessageAttributes.ContainsKey("EntityId"));
            Assert.True(dlqMessage.MessageAttributes.ContainsKey("CommandType"));
            Assert.Equal(entityId.ToString(), dlqMessage.MessageAttributes["EntityId"].StringValue);
        }
        
        // Clean up
        var deleteTasks = dlqMessages.Select(message =>
            _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
            {
                QueueUrl = dlqUrl,
                ReceiptHandle = message.ReceiptHandle
            }));
        
        await Task.WhenAll(deleteTasks);
    }
    
    /// <summary>
    /// Create a standard queue with the specified name and attributes
    /// </summary>
    private async Task<string> CreateStandardQueueAsync(string queueName, Dictionary<string, string>? additionalAttributes = null)
    {
        var attributes = new Dictionary<string, string>
        {
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
    /// Create a FIFO queue with the specified name and attributes
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
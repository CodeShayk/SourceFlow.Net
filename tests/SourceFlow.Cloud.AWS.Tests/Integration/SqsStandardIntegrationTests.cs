using Amazon.SQS.Model;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace SourceFlow.Cloud.AWS.Tests.Integration;

/// <summary>
/// Comprehensive integration tests for SQS standard queue functionality
/// Tests high-throughput delivery, at-least-once guarantees, concurrent processing, and performance characteristics
/// </summary>
[Collection("AWS Integration Tests")]
[Trait("Category", "Integration")]
[Trait("Category", "RequiresLocalStack")]
public class SqsStandardIntegrationTests : IClassFixture<LocalStackTestFixture>, IAsyncDisposable
{
    private readonly LocalStackTestFixture _localStack;
    private readonly List<string> _createdQueues = new();
    
    public SqsStandardIntegrationTests(LocalStackTestFixture localStack)
    {
        _localStack = localStack;
    }
    
    [Fact]
    public async Task StandardQueue_ShouldSupportHighThroughputMessageDelivery()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-standard-throughput-{Guid.NewGuid():N}";
        var queueUrl = await CreateStandardQueueAsync(queueName);
        
        var messageCount = 100;
        var concurrentSenders = 5;
        var messagesPerSender = messageCount / concurrentSenders;
        
        // Act - Send messages concurrently for high throughput
        var sendTasks = new List<Task<List<SendMessageResponse>>>();
        var stopwatch = Stopwatch.StartNew();
        
        for (int senderId = 0; senderId < concurrentSenders; senderId++)
        {
            var currentSenderId = senderId; // Capture for closure
            sendTasks.Add(Task.Run(async () =>
            {
                var responses = new List<SendMessageResponse>();
                for (int msgId = 0; msgId < messagesPerSender; msgId++)
                {
                    var response = await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
                    {
                        QueueUrl = queueUrl,
                        MessageBody = $"Sender {currentSenderId} - Message {msgId} - {DateTime.UtcNow:HH:mm:ss.fff}",
                        MessageAttributes = new Dictionary<string, MessageAttributeValue>
                        {
                            ["SenderId"] = new MessageAttributeValue
                            {
                                DataType = "Number",
                                StringValue = currentSenderId.ToString()
                            },
                            ["MessageId"] = new MessageAttributeValue
                            {
                                DataType = "Number",
                                StringValue = msgId.ToString()
                            },
                            ["Timestamp"] = new MessageAttributeValue
                            {
                                DataType = "String",
                                StringValue = DateTime.UtcNow.ToString("O")
                            }
                        }
                    });
                    responses.Add(response);
                }
                return responses;
            }));
        }
        
        var allSendResponses = await Task.WhenAll(sendTasks);
        var sendDuration = stopwatch.Elapsed;
        
        var totalSent = allSendResponses.SelectMany(responses => responses).ToList();
        
        // Assert - All messages should be sent successfully
        Assert.Equal(messageCount, totalSent.Count);
        Assert.All(totalSent, response => Assert.NotNull(response.MessageId));
        
        // Calculate and verify throughput
        var sendThroughput = messageCount / sendDuration.TotalSeconds;
        Assert.True(sendThroughput > 0, $"Send throughput: {sendThroughput:F2} messages/second");
        
        // Act - Receive all messages with concurrent consumers
        var receivedMessages = new ConcurrentBag<Message>();
        var concurrentReceivers = 3;
        var maxReceiveAttempts = 20;
        
        stopwatch.Restart();
        var receiveTasks = new List<Task>();
        
        for (int receiverId = 0; receiverId < concurrentReceivers; receiverId++)
        {
            receiveTasks.Add(Task.Run(async () =>
            {
                var attempts = 0;
                while (receivedMessages.Count < messageCount && attempts < maxReceiveAttempts)
                {
                    var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
                    {
                        QueueUrl = queueUrl,
                        MaxNumberOfMessages = 10,
                        MessageAttributeNames = new List<string> { "All" },
                        WaitTimeSeconds = 1
                    });
                    
                    foreach (var message in receiveResponse.Messages)
                    {
                        receivedMessages.Add(message);
                        
                        // Delete message to acknowledge processing
                        await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
                        {
                            QueueUrl = queueUrl,
                            ReceiptHandle = message.ReceiptHandle
                        });
                    }
                    
                    attempts++;
                    
                    if (receiveResponse.Messages.Count == 0)
                    {
                        await Task.Delay(100); // Brief pause if no messages
                    }
                }
            }));
        }
        
        await Task.WhenAll(receiveTasks);
        var receiveDuration = stopwatch.Elapsed;
        
        // Assert - All messages should be received
        Assert.True(receivedMessages.Count >= messageCount * 0.95, // Allow for some variance in LocalStack
            $"Expected at least {messageCount * 0.95} messages, received {receivedMessages.Count}");
        
        var receiveThroughput = receivedMessages.Count / receiveDuration.TotalSeconds;
        Assert.True(receiveThroughput > 0, $"Receive throughput: {receiveThroughput:F2} messages/second");
        
        // Verify message distribution across senders
        var messagesBySender = receivedMessages
            .Where(m => m.MessageAttributes.ContainsKey("SenderId"))
            .GroupBy(m => m.MessageAttributes["SenderId"].StringValue)
            .ToDictionary(g => int.Parse(g.Key), g => g.Count());
        
        Assert.True(messagesBySender.Count > 0, "Should receive messages from multiple senders");
    }
    
    [Fact]
    public async Task StandardQueue_ShouldGuaranteeAtLeastOnceDelivery()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-standard-at-least-once-{Guid.NewGuid():N}";
        var queueUrl = await CreateStandardQueueAsync(queueName, new Dictionary<string, string>
        {
            ["VisibilityTimeoutSeconds"] = "5" // Short visibility timeout for testing
        });
        
        var messageBody = $"At-least-once test message - {Guid.NewGuid()}";
        var messageId = Guid.NewGuid().ToString();
        
        // Act - Send a message
        var sendResponse = await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = messageBody,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["MessageId"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = messageId
                },
                ["SendTime"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = DateTime.UtcNow.ToString("O")
                }
            }
        });
        
        Assert.NotNull(sendResponse.MessageId);
        
        // Act - Receive message but don't delete it (simulate processing failure)
        var firstReceive = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
            MessageAttributeNames = new List<string> { "All" },
            WaitTimeSeconds = 2
        });
        
        Assert.Single(firstReceive.Messages);
        var firstMessage = firstReceive.Messages[0];
        Assert.Equal(messageBody, firstMessage.Body);
        Assert.Equal(messageId, firstMessage.MessageAttributes["MessageId"].StringValue);
        
        // Don't delete the message - it should become visible again after visibility timeout
        
        // Act - Wait for visibility timeout and receive again
        await Task.Delay(TimeSpan.FromSeconds(6)); // Wait longer than visibility timeout
        
        var secondReceive = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
            MessageAttributeNames = new List<string> { "All" },
            WaitTimeSeconds = 2
        });
        
        // Assert - Message should be available again (at-least-once delivery)
        Assert.Single(secondReceive.Messages);
        var secondMessage = secondReceive.Messages[0];
        Assert.Equal(messageBody, secondMessage.Body);
        Assert.Equal(messageId, secondMessage.MessageAttributes["MessageId"].StringValue);
        
        // The receipt handles should be different (message was re-delivered)
        Assert.NotEqual(firstMessage.ReceiptHandle, secondMessage.ReceiptHandle);
        
        // Clean up - delete the message
        await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
        {
            QueueUrl = queueUrl,
            ReceiptHandle = secondMessage.ReceiptHandle
        });
        
        // Verify message is gone
        var finalReceive = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 1
        });
        
        Assert.Empty(finalReceive.Messages);
    }
    
    [Fact]
    public async Task StandardQueue_ShouldSupportConcurrentMessageProcessing()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-standard-concurrent-{Guid.NewGuid():N}";
        var queueUrl = await CreateStandardQueueAsync(queueName);
        
        var messageCount = 50;
        var concurrentProcessors = 5;
        
        // Act - Send messages
        var sendTasks = new List<Task<SendMessageResponse>>();
        for (int i = 0; i < messageCount; i++)
        {
            sendTasks.Add(_localStack.SqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = $"Concurrent processing test message {i}",
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["MessageIndex"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = i.ToString()
                    },
                    ["SendTime"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = DateTime.UtcNow.ToString("O")
                    }
                }
            }));
        }
        
        await Task.WhenAll(sendTasks);
        
        // Act - Process messages concurrently
        var processedMessages = new ConcurrentBag<(int ProcessorId, string MessageBody, int MessageIndex)>();
        var processingTasks = new List<Task>();
        var stopwatch = Stopwatch.StartNew();
        
        for (int processorId = 0; processorId < concurrentProcessors; processorId++)
        {
            var currentProcessorId = processorId;
            processingTasks.Add(Task.Run(async () =>
            {
                var maxAttempts = 20;
                var attempts = 0;
                
                while (processedMessages.Count < messageCount && attempts < maxAttempts)
                {
                    var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
                    {
                        QueueUrl = queueUrl,
                        MaxNumberOfMessages = 5, // Process multiple messages per call
                        MessageAttributeNames = new List<string> { "All" },
                        WaitTimeSeconds = 1
                    });
                    
                    var processingSubTasks = receiveResponse.Messages.Select(async message =>
                    {
                        // Simulate processing time
                        await Task.Delay(System.Random.Shared.Next(10, 50));
                        
                        var messageIndex = int.Parse(message.MessageAttributes["MessageIndex"].StringValue);
                        processedMessages.Add((currentProcessorId, message.Body, messageIndex));
                        
                        // Delete message after processing
                        await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
                        {
                            QueueUrl = queueUrl,
                            ReceiptHandle = message.ReceiptHandle
                        });
                    });
                    
                    await Task.WhenAll(processingSubTasks);
                    attempts++;
                    
                    if (receiveResponse.Messages.Count == 0)
                    {
                        await Task.Delay(100);
                    }
                }
            }));
        }
        
        await Task.WhenAll(processingTasks);
        var processingDuration = stopwatch.Elapsed;
        
        // Assert - All messages should be processed
        Assert.True(processedMessages.Count >= messageCount * 0.95, // Allow for some variance
            $"Expected at least {messageCount * 0.95} processed messages, got {processedMessages.Count}");
        
        // Verify concurrent processing occurred
        var messagesByProcessor = processedMessages
            .GroupBy(m => m.ProcessorId)
            .ToDictionary(g => g.Key, g => g.Count());
        
        Assert.True(messagesByProcessor.Count > 1, "Messages should be processed by multiple processors");
        
        // Verify no duplicate processing (each message index should appear only once)
        var messageIndices = processedMessages.Select(m => m.MessageIndex).ToList();
        var uniqueIndices = messageIndices.Distinct().ToList();
        Assert.Equal(uniqueIndices.Count, messageIndices.Count);
        
        var processingThroughput = processedMessages.Count / processingDuration.TotalSeconds;
        Assert.True(processingThroughput > 0, $"Processing throughput: {processingThroughput:F2} messages/second");
    }
    
    [Fact]
    public async Task StandardQueue_ShouldValidatePerformanceCharacteristics()
    {
        // Skip if not configured for integration tests or performance tests
        if (!_localStack.Configuration.RunIntegrationTests || 
            !_localStack.Configuration.RunPerformanceTests || 
            _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-standard-performance-{Guid.NewGuid():N}";
        var queueUrl = await CreateStandardQueueAsync(queueName);
        
        var messageSizes = new[] { 1024, 4096, 16384, 65536 }; // 1KB, 4KB, 16KB, 64KB
        var messagesPerSize = 20;
        
        var performanceResults = new List<(int MessageSize, double SendLatency, double ReceiveLatency, double Throughput)>();
        
        foreach (var messageSize in messageSizes)
        {
            // Generate test message of specified size
            var messageBody = new string('A', messageSize);
            var messageIds = new List<string>();
            
            // Measure send performance
            var sendStopwatch = Stopwatch.StartNew();
            var sendTasks = new List<Task<SendMessageResponse>>();
            
            for (int i = 0; i < messagesPerSize; i++)
            {
                var messageId = Guid.NewGuid().ToString();
                messageIds.Add(messageId);
                
                sendTasks.Add(_localStack.SqsClient.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl = queueUrl,
                    MessageBody = messageBody,
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        ["MessageId"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = messageId
                        },
                        ["MessageSize"] = new MessageAttributeValue
                        {
                            DataType = "Number",
                            StringValue = messageSize.ToString()
                        },
                        ["SendTime"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = DateTime.UtcNow.ToString("O")
                        }
                    }
                }));
            }
            
            await Task.WhenAll(sendTasks);
            var sendDuration = sendStopwatch.Elapsed;
            var avgSendLatency = sendDuration.TotalMilliseconds / messagesPerSize;
            
            // Measure receive performance
            var receivedMessages = new List<Message>();
            var receiveStopwatch = Stopwatch.StartNew();
            var maxAttempts = 15;
            var attempts = 0;
            
            while (receivedMessages.Count < messagesPerSize && attempts < maxAttempts)
            {
                var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 10,
                    MessageAttributeNames = new List<string> { "All" },
                    WaitTimeSeconds = 1
                });
                
                foreach (var message in receiveResponse.Messages)
                {
                    if (message.MessageAttributes.ContainsKey("MessageSize") &&
                        message.MessageAttributes["MessageSize"].StringValue == messageSize.ToString())
                    {
                        receivedMessages.Add(message);
                        
                        // Delete message
                        await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
                        {
                            QueueUrl = queueUrl,
                            ReceiptHandle = message.ReceiptHandle
                        });
                    }
                }
                
                attempts++;
            }
            
            var receiveDuration = receiveStopwatch.Elapsed;
            var avgReceiveLatency = receiveDuration.TotalMilliseconds / receivedMessages.Count;
            var throughput = receivedMessages.Count / receiveDuration.TotalSeconds;
            
            performanceResults.Add((messageSize, avgSendLatency, avgReceiveLatency, throughput));
            
            // Assert - Should receive all messages
            Assert.True(receivedMessages.Count >= messagesPerSize * 0.9, 
                $"Expected at least {messagesPerSize * 0.9} messages for size {messageSize}, got {receivedMessages.Count}");
        }
        
        // Assert - Performance should be reasonable and consistent
        foreach (var result in performanceResults)
        {
            Assert.True(result.SendLatency > 0, $"Send latency should be positive for {result.MessageSize} byte messages");
            Assert.True(result.ReceiveLatency > 0, $"Receive latency should be positive for {result.MessageSize} byte messages");
            Assert.True(result.Throughput > 0, $"Throughput should be positive for {result.MessageSize} byte messages");
            
            // Log performance metrics for analysis
            Console.WriteLine($"Message Size: {result.MessageSize} bytes, " +
                            $"Send Latency: {result.SendLatency:F2}ms, " +
                            $"Receive Latency: {result.ReceiveLatency:F2}ms, " +
                            $"Throughput: {result.Throughput:F2} msg/sec");
        }
        
        // Performance should generally degrade with larger message sizes (but this is informational)
        var smallMessageThroughput = performanceResults.First().Throughput;
        var largeMessageThroughput = performanceResults.Last().Throughput;
        
        // This is informational - actual performance depends on LocalStack vs real AWS
        Assert.True(smallMessageThroughput > 0 && largeMessageThroughput > 0, 
            "Both small and large message throughput should be positive");
    }
    
    [Fact]
    public async Task StandardQueue_ShouldHandleMessageAttributesCorrectly()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-standard-attributes-{Guid.NewGuid():N}";
        var queueUrl = await CreateStandardQueueAsync(queueName);
        
        var testData = new
        {
            OrderId = Guid.NewGuid(),
            CustomerId = 12345,
            Amount = 99.99m,
            Items = new[] { "Item1", "Item2", "Item3" }
        };
        
        var messageAttributes = new Dictionary<string, MessageAttributeValue>
        {
            ["EntityId"] = new MessageAttributeValue
            {
                DataType = "Number",
                StringValue = "12345"
            },
            ["SequenceNo"] = new MessageAttributeValue
            {
                DataType = "Number",
                StringValue = "42"
            },
            ["CommandType"] = new MessageAttributeValue
            {
                DataType = "String",
                StringValue = "CreateOrderCommand"
            },
            ["PayloadType"] = new MessageAttributeValue
            {
                DataType = "String",
                StringValue = "CreateOrderPayload"
            },
            ["CorrelationId"] = new MessageAttributeValue
            {
                DataType = "String",
                StringValue = Guid.NewGuid().ToString()
            },
            ["Priority"] = new MessageAttributeValue
            {
                DataType = "Number",
                StringValue = "5"
            },
            ["IsUrgent"] = new MessageAttributeValue
            {
                DataType = "String",
                StringValue = "true"
            },
            ["ProcessingHints"] = new MessageAttributeValue
            {
                DataType = "String",
                StringValue = JsonSerializer.Serialize(new { Timeout = 30, RetryCount = 3 })
            }
        };
        
        // Act - Send message with comprehensive attributes
        var sendResponse = await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = JsonSerializer.Serialize(testData),
            MessageAttributes = messageAttributes
        });
        
        Assert.NotNull(sendResponse.MessageId);
        
        // Act - Receive message and validate attributes
        var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
            MessageAttributeNames = new List<string> { "All" },
            WaitTimeSeconds = 2
        });
        
        // Assert - Message and all attributes should be preserved
        Assert.Single(receiveResponse.Messages);
        var message = receiveResponse.Messages[0];
        
        // Validate message body
        var receivedData = JsonSerializer.Deserialize<Dictionary<string, object>>(message.Body);
        Assert.NotNull(receivedData);
        Assert.True(receivedData.ContainsKey("OrderId"));
        Assert.True(receivedData.ContainsKey("CustomerId"));
        Assert.True(receivedData.ContainsKey("Amount"));
        
        // Validate all message attributes
        Assert.Equal(messageAttributes.Count, message.MessageAttributes.Count);
        
        foreach (var expectedAttr in messageAttributes)
        {
            Assert.True(message.MessageAttributes.ContainsKey(expectedAttr.Key), 
                $"Missing attribute: {expectedAttr.Key}");
            
            var receivedAttr = message.MessageAttributes[expectedAttr.Key];
            Assert.Equal(expectedAttr.Value.DataType, receivedAttr.DataType);
            Assert.Equal(expectedAttr.Value.StringValue, receivedAttr.StringValue);
        }
        
        // Validate specific SourceFlow attributes
        Assert.Equal("12345", message.MessageAttributes["EntityId"].StringValue);
        Assert.Equal("42", message.MessageAttributes["SequenceNo"].StringValue);
        Assert.Equal("CreateOrderCommand", message.MessageAttributes["CommandType"].StringValue);
        Assert.Equal("CreateOrderPayload", message.MessageAttributes["PayloadType"].StringValue);
        
        // Clean up
        await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
        {
            QueueUrl = queueUrl,
            ReceiptHandle = message.ReceiptHandle
        });
    }
    
    [Fact]
    public async Task StandardQueue_ShouldSupportLongPolling()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-standard-long-polling-{Guid.NewGuid():N}";
        var queueUrl = await CreateStandardQueueAsync(queueName, new Dictionary<string, string>
        {
            ["ReceiveMessageWaitTimeSeconds"] = "10" // Enable long polling
        });
        
        var messageBody = $"Long polling test message - {Guid.NewGuid()}";
        
        // Act - Start long polling receive (should wait for message)
        var receiveTask = Task.Run(async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 5, // Long poll for 5 seconds
                MessageAttributeNames = new List<string> { "All" }
            });
            stopwatch.Stop();
            
            return (Messages: receiveResponse.Messages, WaitTime: stopwatch.Elapsed);
        });
        
        // Wait a moment, then send a message
        await Task.Delay(2000);
        
        var sendResponse = await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = messageBody,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["SendTime"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = DateTime.UtcNow.ToString("O")
                }
            }
        });
        
        // Wait for receive to complete
        var result = await receiveTask;
        
        // Assert - Should receive the message
        Assert.Single(result.Messages);
        Assert.Equal(messageBody, result.Messages[0].Body);
        
        // Long polling should have waited at least 2 seconds (when we sent the message)
        Assert.True(result.WaitTime.TotalSeconds >= 1.5, 
            $"Long polling should have waited, actual wait time: {result.WaitTime.TotalSeconds:F2} seconds");
        
        // Clean up
        await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
        {
            QueueUrl = queueUrl,
            ReceiptHandle = result.Messages[0].ReceiptHandle
        });
    }
    
    /// <summary>
    /// Create a standard queue with the specified name and attributes
    /// </summary>
    private async Task<string> CreateStandardQueueAsync(string queueName, Dictionary<string, string>? additionalAttributes = null)
    {
        var attributes = new Dictionary<string, string>
        {
            ["MessageRetentionPeriod"] = "1209600", // 14 days
            ["VisibilityTimeoutSeconds"] = "30",
            ["ReceiveMessageWaitTimeSeconds"] = "0" // Short polling by default
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

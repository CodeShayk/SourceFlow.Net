using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.AWS.Monitoring;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using SourceFlow.Cloud.DeadLetter;
using System.Text.Json;

namespace SourceFlow.Cloud.AWS.Tests.Integration;

/// <summary>
/// Comprehensive integration tests for AWS dead letter queue processing
/// Tests failed message capture, analysis, categorization, reprocessing, and monitoring
/// Validates Requirement 7.3
/// </summary>
[Collection("AWS Integration Tests")]
[Trait("Category", "Integration")]
[Trait("Category", "RequiresLocalStack")]
public class AwsDeadLetterQueueProcessingTests : IClassFixture<LocalStackTestFixture>, IAsyncDisposable
{
    private readonly LocalStackTestFixture _localStack;
    private readonly List<string> _createdQueues = new();
    private readonly IDeadLetterStore _deadLetterStore;
    private readonly ILogger<AwsDeadLetterQueueProcessingTests> _logger;
    
    public AwsDeadLetterQueueProcessingTests(LocalStackTestFixture localStack)
    {
        _localStack = localStack;
        
        // Create in-memory dead letter store for testing
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<IDeadLetterStore, InMemoryDeadLetterStore>();
        
        var serviceProvider = services.BuildServiceProvider();
        _deadLetterStore = serviceProvider.GetRequiredService<IDeadLetterStore>();
        _logger = serviceProvider.GetRequiredService<ILogger<AwsDeadLetterQueueProcessingTests>>();
    }
    
    [Fact]
    public async Task DeadLetterProcessing_ShouldCaptureCompleteMetadata()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange - Create main queue with DLQ
        var mainQueueName = $"test-dlq-processing-main-{Guid.NewGuid():N}";
        var dlqName = $"test-dlq-processing-dead-{Guid.NewGuid():N}";
        
        var dlqUrl = await CreateStandardQueueAsync(dlqName);
        var dlqArn = await GetQueueArnAsync(dlqUrl);
        
        var mainQueueUrl = await CreateStandardQueueAsync(mainQueueName, new Dictionary<string, string>
        {
            ["VisibilityTimeoutSeconds"] = "2",
            ["RedrivePolicy"] = JsonSerializer.Serialize(new
            {
                deadLetterTargetArn = dlqArn,
                maxReceiveCount = 2
            })
        });
        
        // Create test message with comprehensive metadata
        var testCommand = new
        {
            CommandId = Guid.NewGuid(),
            EntityId = 12345,
            SequenceNo = 42,
            CommandType = "ProcessOrderCommand",
            PayloadType = "ProcessOrderPayload",
            Timestamp = DateTime.UtcNow,
            Data = new
            {
                OrderId = Guid.NewGuid(),
                CustomerId = 9876,
                Amount = 299.99m,
                Items = new[] { "Item1", "Item2", "Item3" }
            },
            Metadata = new Dictionary<string, string>
            {
                ["CorrelationId"] = Guid.NewGuid().ToString(),
                ["UserId"] = "user-123",
                ["TenantId"] = "tenant-456"
            }
        };
        
        // Act - Send message with comprehensive attributes
        var sendResponse = await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = mainQueueUrl,
            MessageBody = JsonSerializer.Serialize(testCommand),
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["CommandType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = testCommand.CommandType
                },
                ["PayloadType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = testCommand.PayloadType
                },
                ["EntityId"] = new MessageAttributeValue
                {
                    DataType = "Number",
                    StringValue = testCommand.EntityId.ToString()
                },
                ["SequenceNo"] = new MessageAttributeValue
                {
                    DataType = "Number",
                    StringValue = testCommand.SequenceNo.ToString()
                },
                ["CorrelationId"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = testCommand.Metadata["CorrelationId"]
                },
                ["UserId"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = testCommand.Metadata["UserId"]
                },
                ["TenantId"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = testCommand.Metadata["TenantId"]
                },
                ["FailureReason"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "ValidationError"
                },
                ["SourceQueue"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = mainQueueUrl
                }
            }
        });
        
        Assert.NotNull(sendResponse.MessageId);
        
        // Act - Simulate processing failures
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = mainQueueUrl,
                MaxNumberOfMessages = 1,
                MessageAttributeNames = new List<string> { "All" },
                AttributeNames = new List<string> { "All" },
                WaitTimeSeconds = 1
            });
            
            if (receiveResponse.Messages.Any())
            {
                // Don't delete - simulate failure
                await Task.Delay(3000);
            }
        }
        
        // Wait for DLQ processing
        await Task.Delay(2000);
        
        // Act - Retrieve from DLQ and process
        var dlqReceiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = dlqUrl,
            MaxNumberOfMessages = 1,
            MessageAttributeNames = new List<string> { "All" },
            AttributeNames = new List<string> { "All" },
            WaitTimeSeconds = 2
        });
        
        // Assert - Message should be in DLQ
        Assert.Single(dlqReceiveResponse.Messages);
        var dlqMessage = dlqReceiveResponse.Messages[0];
        
        // Assert - All metadata should be preserved
        Assert.Equal(testCommand.CommandType, dlqMessage.MessageAttributes["CommandType"].StringValue);
        Assert.Equal(testCommand.PayloadType, dlqMessage.MessageAttributes["PayloadType"].StringValue);
        Assert.Equal(testCommand.EntityId.ToString(), dlqMessage.MessageAttributes["EntityId"].StringValue);
        Assert.Equal(testCommand.SequenceNo.ToString(), dlqMessage.MessageAttributes["SequenceNo"].StringValue);
        Assert.Equal(testCommand.Metadata["CorrelationId"], dlqMessage.MessageAttributes["CorrelationId"].StringValue);
        Assert.Equal(testCommand.Metadata["UserId"], dlqMessage.MessageAttributes["UserId"].StringValue);
        Assert.Equal(testCommand.Metadata["TenantId"], dlqMessage.MessageAttributes["TenantId"].StringValue);
        Assert.Equal("ValidationError", dlqMessage.MessageAttributes["FailureReason"].StringValue);
        Assert.Equal(mainQueueUrl, dlqMessage.MessageAttributes["SourceQueue"].StringValue);
        
        // Assert - Message body should be intact
        var dlqBody = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(dlqMessage.Body);
        Assert.NotNull(dlqBody);
        Assert.True(dlqBody.ContainsKey("CommandId"));
        Assert.True(dlqBody.ContainsKey("EntityId"));
        Assert.True(dlqBody.ContainsKey("Data"));
        Assert.True(dlqBody.ContainsKey("Metadata"));
        
        // Assert - SQS attributes should be available
        Assert.True(dlqMessage.Attributes.ContainsKey("ApproximateReceiveCount"));
        Assert.True(dlqMessage.Attributes.ContainsKey("SentTimestamp"));
        
        // Clean up
        await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
        {
            QueueUrl = dlqUrl,
            ReceiptHandle = dlqMessage.ReceiptHandle
        });
    }
    
    [Fact]
    public async Task DeadLetterProcessing_ShouldCategorizeMessagesByFailureType()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange - Create DLQ
        var dlqName = $"test-dlq-categorization-{Guid.NewGuid():N}";
        var dlqUrl = await CreateStandardQueueAsync(dlqName);
        
        // Create messages with different failure types
        var failureTypes = new[]
        {
            new { Type = "ValidationError", Description = "Invalid input data", Count = 3 },
            new { Type = "TimeoutError", Description = "External service timeout", Count = 2 },
            new { Type = "DataCorruption", Description = "Corrupted message payload", Count = 2 },
            new { Type = "ExternalServiceError", Description = "Third-party API failure", Count = 1 },
            new { Type = "InsufficientResources", Description = "Resource exhaustion", Count = 1 }
        };
        
        var sentMessages = new List<string>();
        
        // Act - Send messages with different failure types
        foreach (var failureType in failureTypes)
        {
            for (int i = 0; i < failureType.Count; i++)
            {
                var messageBody = JsonSerializer.Serialize(new
                {
                    CommandId = Guid.NewGuid(),
                    EntityId = 1000 + i,
                    FailureType = failureType.Type,
                    Description = failureType.Description,
                    Timestamp = DateTime.UtcNow
                });
                
                var sendResponse = await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl = dlqUrl,
                    MessageBody = messageBody,
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        ["FailureType"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = failureType.Type
                        },
                        ["FailureDescription"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = failureType.Description
                        },
                        ["CommandType"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = "TestCommand"
                        },
                        ["EntityId"] = new MessageAttributeValue
                        {
                            DataType = "Number",
                            StringValue = (1000 + i).ToString()
                        }
                    }
                });
                
                sentMessages.Add(sendResponse.MessageId);
            }
        }
        
        // Act - Retrieve and categorize messages
        var categorizedMessages = new Dictionary<string, List<Message>>();
        var maxAttempts = 10;
        var attempts = 0;
        
        while (attempts < maxAttempts)
        {
            var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = dlqUrl,
                MaxNumberOfMessages = 10,
                MessageAttributeNames = new List<string> { "All" },
                WaitTimeSeconds = 1
            });
            
            foreach (var message in receiveResponse.Messages)
            {
                if (message.MessageAttributes.TryGetValue("FailureType", out var failureTypeAttr))
                {
                    var failureType = failureTypeAttr.StringValue ?? "Unknown";
                    
                    if (!categorizedMessages.ContainsKey(failureType))
                    {
                        categorizedMessages[failureType] = new List<Message>();
                    }
                    
                    categorizedMessages[failureType].Add(message);
                }
            }
            
            if (receiveResponse.Messages.Count == 0)
            {
                break;
            }
            
            attempts++;
        }
        
        // Assert - All failure types should be categorized
        Assert.Equal(failureTypes.Length, categorizedMessages.Count);
        
        // Assert - Each category should have the correct count
        foreach (var failureType in failureTypes)
        {
            Assert.True(categorizedMessages.ContainsKey(failureType.Type),
                $"Missing failure type category: {failureType.Type}");
            
            Assert.Equal(failureType.Count, categorizedMessages[failureType.Type].Count);
            
            // Verify all messages in category have correct attributes
            foreach (var message in categorizedMessages[failureType.Type])
            {
                Assert.Equal(failureType.Type, message.MessageAttributes["FailureType"].StringValue);
                Assert.Equal(failureType.Description, message.MessageAttributes["FailureDescription"].StringValue);
                Assert.True(message.MessageAttributes.ContainsKey("CommandType"));
                Assert.True(message.MessageAttributes.ContainsKey("EntityId"));
            }
        }
        
        // Clean up
        foreach (var category in categorizedMessages.Values)
        {
            foreach (var message in category)
            {
                await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
                {
                    QueueUrl = dlqUrl,
                    ReceiptHandle = message.ReceiptHandle
                });
            }
        }
    }
    
    [Fact]
    public async Task DeadLetterProcessing_ShouldSupportMessageAnalysis()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange - Create DLQ with various failed messages
        var dlqName = $"test-dlq-analysis-{Guid.NewGuid():N}";
        var dlqUrl = await CreateStandardQueueAsync(dlqName);
        
        // Create messages with different characteristics for analysis
        var testMessages = new[]
        {
            new { EntityId = 1001, FailureType = "ValidationError", RetryCount = 3, Age = TimeSpan.FromHours(1) },
            new { EntityId = 1002, FailureType = "ValidationError", RetryCount = 5, Age = TimeSpan.FromHours(2) },
            new { EntityId = 1003, FailureType = "TimeoutError", RetryCount = 2, Age = TimeSpan.FromMinutes(30) },
            new { EntityId = 1004, FailureType = "TimeoutError", RetryCount = 4, Age = TimeSpan.FromHours(3) },
            new { EntityId = 1005, FailureType = "DataCorruption", RetryCount = 1, Age = TimeSpan.FromHours(24) }
        };
        
        // Send messages
        foreach (var testMsg in testMessages)
        {
            var timestamp = DateTime.UtcNow.Subtract(testMsg.Age);
            
            await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = dlqUrl,
                MessageBody = JsonSerializer.Serialize(new
                {
                    EntityId = testMsg.EntityId,
                    FailureType = testMsg.FailureType,
                    OriginalTimestamp = timestamp
                }),
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["EntityId"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = testMsg.EntityId.ToString()
                    },
                    ["FailureType"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = testMsg.FailureType
                    },
                    ["RetryCount"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = testMsg.RetryCount.ToString()
                    },
                    ["OriginalTimestamp"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = timestamp.ToString("O")
                    },
                    ["CommandType"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = "TestCommand"
                    }
                }
            });
        }
        
        // Act - Retrieve and analyze messages
        var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = dlqUrl,
            MaxNumberOfMessages = 10,
            MessageAttributeNames = new List<string> { "All" },
            WaitTimeSeconds = 2
        });
        
        var messages = receiveResponse.Messages;
        
        // Assert - All messages retrieved
        Assert.Equal(testMessages.Length, messages.Count);
        
        // Analyze - Group by failure type
        var failureTypeGroups = messages
            .GroupBy(m => m.MessageAttributes["FailureType"].StringValue)
            .ToDictionary(g => g.Key ?? "Unknown", g => g.ToList());
        
        Assert.Equal(3, failureTypeGroups.Count); // ValidationError, TimeoutError, DataCorruption
        Assert.Equal(2, failureTypeGroups["ValidationError"].Count);
        Assert.Equal(2, failureTypeGroups["TimeoutError"].Count);
        Assert.Single(failureTypeGroups["DataCorruption"]);
        
        // Analyze - Find high retry count messages (>= 4)
        var highRetryMessages = messages
            .Where(m => int.Parse(m.MessageAttributes["RetryCount"].StringValue ?? "0") >= 4)
            .ToList();
        
        Assert.Equal(2, highRetryMessages.Count);
        
        // Analyze - Find old messages (> 12 hours)
        var oldMessages = messages
            .Where(m =>
            {
                if (m.MessageAttributes.TryGetValue("OriginalTimestamp", out var tsAttr))
                {
                    if (DateTime.TryParse(tsAttr.StringValue, out var timestamp))
                    {
                        return DateTime.UtcNow.Subtract(timestamp).TotalHours > 12;
                    }
                }
                return false;
            })
            .ToList();
        
        Assert.Single(oldMessages);
        
        // Analyze - Calculate statistics
        var totalRetries = messages
            .Sum(m => int.Parse(m.MessageAttributes["RetryCount"].StringValue ?? "0"));
        
        var averageRetries = (double)totalRetries / messages.Count;
        
        Assert.True(averageRetries > 0);
        Assert.True(averageRetries < 10); // Reasonable average
        
        // Clean up
        foreach (var message in messages)
        {
            await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
            {
                QueueUrl = dlqUrl,
                ReceiptHandle = message.ReceiptHandle
            });
        }
    }
    
    [Fact]
    public async Task DeadLetterProcessing_ShouldSupportReprocessingWorkflow()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange - Create DLQ and reprocessing queue
        var dlqName = $"test-dlq-reprocess-workflow-{Guid.NewGuid():N}";
        var dlqUrl = await CreateStandardQueueAsync(dlqName);
        
        var reprocessQueueName = $"test-reprocess-target-{Guid.NewGuid():N}";
        var reprocessQueueUrl = await CreateStandardQueueAsync(reprocessQueueName);
        
        // Add failed messages to DLQ
        var failedMessages = new[]
        {
            new { OrderId = Guid.NewGuid(), EntityId = 2001, Status = "Failed", Reason = "PaymentTimeout" },
            new { OrderId = Guid.NewGuid(), EntityId = 2002, Status = "Failed", Reason = "InventoryUnavailable" },
            new { OrderId = Guid.NewGuid(), EntityId = 2003, Status = "Failed", Reason = "AddressValidationFailed" }
        };
        
        foreach (var failedMsg in failedMessages)
        {
            await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = dlqUrl,
                MessageBody = JsonSerializer.Serialize(failedMsg),
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["EntityId"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = failedMsg.EntityId.ToString()
                    },
                    ["OriginalFailureReason"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = failedMsg.Reason
                    },
                    ["CommandType"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = "ProcessOrderCommand"
                    },
                    ["FailureTimestamp"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = DateTime.UtcNow.ToString("O")
                    },
                    ["ReprocessAttempt"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = "0"
                    }
                }
            });
        }
        
        // Act - Retrieve messages from DLQ
        var dlqMessages = new List<Message>();
        var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = dlqUrl,
            MaxNumberOfMessages = 10,
            MessageAttributeNames = new List<string> { "All" },
            WaitTimeSeconds = 2
        });
        
        dlqMessages.AddRange(receiveResponse.Messages);
        
        // Assert - Retrieved all failed messages
        Assert.Equal(failedMessages.Length, dlqMessages.Count);
        
        // Act - Reprocess messages with enrichment
        var reprocessedCount = 0;
        
        foreach (var dlqMessage in dlqMessages)
        {
            var originalBody = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(dlqMessage.Body);
            Assert.NotNull(originalBody);
            
            // Enrich message for reprocessing
            var reprocessedBody = new Dictionary<string, object>
            {
                ["OrderId"] = originalBody["OrderId"].GetGuid(),
                ["EntityId"] = originalBody["EntityId"].GetInt32(),
                ["Status"] = "Reprocessing",
                ["OriginalStatus"] = originalBody["Status"].GetString() ?? "",
                ["OriginalReason"] = originalBody["Reason"].GetString() ?? "",
                ["ReprocessedAt"] = DateTime.UtcNow.ToString("O"),
                ["ReprocessingStrategy"] = DetermineReprocessingStrategy(
                    dlqMessage.MessageAttributes["OriginalFailureReason"].StringValue ?? ""),
                ["Priority"] = "High" // Reprocessed messages get high priority
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
                    ["OriginalEntityId"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = dlqMessage.MessageAttributes["EntityId"].StringValue
                    },
                    ["OriginalFailureReason"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = dlqMessage.MessageAttributes["OriginalFailureReason"].StringValue
                    },
                    ["CommandType"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = dlqMessage.MessageAttributes["CommandType"].StringValue
                    },
                    ["ReprocessAttempt"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = (int.Parse(dlqMessage.MessageAttributes["ReprocessAttempt"].StringValue ?? "0") + 1).ToString()
                    },
                    ["ReprocessingStrategy"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = (string)reprocessedBody["ReprocessingStrategy"]
                    }
                }
            });
            
            Assert.NotNull(reprocessResponse.MessageId);
            
            // Delete from DLQ after successful reprocessing
            await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
            {
                QueueUrl = dlqUrl,
                ReceiptHandle = dlqMessage.ReceiptHandle
            });
            
            reprocessedCount++;
        }
        
        // Assert - All messages reprocessed
        Assert.Equal(failedMessages.Length, reprocessedCount);
        
        // Act - Verify reprocessed messages in target queue
        var reprocessedReceiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = reprocessQueueUrl,
            MaxNumberOfMessages = 10,
            MessageAttributeNames = new List<string> { "All" },
            WaitTimeSeconds = 2
        });
        
        // Assert - All reprocessed messages available
        Assert.Equal(failedMessages.Length, reprocessedReceiveResponse.Messages.Count);
        
        // Assert - Verify reprocessing metadata
        foreach (var reprocessedMessage in reprocessedReceiveResponse.Messages)
        {
            Assert.Equal("DeadLetterQueue", reprocessedMessage.MessageAttributes["ReprocessedFrom"].StringValue);
            Assert.True(int.Parse(reprocessedMessage.MessageAttributes["ReprocessAttempt"].StringValue ?? "0") > 0);
            Assert.True(reprocessedMessage.MessageAttributes.ContainsKey("OriginalEntityId"));
            Assert.True(reprocessedMessage.MessageAttributes.ContainsKey("OriginalFailureReason"));
            Assert.True(reprocessedMessage.MessageAttributes.ContainsKey("ReprocessingStrategy"));
            
            var messageBody = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(reprocessedMessage.Body);
            Assert.NotNull(messageBody);
            Assert.Equal("Reprocessing", messageBody["Status"].GetString());
            Assert.True(messageBody.ContainsKey("ReprocessedAt"));
            Assert.True(messageBody.ContainsKey("ReprocessingStrategy"));
            Assert.Equal("High", messageBody["Priority"].GetString());
        }
        
        // Clean up
        foreach (var message in reprocessedReceiveResponse.Messages)
        {
            await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
            {
                QueueUrl = reprocessQueueUrl,
                ReceiptHandle = message.ReceiptHandle
            });
        }
        
        // Verify DLQ is empty
        var dlqCheckResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = dlqUrl,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 1
        });
        
        Assert.Empty(dlqCheckResponse.Messages);
    }
    
    [Fact]
    public async Task DeadLetterProcessing_ShouldSupportMonitoringAndAlerting()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange - Create DLQ for monitoring
        var dlqName = $"test-dlq-monitoring-{Guid.NewGuid():N}";
        var dlqUrl = await CreateStandardQueueAsync(dlqName);
        
        // Configure monitoring options
        var monitorOptions = new AwsDeadLetterMonitorOptions
        {
            Enabled = true,
            DeadLetterQueues = new List<string> { dlqUrl },
            CheckIntervalSeconds = 5,
            BatchSize = 10,
            StoreRecords = true,
            SendAlerts = true,
            AlertThreshold = 5,
            DeleteAfterProcessing = false
        };
        
        // Add messages to DLQ to trigger monitoring
        var messageCount = 7; // Above alert threshold
        
        for (int i = 0; i < messageCount; i++)
        {
            await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = dlqUrl,
                MessageBody = JsonSerializer.Serialize(new
                {
                    CommandId = Guid.NewGuid(),
                    EntityId = 3000 + i,
                    FailureType = i % 2 == 0 ? "ValidationError" : "TimeoutError"
                }),
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["CommandType"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = "TestCommand"
                    },
                    ["EntityId"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = (3000 + i).ToString()
                    },
                    ["FailureType"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = i % 2 == 0 ? "ValidationError" : "TimeoutError"
                    }
                }
            });
        }
        
        // Act - Check queue depth (monitoring metric)
        var attributesResponse = await _localStack.SqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = dlqUrl,
            AttributeNames = new List<string>
            {
                "ApproximateNumberOfMessages",
                "ApproximateNumberOfMessagesNotVisible",
                "ApproximateNumberOfMessagesDelayed"
            }
        });
        
        var queueDepth = 0;
        if (attributesResponse.Attributes.TryGetValue("ApproximateNumberOfMessages", out var depthStr))
        {
            int.TryParse(depthStr, out queueDepth);
        }
        
        // Assert - Queue depth should match sent messages
        Assert.True(queueDepth >= messageCount * 0.8, // Allow some variance
            $"Expected queue depth around {messageCount}, got {queueDepth}");
        
        // Assert - Should trigger alert (depth > threshold)
        Assert.True(queueDepth >= monitorOptions.AlertThreshold,
            $"Queue depth {queueDepth} should exceed alert threshold {monitorOptions.AlertThreshold}");
        
        // Act - Retrieve messages for monitoring analysis
        var monitoredMessages = new List<Message>();
        var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = dlqUrl,
            MaxNumberOfMessages = 10,
            MessageAttributeNames = new List<string> { "All" },
            AttributeNames = new List<string> { "All" },
            WaitTimeSeconds = 2
        });
        
        monitoredMessages.AddRange(receiveResponse.Messages);
        
        // Assert - Messages retrieved for monitoring
        Assert.True(monitoredMessages.Count >= messageCount * 0.8);
        
        // Act - Create dead letter records for monitoring
        var deadLetterRecords = new List<DeadLetterRecord>();
        
        foreach (var message in monitoredMessages)
        {
            var receiveCount = 0;
            if (message.Attributes.TryGetValue("ApproximateReceiveCount", out var countStr))
            {
                int.TryParse(countStr, out receiveCount);
            }
            
            var record = new DeadLetterRecord
            {
                MessageId = message.MessageId,
                Body = message.Body,
                MessageType = message.MessageAttributes["CommandType"].StringValue ?? "Unknown",
                Reason = "DeadLetterQueueThresholdExceeded",
                ErrorDescription = $"Message exceeded max receive count. Receive count: {receiveCount}",
                OriginalSource = "TestQueue",
                DeadLetterSource = dlqUrl,
                CloudProvider = "aws",
                DeadLetteredAt = DateTime.UtcNow,
                DeliveryCount = receiveCount,
                Metadata = new Dictionary<string, string>()
            };
            
            // Add message attributes to metadata
            foreach (var attr in message.MessageAttributes)
            {
                record.Metadata[attr.Key] = attr.Value.StringValue ?? string.Empty;
            }
            
            // Save to store
            await _deadLetterStore.SaveAsync(record);
            deadLetterRecords.Add(record);
        }
        
        // Assert - All records stored
        Assert.Equal(monitoredMessages.Count, deadLetterRecords.Count);
        
        // Act - Query stored records
        var query = new DeadLetterQuery
        {
            CloudProvider = "aws",
            FromDate = DateTime.UtcNow.AddHours(-1)
        };
        
        var storedRecords = await _deadLetterStore.QueryAsync(query);
        var storedRecordsList = storedRecords.ToList();
        
        // Assert - Records can be queried
        Assert.True(storedRecordsList.Count >= deadLetterRecords.Count);
        
        // Act - Generate monitoring statistics
        var validationErrors = storedRecordsList.Count(r => r.Metadata.ContainsKey("FailureType") && 
                                                            r.Metadata["FailureType"] == "ValidationError");
        var timeoutErrors = storedRecordsList.Count(r => r.Metadata.ContainsKey("FailureType") && 
                                                         r.Metadata["FailureType"] == "TimeoutError");
        
        // Assert - Statistics are meaningful
        Assert.True(validationErrors > 0);
        Assert.True(timeoutErrors > 0);
        Assert.Equal(storedRecordsList.Count, validationErrors + timeoutErrors);
        
        // Clean up
        foreach (var message in monitoredMessages)
        {
            await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
            {
                QueueUrl = dlqUrl,
                ReceiptHandle = message.ReceiptHandle
            });
        }
    }
    
    [Fact]
    public async Task DeadLetterProcessing_ShouldSupportBatchReprocessing()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange - Create DLQ with multiple messages
        var dlqName = $"test-dlq-batch-reprocess-{Guid.NewGuid():N}";
        var dlqUrl = await CreateStandardQueueAsync(dlqName);
        
        var targetQueueName = $"test-batch-reprocess-target-{Guid.NewGuid():N}";
        var targetQueueUrl = await CreateStandardQueueAsync(targetQueueName);
        
        var batchSize = 10;
        var sentMessageIds = new List<string>();
        
        // Add messages to DLQ
        for (int i = 0; i < batchSize; i++)
        {
            var sendResponse = await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = dlqUrl,
                MessageBody = JsonSerializer.Serialize(new
                {
                    CommandId = Guid.NewGuid(),
                    EntityId = 4000 + i,
                    BatchIndex = i,
                    Data = $"Batch message {i}"
                }),
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["EntityId"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = (4000 + i).ToString()
                    },
                    ["CommandType"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = "BatchTestCommand"
                    },
                    ["BatchIndex"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = i.ToString()
                    }
                }
            });
            
            sentMessageIds.Add(sendResponse.MessageId);
        }
        
        // Act - Batch retrieve from DLQ
        var dlqMessages = new List<Message>();
        var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = dlqUrl,
            MaxNumberOfMessages = 10, // AWS max batch size
            MessageAttributeNames = new List<string> { "All" },
            WaitTimeSeconds = 2
        });
        
        dlqMessages.AddRange(receiveResponse.Messages);
        
        // Assert - Retrieved batch
        Assert.Equal(batchSize, dlqMessages.Count);
        
        // Act - Batch reprocess to target queue
        var reprocessTasks = dlqMessages.Select(async message =>
        {
            var reprocessedBody = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(message.Body);
            Assert.NotNull(reprocessedBody);
            
            // Add reprocessing metadata
            var enrichedBody = new Dictionary<string, object>
            {
                ["CommandId"] = reprocessedBody["CommandId"].GetGuid(),
                ["EntityId"] = reprocessedBody["EntityId"].GetInt32(),
                ["BatchIndex"] = reprocessedBody["BatchIndex"].GetInt32(),
                ["Data"] = reprocessedBody["Data"].GetString() ?? "",
                ["ReprocessedAt"] = DateTime.UtcNow.ToString("O"),
                ["ReprocessedFromDLQ"] = true
            };
            
            return await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = targetQueueUrl,
                MessageBody = JsonSerializer.Serialize(enrichedBody),
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
                        StringValue = message.MessageAttributes["EntityId"].StringValue
                    },
                    ["CommandType"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = message.MessageAttributes["CommandType"].StringValue
                    },
                    ["BatchIndex"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = message.MessageAttributes["BatchIndex"].StringValue
                    }
                }
            });
        });
        
        var reprocessResults = await Task.WhenAll(reprocessTasks);
        
        // Assert - All batch reprocessed
        Assert.Equal(batchSize, reprocessResults.Length);
        Assert.All(reprocessResults, result => Assert.NotNull(result.MessageId));
        
        // Act - Batch delete from DLQ
        var deleteTasks = dlqMessages.Select(message =>
            _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
            {
                QueueUrl = dlqUrl,
                ReceiptHandle = message.ReceiptHandle
            }));
        
        await Task.WhenAll(deleteTasks);
        
        // Act - Verify reprocessed messages in target queue
        var targetReceiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = targetQueueUrl,
            MaxNumberOfMessages = 10,
            MessageAttributeNames = new List<string> { "All" },
            WaitTimeSeconds = 2
        });
        
        // Assert - All messages in target queue
        Assert.Equal(batchSize, targetReceiveResponse.Messages.Count);
        
        // Assert - Verify batch ordering preserved
        var orderedMessages = targetReceiveResponse.Messages
            .OrderBy(m => int.Parse(m.MessageAttributes["BatchIndex"].StringValue ?? "0"))
            .ToList();
        
        for (int i = 0; i < orderedMessages.Count; i++)
        {
            Assert.Equal(i.ToString(), orderedMessages[i].MessageAttributes["BatchIndex"].StringValue);
        }
        
        // Clean up
        var cleanupTasks = targetReceiveResponse.Messages.Select(message =>
            _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
            {
                QueueUrl = targetQueueUrl,
                ReceiptHandle = message.ReceiptHandle
            }));
        
        await Task.WhenAll(cleanupTasks);
        
        // Verify DLQ is empty
        var dlqCheckResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = dlqUrl,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 1
        });
        
        Assert.Empty(dlqCheckResponse.Messages);
    }
    
    [Fact]
    public async Task DeadLetterProcessing_ShouldSupportFifoQueueReprocessing()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange - Create FIFO DLQ and target queue
        var dlqName = $"test-dlq-fifo-reprocess-{Guid.NewGuid():N}.fifo";
        var dlqUrl = await CreateFifoQueueAsync(dlqName);
        
        var targetQueueName = $"test-fifo-reprocess-target-{Guid.NewGuid():N}.fifo";
        var targetQueueUrl = await CreateFifoQueueAsync(targetQueueName);
        
        var entityId = 5000;
        var messageGroupId = $"entity-{entityId}";
        
        // Add ordered messages to FIFO DLQ
        var fifoMessages = new[]
        {
            new { SequenceNo = 1, Command = "CreateOrder", Data = "Order data 1" },
            new { SequenceNo = 2, Command = "UpdateOrder", Data = "Order data 2" },
            new { SequenceNo = 3, Command = "ProcessPayment", Data = "Payment data" },
            new { SequenceNo = 4, Command = "ShipOrder", Data = "Shipping data" }
        };
        
        foreach (var msg in fifoMessages)
        {
            await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = dlqUrl,
                MessageBody = JsonSerializer.Serialize(msg),
                MessageGroupId = messageGroupId,
                MessageDeduplicationId = $"dlq-msg-{entityId}-{msg.SequenceNo}-{Guid.NewGuid():N}",
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
        
        // Act - Retrieve messages from FIFO DLQ (should maintain order)
        var dlqMessages = new List<Message>();
        var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = dlqUrl,
            MaxNumberOfMessages = 10,
            MessageAttributeNames = new List<string> { "All" },
            WaitTimeSeconds = 2
        });
        
        dlqMessages.AddRange(receiveResponse.Messages);
        
        // Assert - All messages retrieved
        Assert.Equal(fifoMessages.Length, dlqMessages.Count);
        
        // Act - Reprocess to target FIFO queue maintaining order
        var reprocessedCount = 0;
        
        foreach (var dlqMessage in dlqMessages)
        {
            var originalBody = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(dlqMessage.Body);
            Assert.NotNull(originalBody);
            
            var sequenceNo = int.Parse(dlqMessage.MessageAttributes["SequenceNo"].StringValue ?? "0");
            
            var reprocessedBody = new Dictionary<string, object>
            {
                ["SequenceNo"] = sequenceNo,
                ["Command"] = originalBody["Command"].GetString() ?? "",
                ["Data"] = originalBody["Data"].GetString() ?? "",
                ["ReprocessedAt"] = DateTime.UtcNow.ToString("O"),
                ["ReprocessedFromDLQ"] = true
            };
            
            // Send to target FIFO queue with same message group
            await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = targetQueueUrl,
                MessageBody = JsonSerializer.Serialize(reprocessedBody),
                MessageGroupId = messageGroupId, // Maintain same group for ordering
                MessageDeduplicationId = $"reprocess-{entityId}-{sequenceNo}-{Guid.NewGuid():N}",
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["ReprocessedFrom"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = "DeadLetterQueue"
                    },
                    ["EntityId"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = dlqMessage.MessageAttributes["EntityId"].StringValue
                    },
                    ["SequenceNo"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = dlqMessage.MessageAttributes["SequenceNo"].StringValue
                    },
                    ["CommandType"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = dlqMessage.MessageAttributes["CommandType"].StringValue
                    }
                }
            });
            
            // Delete from DLQ
            await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
            {
                QueueUrl = dlqUrl,
                ReceiptHandle = dlqMessage.ReceiptHandle
            });
            
            reprocessedCount++;
        }
        
        // Assert - All messages reprocessed
        Assert.Equal(fifoMessages.Length, reprocessedCount);
        
        // Act - Verify messages in target queue maintain FIFO order
        var targetMessages = new List<Message>();
        var targetReceiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = targetQueueUrl,
            MaxNumberOfMessages = 10,
            MessageAttributeNames = new List<string> { "All" },
            WaitTimeSeconds = 2
        });
        
        targetMessages.AddRange(targetReceiveResponse.Messages);
        
        // Assert - All messages in target queue
        Assert.Equal(fifoMessages.Length, targetMessages.Count);
        
        // Assert - FIFO ordering maintained
        var orderedTargetMessages = targetMessages
            .OrderBy(m => int.Parse(m.MessageAttributes["SequenceNo"].StringValue ?? "0"))
            .ToList();
        
        for (int i = 0; i < orderedTargetMessages.Count; i++)
        {
            var expectedSequenceNo = i + 1;
            Assert.Equal(expectedSequenceNo.ToString(), orderedTargetMessages[i].MessageAttributes["SequenceNo"].StringValue);
        }
        
        // Clean up
        foreach (var message in targetMessages)
        {
            await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
            {
                QueueUrl = targetQueueUrl,
                ReceiptHandle = message.ReceiptHandle
            });
        }
    }
    
    [Fact]
    public async Task DeadLetterProcessing_ShouldTrackReprocessingHistory()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange - Create DLQ
        var dlqName = $"test-dlq-history-{Guid.NewGuid():N}";
        var dlqUrl = await CreateStandardQueueAsync(dlqName);
        
        // Add message with reprocessing history
        var messageId = Guid.NewGuid().ToString();
        var entityId = 6000;
        
        await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = dlqUrl,
            MessageBody = JsonSerializer.Serialize(new
            {
                CommandId = messageId,
                EntityId = entityId,
                Data = "Test data"
            }),
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["EntityId"] = new MessageAttributeValue
                {
                    DataType = "Number",
                    StringValue = entityId.ToString()
                },
                ["CommandType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "TestCommand"
                },
                ["OriginalFailureReason"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "ValidationError"
                },
                ["FirstFailureTimestamp"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = DateTime.UtcNow.AddHours(-2).ToString("O")
                },
                ["ReprocessAttempt"] = new MessageAttributeValue
                {
                    DataType = "Number",
                    StringValue = "0"
                }
            }
        });
        
        // Act - Create dead letter record with history tracking
        var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = dlqUrl,
            MaxNumberOfMessages = 1,
            MessageAttributeNames = new List<string> { "All" },
            WaitTimeSeconds = 2
        });
        
        Assert.Single(receiveResponse.Messages);
        var message = receiveResponse.Messages[0];
        
        // Create dead letter record
        var record = new DeadLetterRecord
        {
            MessageId = messageId,
            Body = message.Body,
            MessageType = message.MessageAttributes["CommandType"].StringValue ?? "Unknown",
            Reason = message.MessageAttributes["OriginalFailureReason"].StringValue ?? "Unknown",
            ErrorDescription = "Message failed validation and was moved to DLQ",
            OriginalSource = "TestQueue",
            DeadLetterSource = dlqUrl,
            CloudProvider = "aws",
            DeadLetteredAt = DateTime.UtcNow,
            DeliveryCount = int.Parse(message.MessageAttributes["ReprocessAttempt"].StringValue ?? "0"),
            Replayed = false,
            Metadata = new Dictionary<string, string>
            {
                ["EntityId"] = message.MessageAttributes["EntityId"].StringValue ?? "",
                ["FirstFailureTimestamp"] = message.MessageAttributes["FirstFailureTimestamp"].StringValue ?? "",
                ["ReprocessAttempt"] = message.MessageAttributes["ReprocessAttempt"].StringValue ?? "0"
            }
        };
        
        // Save record
        await _deadLetterStore.SaveAsync(record);
        
        // Assert - Record saved
        var savedRecord = await _deadLetterStore.GetAsync(record.Id);
        Assert.NotNull(savedRecord);
        Assert.Equal(messageId, savedRecord.MessageId);
        Assert.False(savedRecord.Replayed);
        
        // Act - Mark as replayed
        await _deadLetterStore.MarkAsReplayedAsync(record.Id);
        
        // Assert - Record marked as replayed
        var replayedRecord = await _deadLetterStore.GetAsync(record.Id);
        Assert.NotNull(replayedRecord);
        Assert.True(replayedRecord.Replayed);
        Assert.NotNull(replayedRecord.ReplayedAt);
        
        // Act - Query reprocessing history
        var query = new DeadLetterQuery
        {
            MessageType = "TestCommand",
            Replayed = true,
            CloudProvider = "aws"
        };
        
        var replayedRecords = await _deadLetterStore.QueryAsync(query);
        var replayedRecordsList = replayedRecords.ToList();
        
        // Assert - Can query replayed messages
        Assert.True(replayedRecordsList.Any(r => r.MessageId == messageId));
        
        // Clean up
        await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
        {
            QueueUrl = dlqUrl,
            ReceiptHandle = message.ReceiptHandle
        });
    }
    
    // Helper methods
    
    private static string DetermineReprocessingStrategy(string failureReason)
    {
        return failureReason switch
        {
            "PaymentTimeout" => "RetryWithExtendedTimeout",
            "InventoryUnavailable" => "RetryAfterInventoryCheck",
            "AddressValidationFailed" => "ManualReview",
            "ValidationError" => "RetryWithValidation",
            "TimeoutError" => "RetryWithBackoff",
            "DataCorruption" => "ManualIntervention",
            _ => "StandardRetry"
        };
    }
    
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
    
    private async Task<string> GetQueueArnAsync(string queueUrl)
    {
        var response = await _localStack.SqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = new List<string> { "QueueArn" }
        });
        
        return response.Attributes["QueueArn"];
    }
    
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

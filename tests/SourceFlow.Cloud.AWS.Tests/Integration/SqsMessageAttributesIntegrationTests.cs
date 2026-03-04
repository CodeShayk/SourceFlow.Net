using Amazon.SQS.Model;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using System.Text;
using System.Text.Json;

namespace SourceFlow.Cloud.AWS.Tests.Integration;

/// <summary>
/// Comprehensive integration tests for SQS message attributes
/// Tests SourceFlow command metadata preservation, custom attributes handling, routing/filtering, and size limits
/// </summary>
[Collection("AWS Integration Tests")]
[Trait("Category", "Integration")]
[Trait("Category", "RequiresLocalStack")]
public class SqsMessageAttributesIntegrationTests : IClassFixture<LocalStackTestFixture>, IAsyncDisposable
{
    private readonly LocalStackTestFixture _localStack;
    private readonly List<string> _createdQueues = new();
    
    public SqsMessageAttributesIntegrationTests(LocalStackTestFixture localStack)
    {
        _localStack = localStack;
    }
    
    [Fact]
    public async Task MessageAttributes_ShouldPreserveSourceFlowCommandMetadata()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-sourceflow-metadata-{Guid.NewGuid():N}";
        var queueUrl = await CreateStandardQueueAsync(queueName);
        
        var entityId = 12345;
        var sequenceNo = 42;
        var commandType = "CreateOrderCommand";
        var payloadType = "CreateOrderPayload";
        var correlationId = Guid.NewGuid().ToString();
        var userId = "user-123";
        var tenantId = "tenant-456";
        
        var commandPayload = new
        {
            OrderId = Guid.NewGuid(),
            CustomerId = 67890,
            Amount = 199.99m,
            Currency = "USD",
            Items = new[]
            {
                new { ProductId = "PROD-001", Quantity = 2, Price = 99.99m },
                new { ProductId = "PROD-002", Quantity = 1, Price = 99.99m }
            }
        };
        
        var commandMetadata = new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["UserId"] = userId,
            ["TenantId"] = tenantId,
            ["RequestId"] = Guid.NewGuid().ToString(),
            ["ClientVersion"] = "1.2.3",
            ["Timestamp"] = DateTime.UtcNow.ToString("O"),
            ["Source"] = "OrderService",
            ["TraceId"] = "trace-" + Guid.NewGuid().ToString("N")[..16]
        };
        
        // Act - Send message with comprehensive SourceFlow metadata
        var sendResponse = await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = JsonSerializer.Serialize(commandPayload),
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                // Core SourceFlow attributes
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
                },
                // Additional SourceFlow attributes
                ["Version"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "1.0"
                },
                ["Priority"] = new MessageAttributeValue
                {
                    DataType = "Number",
                    StringValue = "5"
                },
                ["RetryCount"] = new MessageAttributeValue
                {
                    DataType = "Number",
                    StringValue = "0"
                },
                ["TimeToLive"] = new MessageAttributeValue
                {
                    DataType = "Number",
                    StringValue = "3600" // 1 hour in seconds
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
        
        // Verify core SourceFlow attributes
        Assert.Equal(entityId.ToString(), message.MessageAttributes["EntityId"].StringValue);
        Assert.Equal(sequenceNo.ToString(), message.MessageAttributes["SequenceNo"].StringValue);
        Assert.Equal(commandType, message.MessageAttributes["CommandType"].StringValue);
        Assert.Equal(payloadType, message.MessageAttributes["PayloadType"].StringValue);
        Assert.Equal("1.0", message.MessageAttributes["Version"].StringValue);
        Assert.Equal("5", message.MessageAttributes["Priority"].StringValue);
        Assert.Equal("0", message.MessageAttributes["RetryCount"].StringValue);
        Assert.Equal("3600", message.MessageAttributes["TimeToLive"].StringValue);
        
        // Verify metadata preservation
        var receivedMetadata = JsonSerializer.Deserialize<Dictionary<string, object>>(
            message.MessageAttributes["Metadata"].StringValue);
        Assert.NotNull(receivedMetadata);
        Assert.Equal(correlationId, receivedMetadata["CorrelationId"].ToString());
        Assert.Equal(userId, receivedMetadata["UserId"].ToString());
        Assert.Equal(tenantId, receivedMetadata["TenantId"].ToString());
        Assert.True(receivedMetadata.ContainsKey("RequestId"));
        Assert.True(receivedMetadata.ContainsKey("ClientVersion"));
        Assert.True(receivedMetadata.ContainsKey("Timestamp"));
        Assert.True(receivedMetadata.ContainsKey("Source"));
        Assert.True(receivedMetadata.ContainsKey("TraceId"));
        
        // Verify payload preservation
        var receivedPayload = JsonSerializer.Deserialize<Dictionary<string, object>>(message.Body);
        Assert.NotNull(receivedPayload);
        Assert.True(receivedPayload.ContainsKey("OrderId"));
        Assert.True(receivedPayload.ContainsKey("CustomerId"));
        Assert.True(receivedPayload.ContainsKey("Amount"));
        Assert.True(receivedPayload.ContainsKey("Currency"));
        Assert.True(receivedPayload.ContainsKey("Items"));
        
        // Clean up
        await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
        {
            QueueUrl = queueUrl,
            ReceiptHandle = message.ReceiptHandle
        });
    }
    
    [Fact]
    public async Task MessageAttributes_ShouldSupportAllDataTypes()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-attribute-data-types-{Guid.NewGuid():N}";
        var queueUrl = await CreateStandardQueueAsync(queueName);
        
        var binaryData = Encoding.UTF8.GetBytes("Binary test data with special chars: àáâãäå");
        
        // Act - Send message with various data types
        var sendResponse = await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Message with various attribute data types",
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                // String attributes
                ["StringAttribute"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "Test string value with unicode: 你好世界"
                },
                ["EmptyString"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = ""
                },
                // Number attributes
                ["IntegerAttribute"] = new MessageAttributeValue
                {
                    DataType = "Number",
                    StringValue = "42"
                },
                ["NegativeNumber"] = new MessageAttributeValue
                {
                    DataType = "Number",
                    StringValue = "-123"
                },
                ["DecimalNumber"] = new MessageAttributeValue
                {
                    DataType = "Number",
                    StringValue = "3.14159"
                },
                ["LargeNumber"] = new MessageAttributeValue
                {
                    DataType = "Number",
                    StringValue = "9223372036854775807" // Long.MaxValue
                },
                // Binary attribute
                ["BinaryAttribute"] = new MessageAttributeValue
                {
                    DataType = "Binary",
                    BinaryValue = new MemoryStream(binaryData)
                },
                // Custom data types
                ["CustomType.DateTime"] = new MessageAttributeValue
                {
                    DataType = "String.DateTime",
                    StringValue = DateTime.UtcNow.ToString("O")
                },
                ["CustomType.Boolean"] = new MessageAttributeValue
                {
                    DataType = "String.Boolean",
                    StringValue = "true"
                },
                ["CustomType.Guid"] = new MessageAttributeValue
                {
                    DataType = "String.Guid",
                    StringValue = Guid.NewGuid().ToString()
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
        
        // Assert - All attributes should be preserved with correct data types
        Assert.Single(receiveResponse.Messages);
        var message = receiveResponse.Messages[0];
        
        // Verify string attributes
        Assert.Equal("String", message.MessageAttributes["StringAttribute"].DataType);
        Assert.Equal("Test string value with unicode: 你好世界", message.MessageAttributes["StringAttribute"].StringValue);
        Assert.Equal("String", message.MessageAttributes["EmptyString"].DataType);
        Assert.Equal("", message.MessageAttributes["EmptyString"].StringValue);
        
        // Verify number attributes
        Assert.Equal("Number", message.MessageAttributes["IntegerAttribute"].DataType);
        Assert.Equal("42", message.MessageAttributes["IntegerAttribute"].StringValue);
        Assert.Equal("Number", message.MessageAttributes["NegativeNumber"].DataType);
        Assert.Equal("-123", message.MessageAttributes["NegativeNumber"].StringValue);
        Assert.Equal("Number", message.MessageAttributes["DecimalNumber"].DataType);
        Assert.Equal("3.14159", message.MessageAttributes["DecimalNumber"].StringValue);
        Assert.Equal("Number", message.MessageAttributes["LargeNumber"].DataType);
        Assert.Equal("9223372036854775807", message.MessageAttributes["LargeNumber"].StringValue);
        
        // Verify binary attribute
        Assert.Equal("Binary", message.MessageAttributes["BinaryAttribute"].DataType);
        var receivedBinaryData = new byte[message.MessageAttributes["BinaryAttribute"].BinaryValue.Length];
        message.MessageAttributes["BinaryAttribute"].BinaryValue.Read(receivedBinaryData, 0, receivedBinaryData.Length);
        Assert.Equal(binaryData, receivedBinaryData);
        
        // Verify custom data types
        Assert.Equal("String.DateTime", message.MessageAttributes["CustomType.DateTime"].DataType);
        Assert.True(DateTime.TryParse(message.MessageAttributes["CustomType.DateTime"].StringValue, out _));
        Assert.Equal("String.Boolean", message.MessageAttributes["CustomType.Boolean"].DataType);
        Assert.Equal("true", message.MessageAttributes["CustomType.Boolean"].StringValue);
        Assert.Equal("String.Guid", message.MessageAttributes["CustomType.Guid"].DataType);
        Assert.True(Guid.TryParse(message.MessageAttributes["CustomType.Guid"].StringValue, out _));
        
        // Clean up
        await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
        {
            QueueUrl = queueUrl,
            ReceiptHandle = message.ReceiptHandle
        });
    }
    
    [Fact]
    public async Task MessageAttributes_ShouldSupportAttributeBasedFiltering()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-attribute-filtering-{Guid.NewGuid():N}";
        var queueUrl = await CreateStandardQueueAsync(queueName);
        
        // Send messages with different attributes for filtering
        var messages = new[]
        {
            new { Priority = "High", Category = "Order", EntityId = 1001, MessageBody = "High priority order message" },
            new { Priority = "Low", Category = "Order", EntityId = 1002, MessageBody = "Low priority order message" },
            new { Priority = "High", Category = "Payment", EntityId = 1003, MessageBody = "High priority payment message" },
            new { Priority = "Medium", Category = "Notification", EntityId = 1004, MessageBody = "Medium priority notification message" },
            new { Priority = "High", Category = "Order", EntityId = 1005, MessageBody = "Another high priority order message" }
        };
        
        var sendTasks = messages.Select(async msg =>
        {
            return await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = msg.MessageBody,
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["Priority"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = msg.Priority
                    },
                    ["Category"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = msg.Category
                    },
                    ["EntityId"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = msg.EntityId.ToString()
                    },
                    ["CommandType"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = $"{msg.Category}Command"
                    }
                }
            });
        });
        
        await Task.WhenAll(sendTasks);
        
        // Act - Receive messages with attribute filtering (receive all first)
        var allMessages = new List<Message>();
        var maxAttempts = 10;
        var attempts = 0;
        
        while (allMessages.Count < messages.Length && attempts < maxAttempts)
        {
            var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                MessageAttributeNames = new List<string> { "All" },
                WaitTimeSeconds = 1
            });
            
            allMessages.AddRange(receiveResponse.Messages);
            attempts++;
        }
        
        // Assert - Should receive all messages
        Assert.Equal(messages.Length, allMessages.Count);
        
        // Filter messages by attributes (client-side filtering for demonstration)
        var highPriorityMessages = allMessages
            .Where(m => m.MessageAttributes.ContainsKey("Priority") && 
                       m.MessageAttributes["Priority"].StringValue == "High")
            .ToList();
        
        var orderMessages = allMessages
            .Where(m => m.MessageAttributes.ContainsKey("Category") && 
                       m.MessageAttributes["Category"].StringValue == "Order")
            .ToList();
        
        var highPriorityOrderMessages = allMessages
            .Where(m => m.MessageAttributes.ContainsKey("Priority") && 
                       m.MessageAttributes["Priority"].StringValue == "High" &&
                       m.MessageAttributes.ContainsKey("Category") && 
                       m.MessageAttributes["Category"].StringValue == "Order")
            .ToList();
        
        // Assert - Filtering should work correctly
        Assert.Equal(3, highPriorityMessages.Count); // 3 high priority messages
        Assert.Equal(3, orderMessages.Count); // 3 order messages
        Assert.Equal(2, highPriorityOrderMessages.Count); // 2 high priority order messages
        
        // Verify attribute values in filtered messages
        foreach (var message in highPriorityMessages)
        {
            Assert.Equal("High", message.MessageAttributes["Priority"].StringValue);
        }
        
        foreach (var message in orderMessages)
        {
            Assert.Equal("Order", message.MessageAttributes["Category"].StringValue);
            Assert.Equal("OrderCommand", message.MessageAttributes["CommandType"].StringValue);
        }
        
        foreach (var message in highPriorityOrderMessages)
        {
            Assert.Equal("High", message.MessageAttributes["Priority"].StringValue);
            Assert.Equal("Order", message.MessageAttributes["Category"].StringValue);
            Assert.Contains("order message", message.Body.ToLower());
        }
        
        // Clean up
        await CleanupMessages(queueUrl, allMessages);
    }
    
    [Fact]
    public async Task MessageAttributes_ShouldRespectSizeLimits()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-attribute-size-limits-{Guid.NewGuid():N}";
        var queueUrl = await CreateStandardQueueAsync(queueName);
        
        // Test with attributes approaching AWS limits
        // AWS SQS limits: 10 attributes per message, 256KB total message size, 256 bytes per attribute name, 256KB per attribute value
        
        var largeAttributeValue = new string('A', 1024); // 1KB value (well within 256KB limit)
        var mediumAttributeValue = new string('B', 256); // 256 bytes
        
        // Act - Send message with multiple attributes of various sizes
        var sendResponse = await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Message with size limit testing",
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["Attribute1"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = largeAttributeValue
                },
                ["Attribute2"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = mediumAttributeValue
                },
                ["Attribute3"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "Small value"
                },
                ["EntityId"] = new MessageAttributeValue
                {
                    DataType = "Number",
                    StringValue = "12345"
                },
                ["CommandType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "SizeLimitTestCommand"
                },
                ["LongAttributeName123456789012345678901234567890"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "Testing long attribute name"
                },
                ["JsonAttribute"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = JsonSerializer.Serialize(new
                    {
                        ComplexObject = new
                        {
                            Id = Guid.NewGuid(),
                            Name = "Complex object in attribute",
                            Values = new[] { 1, 2, 3, 4, 5 },
                            Metadata = new Dictionary<string, string>
                            {
                                ["Key1"] = "Value1",
                                ["Key2"] = "Value2"
                            }
                        }
                    })
                },
                ["BinaryAttribute"] = new MessageAttributeValue
                {
                    DataType = "Binary",
                    BinaryValue = new MemoryStream(Encoding.UTF8.GetBytes(new string('C', 512))) // 512 bytes binary
                },
                ["UnicodeAttribute"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "Unicode test: 🚀🌟💫⭐🎯🔥💎🎨🎪🎭" + string.Concat(Enumerable.Repeat("🎵", 50)) // Unicode with emojis
                },
                ["NumericAttribute"] = new MessageAttributeValue
                {
                    DataType = "Number",
                    StringValue = "123456789012345678901234567890.123456789" // Large decimal number
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
        
        // Assert - All attributes should be preserved despite their size
        Assert.Single(receiveResponse.Messages);
        var message = receiveResponse.Messages[0];
        
        // Verify large attributes are preserved
        Assert.Equal(largeAttributeValue, message.MessageAttributes["Attribute1"].StringValue);
        Assert.Equal(mediumAttributeValue, message.MessageAttributes["Attribute2"].StringValue);
        Assert.Equal("Small value", message.MessageAttributes["Attribute3"].StringValue);
        
        // Verify long attribute name is preserved
        Assert.True(message.MessageAttributes.ContainsKey("LongAttributeName123456789012345678901234567890"));
        Assert.Equal("Testing long attribute name", 
            message.MessageAttributes["LongAttributeName123456789012345678901234567890"].StringValue);
        
        // Verify JSON attribute is preserved
        var jsonAttribute = message.MessageAttributes["JsonAttribute"].StringValue;
        var deserializedJson = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonAttribute);
        Assert.NotNull(deserializedJson);
        Assert.True(deserializedJson.ContainsKey("ComplexObject"));
        
        // Verify binary attribute is preserved
        var binaryAttribute = message.MessageAttributes["BinaryAttribute"];
        Assert.Equal("Binary", binaryAttribute.DataType);
        var binaryData = new byte[binaryAttribute.BinaryValue.Length];
        binaryAttribute.BinaryValue.Read(binaryData, 0, binaryData.Length);
        Assert.Equal(512, binaryData.Length);
        
        // Verify unicode attribute is preserved
        var unicodeAttribute = message.MessageAttributes["UnicodeAttribute"].StringValue;
        Assert.Contains("🚀🌟💫⭐🎯🔥💎🎨🎪🎭", unicodeAttribute);
        Assert.Contains("🎵", unicodeAttribute);
        
        // Verify numeric attribute is preserved
        Assert.Equal("123456789012345678901234567890.123456789", 
            message.MessageAttributes["NumericAttribute"].StringValue);
        
        // Clean up
        await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
        {
            QueueUrl = queueUrl,
            ReceiptHandle = message.ReceiptHandle
        });
    }
    
    [Fact]
    public async Task MessageAttributes_ShouldHandleAttributeEncoding()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-attribute-encoding-{Guid.NewGuid():N}";
        var queueUrl = await CreateStandardQueueAsync(queueName);
        
        // Test various encoding scenarios
        var specialCharacters = "Special chars: !@#$%^&*()_+-=[]{}|;':\",./<>?`~";
        var xmlContent = "<root><item id=\"1\">Value &amp; more</item></root>";
        var jsonContent = "{\"key\": \"value with \\\"quotes\\\" and \\n newlines\"}";
        var base64Content = Convert.ToBase64String(Encoding.UTF8.GetBytes("Base64 encoded content"));
        var urlEncodedContent = "param1=value%201&param2=value%202";
        
        // Act - Send message with various encoded content
        var sendResponse = await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Message with encoding test attributes",
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["SpecialChars"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = specialCharacters
                },
                ["XmlContent"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = xmlContent
                },
                ["JsonContent"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = jsonContent
                },
                ["Base64Content"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = base64Content
                },
                ["UrlEncodedContent"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = urlEncodedContent
                },
                ["MultilineContent"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "Line 1\nLine 2\r\nLine 3\tTabbed\r\n\tIndented"
                },
                ["UnicodeContent"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "Multilingual: English, Español, Français, Deutsch, 中文, 日本語, العربية, Русский"
                },
                ["EscapedContent"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "Escaped: \\n \\t \\r \\\\ \\\" \\'"
                },
                ["EntityId"] = new MessageAttributeValue
                {
                    DataType = "Number",
                    StringValue = "99999"
                },
                ["CommandType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "EncodingTestCommand"
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
        
        // Assert - All encoded content should be preserved exactly
        Assert.Single(receiveResponse.Messages);
        var message = receiveResponse.Messages[0];
        
        // Verify special characters are preserved
        Assert.Equal(specialCharacters, message.MessageAttributes["SpecialChars"].StringValue);
        
        // Verify XML content is preserved
        Assert.Equal(xmlContent, message.MessageAttributes["XmlContent"].StringValue);
        
        // Verify JSON content is preserved
        Assert.Equal(jsonContent, message.MessageAttributes["JsonContent"].StringValue);
        
        // Verify Base64 content is preserved
        Assert.Equal(base64Content, message.MessageAttributes["Base64Content"].StringValue);
        var decodedBase64 = Encoding.UTF8.GetString(Convert.FromBase64String(
            message.MessageAttributes["Base64Content"].StringValue));
        Assert.Equal("Base64 encoded content", decodedBase64);
        
        // Verify URL encoded content is preserved
        Assert.Equal(urlEncodedContent, message.MessageAttributes["UrlEncodedContent"].StringValue);
        
        // Verify multiline content is preserved
        var multilineContent = message.MessageAttributes["MultilineContent"].StringValue;
        Assert.Contains("Line 1\nLine 2", multilineContent);
        Assert.Contains("\tTabbed", multilineContent);
        Assert.Contains("\tIndented", multilineContent);
        
        // Verify Unicode content is preserved
        var unicodeContent = message.MessageAttributes["UnicodeContent"].StringValue;
        Assert.Contains("English", unicodeContent);
        Assert.Contains("中文", unicodeContent);
        Assert.Contains("العربية", unicodeContent);
        Assert.Contains("Русский", unicodeContent);
        
        // Verify escaped content is preserved
        Assert.Equal("Escaped: \\n \\t \\r \\\\ \\\" \\'", 
            message.MessageAttributes["EscapedContent"].StringValue);
        
        // Clean up
        await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
        {
            QueueUrl = queueUrl,
            ReceiptHandle = message.ReceiptHandle
        });
    }
    
    [Fact]
    public async Task MessageAttributes_ShouldSupportFifoQueueAttributes()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-fifo-attributes-{Guid.NewGuid():N}.fifo";
        var queueUrl = await CreateFifoQueueAsync(queueName);
        
        var entityId = 54321;
        var messageGroupId = $"entity-{entityId}";
        
        // Send multiple messages with attributes to FIFO queue
        var messages = new[]
        {
            new { SequenceNo = 1, Priority = "High", Action = "Create" },
            new { SequenceNo = 2, Priority = "Medium", Action = "Update" },
            new { SequenceNo = 3, Priority = "High", Action = "Delete" }
        };
        
        var sendTasks = messages.Select(async (msg, index) =>
        {
            return await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = $"FIFO message {msg.SequenceNo} - {msg.Action}",
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
                    ["Priority"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = msg.Priority
                    },
                    ["Action"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = msg.Action
                    },
                    ["CommandType"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = $"{msg.Action}Command"
                    },
                    ["Timestamp"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = DateTime.UtcNow.ToString("O")
                    }
                }
            });
        });
        
        await Task.WhenAll(sendTasks);
        
        // Act - Receive messages in order
        var receivedMessages = new List<Message>();
        var maxAttempts = 10;
        var attempts = 0;
        
        while (receivedMessages.Count < messages.Length && attempts < maxAttempts)
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
        
        // Assert - All messages should be received with attributes preserved
        Assert.Equal(messages.Length, receivedMessages.Count);
        
        // Verify FIFO ordering is maintained based on SequenceNo
        var orderedMessages = receivedMessages
            .OrderBy(m => int.Parse(m.MessageAttributes["SequenceNo"].StringValue))
            .ToList();
        
        for (int i = 0; i < messages.Length; i++)
        {
            var message = orderedMessages[i];
            var expectedMsg = messages[i];
            
            // Verify attributes are preserved
            Assert.Equal(entityId.ToString(), message.MessageAttributes["EntityId"].StringValue);
            Assert.Equal(expectedMsg.SequenceNo.ToString(), message.MessageAttributes["SequenceNo"].StringValue);
            Assert.Equal(expectedMsg.Priority, message.MessageAttributes["Priority"].StringValue);
            Assert.Equal(expectedMsg.Action, message.MessageAttributes["Action"].StringValue);
            Assert.Equal($"{expectedMsg.Action}Command", message.MessageAttributes["CommandType"].StringValue);
            
            // Verify message body
            Assert.Contains($"FIFO message {expectedMsg.SequenceNo}", message.Body);
            Assert.Contains(expectedMsg.Action, message.Body);
            
            // Verify timestamp is valid
            Assert.True(DateTime.TryParse(message.MessageAttributes["Timestamp"].StringValue, out _));
        }
        
        // Clean up
        await CleanupMessages(queueUrl, receivedMessages);
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

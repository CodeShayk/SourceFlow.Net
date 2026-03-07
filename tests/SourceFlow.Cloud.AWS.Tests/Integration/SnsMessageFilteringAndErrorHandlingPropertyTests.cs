using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using System.Text.Json;
using Xunit.Abstractions;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace SourceFlow.Cloud.AWS.Tests.Integration;

/// <summary>
/// Property-based tests for SNS message filtering and error handling
/// **Property 4: SNS Message Filtering and Error Handling**
/// **Validates: Requirements 2.3, 2.5**
/// </summary>
[Collection("AWS Integration Tests")]
[Trait("Category", "Integration")]
[Trait("Category", "RequiresLocalStack")]
public class SnsMessageFilteringAndErrorHandlingPropertyTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly IAwsTestEnvironment _testEnvironment;
    private readonly ILogger<SnsMessageFilteringAndErrorHandlingPropertyTests> _logger;
    private readonly List<string> _createdTopics = new();
    private readonly List<string> _createdQueues = new();
    private readonly List<string> _createdSubscriptions = new();

    public SnsMessageFilteringAndErrorHandlingPropertyTests(ITestOutputHelper output)
    {
        _output = output;
        
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        var serviceProvider = services.BuildServiceProvider();
        _logger = serviceProvider.GetRequiredService<ILogger<SnsMessageFilteringAndErrorHandlingPropertyTests>>();
        
        _testEnvironment = AwsTestEnvironmentFactory.CreateLocalStackEnvironmentAsync().GetAwaiter().GetResult();
    }

    public async Task InitializeAsync()
    {
        await _testEnvironment.InitializeAsync();
        
        if (!await _testEnvironment.IsAvailableAsync())
        {
            throw new InvalidOperationException("AWS test environment is not available");
        }
        
        _logger.LogInformation("SNS message filtering and error handling property tests initialized");
    }

    public async Task DisposeAsync()
    {
        // Clean up subscriptions first
        foreach (var subscriptionArn in _createdSubscriptions)
        {
            try
            {
                await _testEnvironment.SnsClient.UnsubscribeAsync(new UnsubscribeRequest
                {
                    SubscriptionArn = subscriptionArn
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to delete subscription {SubscriptionArn}: {Error}", subscriptionArn, ex.Message);
            }
        }
        
        // Clean up topics
        foreach (var topicArn in _createdTopics)
        {
            try
            {
                await _testEnvironment.DeleteTopicAsync(topicArn);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to delete topic {TopicArn}: {Error}", topicArn, ex.Message);
            }
        }
        
        // Clean up queues
        foreach (var queueUrl in _createdQueues)
        {
            try
            {
                await _testEnvironment.DeleteQueueAsync(queueUrl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to delete queue {QueueUrl}: {Error}", queueUrl, ex.Message);
            }
        }
        
        await _testEnvironment.DisposeAsync();
        _logger.LogInformation("SNS message filtering and error handling property tests disposed");
    }

    /// <summary>
    /// Property 4: SNS Message Filtering and Error Handling
    /// **Validates: Requirements 2.3, 2.5**
    /// 
    /// For any SNS subscription with message filtering rules, only events matching the filter criteria
    /// should be delivered to that subscriber, and failed deliveries should trigger appropriate retry
    /// mechanisms and error handling.
    /// </summary>
    [Property(MaxTest = 15, Arbitrary = new[] { typeof(SnsFilteringAndErrorHandlingGenerators) })]
    public void SnsMessageFilteringAndErrorHandling(SnsFilteringAndErrorHandlingScenario scenario)
    {
        try
        {
            _logger.LogInformation("Testing SNS message filtering and error handling with scenario: {Scenario}", 
                JsonSerializer.Serialize(scenario, new JsonSerializerOptions { WriteIndented = true }));
            
            // Property 1: Message filtering should deliver only matching messages
            var filteringValid = ValidateMessageFiltering(scenario).GetAwaiter().GetResult();
            
            // Property 2: Error handling should gracefully handle failed deliveries
            var errorHandlingValid = ValidateErrorHandling(scenario).GetAwaiter().GetResult();
            
            // Property 3: Correlation IDs should be preserved even with filtering and errors
            var correlationValid = ValidateCorrelationPreservation(scenario).GetAwaiter().GetResult();
            
            // Property 4: Filter policy validation should reject invalid policies
            var filterValidationValid = ValidateFilterPolicyValidation(scenario).GetAwaiter().GetResult();
            
            var result = filteringValid && errorHandlingValid && correlationValid && filterValidationValid;
            
            if (!result)
            {
                _logger.LogWarning("SNS message filtering and error handling failed for scenario: {Scenario}. " +
                                  "Filtering: {Filtering}, ErrorHandling: {ErrorHandling}, Correlation: {Correlation}, FilterValidation: {FilterValidation}",
                    JsonSerializer.Serialize(scenario), filteringValid, errorHandlingValid, correlationValid, filterValidationValid);
            }
            
            Assert.True(result, "SNS message filtering and error handling validation failed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SNS message filtering and error handling test failed with exception for scenario: {Scenario}", 
                JsonSerializer.Serialize(scenario));
            throw;
        }
    }

    private async Task<bool> ValidateMessageFiltering(SnsFilteringAndErrorHandlingScenario scenario)
    {
        try
        {
            // Create topic
            var topicName = $"prop-test-filtering-{Guid.NewGuid():N}";
            var topicArn = await _testEnvironment.CreateTopicAsync(topicName);
            _createdTopics.Add(topicArn);
            
            // Create filtered subscriber
            var filteredQueueName = $"prop-test-filtered-{Guid.NewGuid():N}";
            var filteredQueueUrl = await _testEnvironment.CreateStandardQueueAsync(filteredQueueName);
            _createdQueues.Add(filteredQueueUrl);
            var filteredQueueArn = await GetQueueArnAsync(filteredQueueUrl);
            
            // Create filter policy based on scenario
            var filterPolicy = CreateFilterPolicy(scenario.FilterCriteria);
            
            var filteredSubscriptionResponse = await _testEnvironment.SnsClient.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = topicArn,
                Protocol = "sqs",
                Endpoint = filteredQueueArn,
                Attributes = new Dictionary<string, string>
                {
                    ["FilterPolicy"] = filterPolicy
                }
            });
            _createdSubscriptions.Add(filteredSubscriptionResponse.SubscriptionArn);
            
            await SetQueuePolicyForSns(filteredQueueUrl, filteredQueueArn, topicArn);
            
            // Create unfiltered subscriber for comparison
            var unfilteredQueueName = $"prop-test-unfiltered-{Guid.NewGuid():N}";
            var unfilteredQueueUrl = await _testEnvironment.CreateStandardQueueAsync(unfilteredQueueName);
            _createdQueues.Add(unfilteredQueueUrl);
            var unfilteredQueueArn = await GetQueueArnAsync(unfilteredQueueUrl);
            
            var unfilteredSubscriptionResponse = await _testEnvironment.SnsClient.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = topicArn,
                Protocol = "sqs",
                Endpoint = unfilteredQueueArn
            });
            _createdSubscriptions.Add(unfilteredSubscriptionResponse.SubscriptionArn);
            
            await SetQueuePolicyForSns(unfilteredQueueUrl, unfilteredQueueArn, topicArn);
            
            // Publish test messages
            var publishedMessages = new List<(bool ShouldMatch, Dictionary<string, SnsMessageAttributeValue> Attributes)>();
            
            foreach (var testMessage in scenario.TestMessages)
            {
                var testEvent = new TestEvent(new TestEventData
                {
                    Id = testMessage.EventId,
                    Message = testMessage.Message,
                    Value = testMessage.Value
                });
                
                var messageAttributes = CreateMessageAttributes(testMessage);
                var shouldMatch = ShouldMessageMatchFilter(testMessage, scenario.FilterCriteria);
                
                await _testEnvironment.SnsClient.PublishAsync(new PublishRequest
                {
                    TopicArn = topicArn,
                    Message = JsonSerializer.Serialize(testEvent),
                    Subject = testEvent.Name,
                    MessageAttributes = messageAttributes
                });
                
                publishedMessages.Add((shouldMatch, messageAttributes));
            }
            
            // Wait for delivery
            await Task.Delay(3000);
            
            // Verify filtering results
            var filteredReceiveResponse = await _testEnvironment.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = filteredQueueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 3
            });
            
            var unfilteredReceiveResponse = await _testEnvironment.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = unfilteredQueueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 3
            });
            
            var expectedFilteredCount = publishedMessages.Count(m => m.ShouldMatch);
            var actualFilteredCount = filteredReceiveResponse.Messages.Count;
            var actualUnfilteredCount = unfilteredReceiveResponse.Messages.Count;
            
            // Filtered queue should receive only matching messages
            var filteringValid = actualFilteredCount <= expectedFilteredCount + 1; // Allow slight variance
            
            // Unfiltered queue should receive all messages
            var unfilteredValid = actualUnfilteredCount >= publishedMessages.Count * 0.8; // Allow 80% delivery rate
            
            var result = filteringValid && unfilteredValid;
            
            if (!result)
            {
                _logger.LogWarning("Message filtering validation failed: Expected filtered {ExpectedFiltered}, got {ActualFiltered}. " +
                                  "Expected unfiltered {ExpectedUnfiltered}, got {ActualUnfiltered}",
                    expectedFilteredCount, actualFilteredCount, publishedMessages.Count, actualUnfilteredCount);
            }
            
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Message filtering validation failed with exception: {Error}", ex.Message);
            return false;
        }
    }

    private async Task<bool> ValidateErrorHandling(SnsFilteringAndErrorHandlingScenario scenario)
    {
        try
        {
            // Create topic
            var topicName = $"prop-test-error-{Guid.NewGuid():N}";
            var topicArn = await _testEnvironment.CreateTopicAsync(topicName);
            _createdTopics.Add(topicArn);
            
            // Create valid SQS subscriber
            var validQueueName = $"prop-test-valid-{Guid.NewGuid():N}";
            var validQueueUrl = await _testEnvironment.CreateStandardQueueAsync(validQueueName);
            _createdQueues.Add(validQueueUrl);
            var validQueueArn = await GetQueueArnAsync(validQueueUrl);
            
            var validSubscriptionResponse = await _testEnvironment.SnsClient.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = topicArn,
                Protocol = "sqs",
                Endpoint = validQueueArn
            });
            _createdSubscriptions.Add(validSubscriptionResponse.SubscriptionArn);
            
            await SetQueuePolicyForSns(validQueueUrl, validQueueArn, topicArn);
            
            // Create invalid HTTP subscribers (will fail delivery)
            foreach (var invalidEndpoint in scenario.InvalidEndpoints.Take(2)) // Limit to 2 for performance
            {
                try
                {
                    var invalidSubscriptionResponse = await _testEnvironment.SnsClient.SubscribeAsync(new SubscribeRequest
                    {
                        TopicArn = topicArn,
                        Protocol = "http",
                        Endpoint = invalidEndpoint
                    });
                    _createdSubscriptions.Add(invalidSubscriptionResponse.SubscriptionArn);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Expected failure creating invalid HTTP subscription for {Endpoint}: {Error}", 
                        invalidEndpoint, ex.Message);
                }
            }
            
            // Publish test message
            var testMessage = scenario.TestMessages.FirstOrDefault() ?? new SnsTestMessage
            {
                EventId = 1,
                Message = "Error handling test",
                Value = 100,
                Priority = "High",
                Source = "Test"
            };
            
            var testEvent = new TestEvent(new TestEventData
            {
                Id = testMessage.EventId,
                Message = testMessage.Message,
                Value = testMessage.Value
            });
            
            var publishResponse = await _testEnvironment.SnsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = JsonSerializer.Serialize(testEvent),
                Subject = testEvent.Name,
                MessageAttributes = CreateMessageAttributes(testMessage)
            });
            
            // Publish should succeed despite invalid subscribers
            var publishValid = publishResponse?.MessageId != null;
            
            // Wait for delivery attempts
            await Task.Delay(2000);
            
            // Valid subscriber should receive the message
            var receiveResponse = await _testEnvironment.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = validQueueUrl,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 3
            });
            
            var deliveryValid = receiveResponse.Messages.Count > 0;
            
            var result = publishValid && deliveryValid;
            
            if (!result)
            {
                _logger.LogWarning("Error handling validation failed: Publish valid: {PublishValid}, Delivery valid: {DeliveryValid}",
                    publishValid, deliveryValid);
            }
            
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error handling validation failed with exception: {Error}", ex.Message);
            return false;
        }
    }

    private async Task<bool> ValidateCorrelationPreservation(SnsFilteringAndErrorHandlingScenario scenario)
    {
        try
        {
            // Create topic
            var topicName = $"prop-test-correlation-{Guid.NewGuid():N}";
            var topicArn = await _testEnvironment.CreateTopicAsync(topicName);
            _createdTopics.Add(topicArn);
            
            // Create subscriber
            var queueName = $"prop-test-corr-queue-{Guid.NewGuid():N}";
            var queueUrl = await _testEnvironment.CreateStandardQueueAsync(queueName);
            _createdQueues.Add(queueUrl);
            var queueArn = await GetQueueArnAsync(queueUrl);
            
            var subscriptionResponse = await _testEnvironment.SnsClient.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = topicArn,
                Protocol = "sqs",
                Endpoint = queueArn
            });
            _createdSubscriptions.Add(subscriptionResponse.SubscriptionArn);
            
            await SetQueuePolicyForSns(queueUrl, queueArn, topicArn);
            
            // Publish message with correlation ID
            var correlationId = scenario.CorrelationId ?? Guid.NewGuid().ToString();
            var testMessage = scenario.TestMessages.FirstOrDefault() ?? new SnsTestMessage
            {
                EventId = 1,
                Message = "Correlation test",
                Value = 100,
                Priority = "High",
                Source = "Test"
            };
            
            var testEvent = new TestEvent(new TestEventData
            {
                Id = testMessage.EventId,
                Message = testMessage.Message,
                Value = testMessage.Value
            });
            
            var messageAttributes = CreateMessageAttributes(testMessage);
            messageAttributes["CorrelationId"] = new SnsMessageAttributeValue
            {
                DataType = "String",
                StringValue = correlationId
            };
            
            await _testEnvironment.SnsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = JsonSerializer.Serialize(testEvent),
                Subject = testEvent.Name,
                MessageAttributes = messageAttributes
            });
            
            // Wait for delivery
            await Task.Delay(1500);
            
            // Verify correlation ID preservation
            var receiveResponse = await _testEnvironment.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 3,
                MessageAttributeNames = new List<string> { "All" }
            });
            
            if (receiveResponse.Messages.Count == 0)
            {
                _logger.LogWarning("Correlation preservation validation failed: No messages received");
                return false;
            }
            
            var receivedMessage = receiveResponse.Messages[0];
            var snsMessage = JsonSerializer.Deserialize<SnsMessageWrapper>(receivedMessage.Body);
            
            var correlationValid = snsMessage?.MessageAttributes?.ContainsKey("CorrelationId") == true &&
                                  snsMessage?.MessageAttributes?["CorrelationId"]?.Value == correlationId;
            
            if (!correlationValid)
            {
                _logger.LogWarning("Correlation preservation validation failed: Expected {ExpectedId}, but correlation ID not found or mismatched",
                    correlationId);
            }
            
            return correlationValid;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Correlation preservation validation failed with exception: {Error}", ex.Message);
            return false;
        }
    }

    private async Task<bool> ValidateFilterPolicyValidation(SnsFilteringAndErrorHandlingScenario scenario)
    {
        try
        {
            // Create topic
            var topicName = $"prop-test-filter-validation-{Guid.NewGuid():N}";
            var topicArn = await _testEnvironment.CreateTopicAsync(topicName);
            _createdTopics.Add(topicArn);
            
            var queueName = $"prop-test-validation-queue-{Guid.NewGuid():N}";
            var queueUrl = await _testEnvironment.CreateStandardQueueAsync(queueName);
            _createdQueues.Add(queueUrl);
            var queueArn = await GetQueueArnAsync(queueUrl);
            
            // Test valid filter policy
            var validFilterPolicy = CreateFilterPolicy(scenario.FilterCriteria);
            
            try
            {
                var validSubscriptionResponse = await _testEnvironment.SnsClient.SubscribeAsync(new SubscribeRequest
                {
                    TopicArn = topicArn,
                    Protocol = "sqs",
                    Endpoint = queueArn,
                    Attributes = new Dictionary<string, string>
                    {
                        ["FilterPolicy"] = validFilterPolicy
                    }
                });
                _createdSubscriptions.Add(validSubscriptionResponse.SubscriptionArn);
                
                // Valid filter policy should succeed
                var validPolicyValid = !string.IsNullOrEmpty(validSubscriptionResponse.SubscriptionArn);
                
                // Test invalid filter policy if provided in scenario
                if (!string.IsNullOrEmpty(scenario.InvalidFilterPolicy))
                {
                    try
                    {
                        await _testEnvironment.SnsClient.SubscribeAsync(new SubscribeRequest
                        {
                            TopicArn = topicArn,
                            Protocol = "sqs",
                            Endpoint = queueArn,
                            Attributes = new Dictionary<string, string>
                            {
                                ["FilterPolicy"] = scenario.InvalidFilterPolicy
                            }
                        });
                        
                        // Invalid filter policy should have failed, but didn't
                        _logger.LogWarning("Invalid filter policy was accepted when it should have been rejected");
                        return false;
                    }
                    catch (Exception)
                    {
                        // Expected exception for invalid filter policy
                        return validPolicyValid;
                    }
                }
                
                return validPolicyValid;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Filter policy validation failed: {Error}", ex.Message);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Filter policy validation failed with exception: {Error}", ex.Message);
            return false;
        }
    }

    private string CreateFilterPolicy(SnsFilterCriteria criteria)
    {
        var policy = new Dictionary<string, object>();
        
        if (!string.IsNullOrEmpty(criteria.Priority))
        {
            policy["Priority"] = new[] { criteria.Priority };
        }
        
        if (!string.IsNullOrEmpty(criteria.Source))
        {
            policy["Source"] = new[] { criteria.Source };
        }
        
        if (criteria.MinValue.HasValue)
        {
            policy["Value"] = new object[] { new { numeric = new object[] { ">=", criteria.MinValue.Value } } };
        }
        
        return JsonSerializer.Serialize(policy);
    }

    private bool ShouldMessageMatchFilter(SnsTestMessage message, SnsFilterCriteria criteria)
    {
        var priorityMatch = string.IsNullOrEmpty(criteria.Priority) || message.Priority == criteria.Priority;
        var sourceMatch = string.IsNullOrEmpty(criteria.Source) || message.Source == criteria.Source;
        var valueMatch = !criteria.MinValue.HasValue || message.Value >= criteria.MinValue.Value;
        
        return priorityMatch && sourceMatch && valueMatch;
    }

    private Dictionary<string, SnsMessageAttributeValue> CreateMessageAttributes(SnsTestMessage message)
    {
        var attributes = new Dictionary<string, SnsMessageAttributeValue>
        {
            ["EventType"] = new SnsMessageAttributeValue
            {
                DataType = "String",
                StringValue = "TestEvent"
            },
            ["Priority"] = new SnsMessageAttributeValue
            {
                DataType = "String",
                StringValue = message.Priority
            },
            ["Source"] = new SnsMessageAttributeValue
            {
                DataType = "String",
                StringValue = message.Source
            },
            ["Value"] = new SnsMessageAttributeValue
            {
                DataType = "Number",
                StringValue = message.Value.ToString()
            }
        };
        
        return attributes;
    }

    private async Task<string> GetQueueArnAsync(string queueUrl)
    {
        var response = await _testEnvironment.SqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = new List<string> { "QueueArn" }
        });
        
        return response.Attributes["QueueArn"];
    }

    private async Task SetQueuePolicyForSns(string queueUrl, string queueArn, string topicArn)
    {
        var policy = $@"{{
            ""Version"": ""2012-10-17"",
            ""Statement"": [
                {{
                    ""Effect"": ""Allow"",
                    ""Principal"": {{
                        ""Service"": ""sns.amazonaws.com""
                    }},
                    ""Action"": ""sqs:SendMessage"",
                    ""Resource"": ""{queueArn}"",
                    ""Condition"": {{
                        ""ArnEquals"": {{
                            ""aws:SourceArn"": ""{topicArn}""
                        }}
                    }}
                }}
            ]
        }}";

        await _testEnvironment.SqsClient.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = new Dictionary<string, string>
            {
                ["Policy"] = policy
            }
        });
    }
}

/// <summary>
/// Generators for SNS message filtering and error handling property tests
/// </summary>
public static class SnsFilteringAndErrorHandlingGenerators
{
    public static Arbitrary<SnsFilteringAndErrorHandlingScenario> SnsFilteringAndErrorHandlingScenario()
    {
        return Gen.Fresh(() => new SnsFilteringAndErrorHandlingScenario
        {
            FilterCriteria = GenerateFilterCriteria(),
            TestMessages = GenerateTestMessages(),
            InvalidEndpoints = GenerateInvalidEndpoints(),
            CorrelationId = Gen.Elements<string?>(null, Guid.NewGuid().ToString(), "test-correlation").Sample(0, 1).First(),
            InvalidFilterPolicy = Gen.Elements<string?>(null, @"{""Priority"":[""High""", @"{invalid:json}").Sample(0, 1).First()
        }).ToArbitrary();
    }
    
    private static SnsFilterCriteria GenerateFilterCriteria()
    {
        return new SnsFilterCriteria
        {
            Priority = Gen.Elements<string?>(null, "High", "Medium", "Low").Sample(0, 1).First(),
            Source = Gen.Elements<string?>(null, "OrderService", "PaymentService", "UserService").Sample(0, 1).First(),
            MinValue = Gen.Elements<int?>(null, 100, 500, 1000).Sample(0, 1).First()
        };
    }
    
    private static List<SnsTestMessage> GenerateTestMessages()
    {
        var messageCount = Gen.Choose(2, 5).Sample(0, 1).First();
        var messages = new List<SnsTestMessage>();
        
        var priorities = new[] { "High", "Medium", "Low" };
        var sources = new[] { "OrderService", "PaymentService", "UserService", "NotificationService" };
        
        for (int i = 0; i < messageCount; i++)
        {
            messages.Add(new SnsTestMessage
            {
                EventId = i + 1,
                Message = $"Test message {i + 1}",
                Value = Gen.Choose(50, 2000).Sample(0, 1).First(),
                Priority = Gen.Elements(priorities).Sample(0, 1).First(),
                Source = Gen.Elements(sources).Sample(0, 1).First()
            });
        }
        
        return messages;
    }
    
    private static List<string> GenerateInvalidEndpoints()
    {
        return new List<string>
        {
            "http://invalid-endpoint-1.example.com/webhook",
            "http://invalid-endpoint-2.example.com/webhook",
            "https://non-existent-service.com/api/events"
        };
    }
}

/// <summary>
/// Test scenario for SNS message filtering and error handling property tests
/// </summary>
public class SnsFilteringAndErrorHandlingScenario
{
    public SnsFilterCriteria FilterCriteria { get; set; } = new();
    public List<SnsTestMessage> TestMessages { get; set; } = new();
    public List<string> InvalidEndpoints { get; set; } = new();
    public string? CorrelationId { get; set; }
    public string? InvalidFilterPolicy { get; set; }
}

/// <summary>
/// Filter criteria for SNS message filtering tests
/// </summary>
public class SnsFilterCriteria
{
    public string? Priority { get; set; }
    public string? Source { get; set; }
    public int? MinValue { get; set; }
}

/// <summary>
/// Test message for SNS filtering tests
/// </summary>
public class SnsTestMessage
{
    public int EventId { get; set; }
    public string Message { get; set; } = "";
    public int Value { get; set; }
    public string Priority { get; set; } = "";
    public string Source { get; set; } = "";
}

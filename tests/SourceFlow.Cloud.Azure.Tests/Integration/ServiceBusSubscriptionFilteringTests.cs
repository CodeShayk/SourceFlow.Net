using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Azure.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Azure.Tests.Integration;

/// <summary>
/// Integration tests for Azure Service Bus subscription filtering including filter expressions,
/// property-based filtering, SQL filter rules, and subscription-specific event delivery.
/// Feature: azure-cloud-integration-testing
/// Task: 5.2 Create Azure Service Bus subscription filtering tests
/// </summary>
public class ServiceBusSubscriptionFilteringTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;
    private IAzureTestEnvironment? _testEnvironment;
    private ServiceBusClient? _serviceBusClient;
    private ServiceBusTestHelpers? _testHelpers;
    private ServiceBusAdministrationClient? _adminClient;

    public ServiceBusSubscriptionFilteringTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Debug);
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

    #region Subscription Filtering Tests (Requirement 2.2)

    /// <summary>
    /// Test: Subscription filters with various event properties
    /// Validates: Requirement 2.2
    /// </summary>
    [Fact]
    public async Task SubscriptionFiltering_PropertyBasedFilter_DeliversMatchingMessagesOnly()
    {
        // Arrange
        var topicName = "filter-test-topic";
        var highPrioritySubscription = "high-priority-sub";
        var lowPrioritySubscription = "low-priority-sub";

        await CreateTopicWithFilteredSubscriptionsAsync(topicName, highPrioritySubscription, lowPrioritySubscription);

        // Create messages with different priorities
        var highPriorityMessages = new[]
        {
            CreateMessageWithPriority("Message1", "High"),
            CreateMessageWithPriority("Message2", "High"),
            CreateMessageWithPriority("Message3", "High")
        };

        var lowPriorityMessages = new[]
        {
            CreateMessageWithPriority("Message4", "Low"),
            CreateMessageWithPriority("Message5", "Low")
        };

        // Act
        foreach (var message in highPriorityMessages.Concat(lowPriorityMessages))
        {
            await _testHelpers!.SendMessageToTopicAsync(topicName, message);
        }

        // Assert
        var highPriorityReceived = await _testHelpers!.ReceiveMessagesFromSubscriptionAsync(
            topicName, highPrioritySubscription, 3, TimeSpan.FromSeconds(15));
        
        var lowPriorityReceived = await _testHelpers.ReceiveMessagesFromSubscriptionAsync(
            topicName, lowPrioritySubscription, 2, TimeSpan.FromSeconds(15));

        Assert.Equal(3, highPriorityReceived.Count);
        Assert.Equal(2, lowPriorityReceived.Count);

        // Verify high priority subscription only received high priority messages
        Assert.All(highPriorityReceived, msg => 
            Assert.Equal("High", msg.ApplicationProperties["Priority"]));

        // Verify low priority subscription only received low priority messages
        Assert.All(lowPriorityReceived, msg => 
            Assert.Equal("Low", msg.ApplicationProperties["Priority"]));
    }

    /// <summary>
    /// Test: Filter expression evaluation and matching
    /// Validates: Requirement 2.2
    /// </summary>
    [Fact]
    public async Task SubscriptionFiltering_SqlFilterExpression_EvaluatesCorrectly()
    {
        // Arrange
        var topicName = "sql-filter-topic";
        var categorySubscription = "category-electronics";

        await CreateTopicAsync(topicName);
        await CreateSubscriptionWithSqlFilterAsync(
            topicName, 
            categorySubscription, 
            "Category = 'Electronics' AND Price > 100");

        // Create messages with different categories and prices
        var messages = new[]
        {
            CreateMessageWithCategoryAndPrice("Product1", "Electronics", 150),
            CreateMessageWithCategoryAndPrice("Product2", "Electronics", 50),
            CreateMessageWithCategoryAndPrice("Product3", "Books", 200),
            CreateMessageWithCategoryAndPrice("Product4", "Electronics", 250)
        };

        // Act
        foreach (var message in messages)
        {
            await _testHelpers!.SendMessageToTopicAsync(topicName, message);
        }

        // Assert
        var receivedMessages = await _testHelpers!.ReceiveMessagesFromSubscriptionAsync(
            topicName, categorySubscription, 2, TimeSpan.FromSeconds(15));

        Assert.Equal(2, receivedMessages.Count);
        
        // Verify only Electronics with Price > 100 were received
        Assert.All(receivedMessages, msg =>
        {
            Assert.Equal("Electronics", msg.ApplicationProperties["Category"]);
            Assert.True((int)msg.ApplicationProperties["Price"] > 100);
        });
    }

    /// <summary>
    /// Test: Subscription-specific event delivery
    /// Validates: Requirement 2.2
    /// </summary>
    [Fact]
    public async Task SubscriptionFiltering_MultipleFilters_DeliverToCorrectSubscriptions()
    {
        // Arrange
        var topicName = "multi-filter-topic";
        var urgentSubscription = "urgent-messages";
        var normalSubscription = "normal-messages";
        var allSubscription = "all-messages";

        await CreateTopicAsync(topicName);
        
        // Urgent: Priority = 'Urgent'
        await CreateSubscriptionWithSqlFilterAsync(
            topicName, urgentSubscription, "Priority = 'Urgent'");
        
        // Normal: Priority = 'Normal'
        await CreateSubscriptionWithSqlFilterAsync(
            topicName, normalSubscription, "Priority = 'Normal'");
        
        // All: No filter (receives everything)
        await CreateSubscriptionWithSqlFilterAsync(
            topicName, allSubscription, "1=1");

        var messages = new[]
        {
            CreateMessageWithPriority("Msg1", "Urgent"),
            CreateMessageWithPriority("Msg2", "Normal"),
            CreateMessageWithPriority("Msg3", "Urgent"),
            CreateMessageWithPriority("Msg4", "Normal"),
            CreateMessageWithPriority("Msg5", "Urgent")
        };

        // Act
        foreach (var message in messages)
        {
            await _testHelpers!.SendMessageToTopicAsync(topicName, message);
        }

        // Assert
        var urgentReceived = await _testHelpers!.ReceiveMessagesFromSubscriptionAsync(
            topicName, urgentSubscription, 3, TimeSpan.FromSeconds(15));
        
        var normalReceived = await _testHelpers.ReceiveMessagesFromSubscriptionAsync(
            topicName, normalSubscription, 2, TimeSpan.FromSeconds(15));
        
        var allReceived = await _testHelpers.ReceiveMessagesFromSubscriptionAsync(
            topicName, allSubscription, 5, TimeSpan.FromSeconds(15));

        Assert.Equal(3, urgentReceived.Count);
        Assert.Equal(2, normalReceived.Count);
        Assert.Equal(5, allReceived.Count);
    }

    /// <summary>
    /// Test: Correlation filter matching
    /// Validates: Requirement 2.2
    /// </summary>
    [Fact]
    public async Task SubscriptionFiltering_CorrelationFilter_MatchesCorrectly()
    {
        // Arrange
        var topicName = "correlation-filter-topic";
        var specificCorrelationSubscription = "specific-correlation";
        var targetCorrelationId = Guid.NewGuid().ToString();

        await CreateTopicAsync(topicName);
        await CreateSubscriptionWithCorrelationFilterAsync(
            topicName, specificCorrelationSubscription, targetCorrelationId);

        var messages = new[]
        {
            CreateMessageWithCorrelationId("Msg1", targetCorrelationId),
            CreateMessageWithCorrelationId("Msg2", Guid.NewGuid().ToString()),
            CreateMessageWithCorrelationId("Msg3", targetCorrelationId),
            CreateMessageWithCorrelationId("Msg4", Guid.NewGuid().ToString())
        };

        // Act
        foreach (var message in messages)
        {
            await _testHelpers!.SendMessageToTopicAsync(topicName, message);
        }

        // Assert
        var receivedMessages = await _testHelpers!.ReceiveMessagesFromSubscriptionAsync(
            topicName, specificCorrelationSubscription, 2, TimeSpan.FromSeconds(15));

        Assert.Equal(2, receivedMessages.Count);
        Assert.All(receivedMessages, msg => 
            Assert.Equal(targetCorrelationId, msg.CorrelationId));
    }

    /// <summary>
    /// Test: Complex filter expressions with multiple conditions
    /// Validates: Requirement 2.2
    /// </summary>
    [Fact]
    public async Task SubscriptionFiltering_ComplexExpression_EvaluatesAllConditions()
    {
        // Arrange
        var topicName = "complex-filter-topic";
        var complexSubscription = "complex-filter-sub";

        await CreateTopicAsync(topicName);
        await CreateSubscriptionWithSqlFilterAsync(
            topicName, 
            complexSubscription, 
            "(Category = 'Electronics' OR Category = 'Computers') AND Price > 50 AND InStock = 'true'");

        var messages = new[]
        {
            CreateComplexMessage("P1", "Electronics", 100, "true"),  // Match
            CreateComplexMessage("P2", "Electronics", 30, "true"),   // No match (price)
            CreateComplexMessage("P3", "Computers", 75, "true"),     // Match
            CreateComplexMessage("P4", "Books", 100, "true"),        // No match (category)
            CreateComplexMessage("P5", "Electronics", 100, "false"), // No match (stock)
            CreateComplexMessage("P6", "Computers", 200, "true")     // Match
        };

        // Act
        foreach (var message in messages)
        {
            await _testHelpers!.SendMessageToTopicAsync(topicName, message);
        }

        // Assert
        var receivedMessages = await _testHelpers!.ReceiveMessagesFromSubscriptionAsync(
            topicName, complexSubscription, 3, TimeSpan.FromSeconds(15));

        Assert.Equal(3, receivedMessages.Count);
        
        // Verify all conditions are met
        Assert.All(receivedMessages, msg =>
        {
            var category = msg.ApplicationProperties["Category"].ToString();
            var price = (int)msg.ApplicationProperties["Price"];
            var inStock = msg.ApplicationProperties["InStock"].ToString();
            
            Assert.True(category == "Electronics" || category == "Computers");
            Assert.True(price > 50);
            Assert.Equal("true", inStock);
        });
    }

    /// <summary>
    /// Test: No matching subscription receives no messages
    /// Validates: Requirement 2.2
    /// </summary>
    [Fact]
    public async Task SubscriptionFiltering_NoMatchingFilter_ReceivesNoMessages()
    {
        // Arrange
        var topicName = "no-match-topic";
        var strictSubscription = "strict-filter-sub";

        await CreateTopicAsync(topicName);
        await CreateSubscriptionWithSqlFilterAsync(
            topicName, strictSubscription, "Category = 'NonExistent'");

        var messages = new[]
        {
            CreateMessageWithCategoryAndPrice("P1", "Electronics", 100),
            CreateMessageWithCategoryAndPrice("P2", "Books", 50),
            CreateMessageWithCategoryAndPrice("P3", "Computers", 200)
        };

        // Act
        foreach (var message in messages)
        {
            await _testHelpers!.SendMessageToTopicAsync(topicName, message);
        }

        // Assert - Try to receive with a short timeout
        var receivedMessages = await _testHelpers!.ReceiveMessagesFromSubscriptionAsync(
            topicName, strictSubscription, 1, TimeSpan.FromSeconds(5));

        Assert.Empty(receivedMessages);
    }

    /// <summary>
    /// Test: Filter with IN operator
    /// Validates: Requirement 2.2
    /// </summary>
    [Fact]
    public async Task SubscriptionFiltering_InOperator_MatchesMultipleValues()
    {
        // Arrange
        var topicName = "in-operator-topic";
        var multiValueSubscription = "multi-value-sub";

        await CreateTopicAsync(topicName);
        await CreateSubscriptionWithSqlFilterAsync(
            topicName, 
            multiValueSubscription, 
            "Status IN ('Pending', 'Processing', 'Completed')");

        var messages = new[]
        {
            CreateMessageWithStatus("Order1", "Pending"),
            CreateMessageWithStatus("Order2", "Cancelled"),
            CreateMessageWithStatus("Order3", "Processing"),
            CreateMessageWithStatus("Order4", "Failed"),
            CreateMessageWithStatus("Order5", "Completed")
        };

        // Act
        foreach (var message in messages)
        {
            await _testHelpers!.SendMessageToTopicAsync(topicName, message);
        }

        // Assert
        var receivedMessages = await _testHelpers!.ReceiveMessagesFromSubscriptionAsync(
            topicName, multiValueSubscription, 3, TimeSpan.FromSeconds(15));

        Assert.Equal(3, receivedMessages.Count);
        
        var validStatuses = new[] { "Pending", "Processing", "Completed" };
        Assert.All(receivedMessages, msg =>
        {
            var status = msg.ApplicationProperties["Status"].ToString();
            Assert.Contains(status, validStatuses);
        });
    }

    #endregion

    #region Helper Methods

    private async Task CreateTopicWithFilteredSubscriptionsAsync(
        string topicName, 
        string highPrioritySubscription, 
        string lowPrioritySubscription)
    {
        await CreateTopicAsync(topicName);
        
        // High priority subscription
        await CreateSubscriptionWithSqlFilterAsync(
            topicName, highPrioritySubscription, "Priority = 'High'");
        
        // Low priority subscription
        await CreateSubscriptionWithSqlFilterAsync(
            topicName, lowPrioritySubscription, "Priority = 'Low'");
    }

    private async Task CreateTopicAsync(string topicName)
    {
        try
        {
            if (!await _adminClient!.TopicExistsAsync(topicName))
            {
                var topicOptions = new CreateTopicOptions(topicName)
                {
                    DefaultMessageTimeToLive = TimeSpan.FromDays(14),
                    EnableBatchedOperations = true
                };

                await _adminClient.CreateTopicAsync(topicOptions);
                _output.WriteLine($"Created topic: {topicName}");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Error creating topic {topicName}: {ex.Message}");
            throw;
        }
    }

    private async Task CreateSubscriptionWithSqlFilterAsync(
        string topicName, 
        string subscriptionName, 
        string sqlFilter)
    {
        try
        {
            if (!await _adminClient!.SubscriptionExistsAsync(topicName, subscriptionName))
            {
                var subscriptionOptions = new CreateSubscriptionOptions(topicName, subscriptionName)
                {
                    MaxDeliveryCount = 10,
                    LockDuration = TimeSpan.FromMinutes(5)
                };

                await _adminClient.CreateSubscriptionAsync(subscriptionOptions);
                
                // Remove default rule and add SQL filter
                await _adminClient.DeleteRuleAsync(topicName, subscriptionName, "$Default");
                
                var ruleOptions = new CreateRuleOptions("SqlFilter", new SqlRuleFilter(sqlFilter));
                await _adminClient.CreateRuleAsync(topicName, subscriptionName, ruleOptions);
                
                _output.WriteLine($"Created subscription {subscriptionName} with SQL filter: {sqlFilter}");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Error creating subscription {subscriptionName}: {ex.Message}");
            throw;
        }
    }

    private async Task CreateSubscriptionWithCorrelationFilterAsync(
        string topicName, 
        string subscriptionName, 
        string correlationId)
    {
        try
        {
            if (!await _adminClient!.SubscriptionExistsAsync(topicName, subscriptionName))
            {
                var subscriptionOptions = new CreateSubscriptionOptions(topicName, subscriptionName)
                {
                    MaxDeliveryCount = 10,
                    LockDuration = TimeSpan.FromMinutes(5)
                };

                await _adminClient.CreateSubscriptionAsync(subscriptionOptions);
                
                // Remove default rule and add correlation filter
                await _adminClient.DeleteRuleAsync(topicName, subscriptionName, "$Default");
                
                var correlationFilter = new CorrelationRuleFilter
                {
                    CorrelationId = correlationId
                };
                
                var ruleOptions = new CreateRuleOptions("CorrelationFilter", correlationFilter);
                await _adminClient.CreateRuleAsync(topicName, subscriptionName, ruleOptions);
                
                _output.WriteLine($"Created subscription {subscriptionName} with correlation filter: {correlationId}");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Error creating subscription {subscriptionName}: {ex.Message}");
            throw;
        }
    }

    private ServiceBusMessage CreateMessageWithPriority(string messageId, string priority)
    {
        var message = new ServiceBusMessage($"Message content: {messageId}")
        {
            MessageId = messageId,
            Subject = "TestMessage"
        };
        
        message.ApplicationProperties["Priority"] = priority;
        message.ApplicationProperties["Timestamp"] = DateTimeOffset.UtcNow.ToString("O");
        
        return message;
    }

    private ServiceBusMessage CreateMessageWithCategoryAndPrice(string messageId, string category, int price)
    {
        var message = new ServiceBusMessage($"Product: {messageId}")
        {
            MessageId = messageId,
            Subject = "Product"
        };
        
        message.ApplicationProperties["Category"] = category;
        message.ApplicationProperties["Price"] = price;
        message.ApplicationProperties["Timestamp"] = DateTimeOffset.UtcNow.ToString("O");
        
        return message;
    }

    private ServiceBusMessage CreateMessageWithCorrelationId(string messageId, string correlationId)
    {
        var message = new ServiceBusMessage($"Message: {messageId}")
        {
            MessageId = messageId,
            CorrelationId = correlationId,
            Subject = "CorrelatedMessage"
        };
        
        message.ApplicationProperties["Timestamp"] = DateTimeOffset.UtcNow.ToString("O");
        
        return message;
    }

    private ServiceBusMessage CreateComplexMessage(
        string messageId, 
        string category, 
        int price, 
        string inStock)
    {
        var message = new ServiceBusMessage($"Product: {messageId}")
        {
            MessageId = messageId,
            Subject = "Product"
        };
        
        message.ApplicationProperties["Category"] = category;
        message.ApplicationProperties["Price"] = price;
        message.ApplicationProperties["InStock"] = inStock;
        message.ApplicationProperties["Timestamp"] = DateTimeOffset.UtcNow.ToString("O");
        
        return message;
    }

    private ServiceBusMessage CreateMessageWithStatus(string messageId, string status)
    {
        var message = new ServiceBusMessage($"Order: {messageId}")
        {
            MessageId = messageId,
            Subject = "Order"
        };
        
        message.ApplicationProperties["Status"] = status;
        message.ApplicationProperties["Timestamp"] = DateTimeOffset.UtcNow.ToString("O");
        
        return message;
    }

    #endregion
}

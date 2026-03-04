using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Azure.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Azure.Tests.Integration;

/// <summary>
/// Property-based tests for Azure Service Bus subscription filtering using FsCheck.
/// Feature: azure-cloud-integration-testing
/// Task: 5.3 Write property test for Azure Service Bus subscription filtering
/// </summary>
public class ServiceBusSubscriptionFilteringPropertyTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;
    private IAzureTestEnvironment? _testEnvironment;
    private ServiceBusClient? _serviceBusClient;
    private ServiceBusTestHelpers? _testHelpers;
    private ServiceBusAdministrationClient? _adminClient;

    public ServiceBusSubscriptionFilteringPropertyTests(ITestOutputHelper output)
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
        var config = AzureTestConfiguration.CreateDefault();
        
        _testEnvironment = new AzureTestEnvironment(config, _loggerFactory);

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

    #region Property 4: Azure Service Bus Subscription Filtering Accuracy

    /// <summary>
    /// Property 4: Azure Service Bus Subscription Filtering Accuracy
    /// For any event published to an Azure Service Bus topic with subscription filters,
    /// the event should be delivered only to subscriptions whose filter criteria match the event properties.
    /// Validates: Requirements 2.2
    /// </summary>
    [Property(MaxTest = 10, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property Property4_SubscriptionFilteringAccuracy_DeliversOnlyToMatchingSubscriptions()
    {
        return Prop.ForAll(
            AzureResourceGenerators.GenerateFilteredMessageBatch().ToArbitrary(),
            (FilteredMessageBatch batch) =>
            {
                try
                {
                    var topicName = $"filter-prop-topic-{Guid.NewGuid():N}".Substring(0, 50);
                    var highPrioritySub = "high-priority";
                    var lowPrioritySub = "low-priority";

                    // Setup topic and filtered subscriptions
                    CreateTopicAsync(topicName).GetAwaiter().GetResult();
                    CreateSubscriptionWithSqlFilterAsync(topicName, highPrioritySub, "Priority = 'High'").GetAwaiter().GetResult();
                    CreateSubscriptionWithSqlFilterAsync(topicName, lowPrioritySub, "Priority = 'Low'").GetAwaiter().GetResult();

                    // Send all messages
                    foreach (var message in batch.Messages)
                    {
                        _testHelpers!.SendMessageToTopicAsync(topicName, message).GetAwaiter().GetResult();
                    }

                    // Wait for message processing
                    Task.Delay(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();

                    // Receive from high priority subscription
                    var highPriorityReceived = _testHelpers!.ReceiveMessagesFromSubscriptionAsync(
                        topicName, highPrioritySub, batch.HighPriorityCount, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();

                    // Receive from low priority subscription
                    var lowPriorityReceived = _testHelpers.ReceiveMessagesFromSubscriptionAsync(
                        topicName, lowPrioritySub, batch.LowPriorityCount, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();

                    // Cleanup
                    CleanupTopicAsync(topicName).GetAwaiter().GetResult();

                    // Property: High priority subscription receives only high priority messages
                    var highPriorityCorrect = highPriorityReceived.All(msg => 
                        msg.ApplicationProperties.ContainsKey("Priority") && 
                        msg.ApplicationProperties["Priority"].ToString() == "High");

                    // Property: Low priority subscription receives only low priority messages
                    var lowPriorityCorrect = lowPriorityReceived.All(msg => 
                        msg.ApplicationProperties.ContainsKey("Priority") && 
                        msg.ApplicationProperties["Priority"].ToString() == "Low");

                    // Property: Count matches expected
                    var countCorrect = 
                        highPriorityReceived.Count == batch.HighPriorityCount &&
                        lowPriorityReceived.Count == batch.LowPriorityCount;

                    return (highPriorityCorrect && lowPriorityCorrect && countCorrect).ToProperty();
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Property test failed: {ex.Message}");
                    return false.ToProperty();
                }
            });
    }

    /// <summary>
    /// Property 4 Variant: SQL filter expressions evaluate correctly for numeric comparisons
    /// Validates: Requirements 2.2
    /// </summary>
    [Property(MaxTest = 10, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property Property4_SqlFilterNumericComparison_EvaluatesCorrectly()
    {
        return Prop.ForAll(
            AzureResourceGenerators.GenerateNumericFilteredMessages().ToArbitrary(),
            (NumericFilteredMessageBatch batch) =>
            {
                try
                {
                    var topicName = $"numeric-filter-topic-{Guid.NewGuid():N}".Substring(0, 50);
                    var highValueSub = "high-value";
                    var threshold = batch.Threshold;

                    // Setup topic and subscription with numeric filter
                    CreateTopicAsync(topicName).GetAwaiter().GetResult();
                    CreateSubscriptionWithSqlFilterAsync(
                        topicName, highValueSub, $"Value > {threshold}").GetAwaiter().GetResult();

                    // Send all messages
                    foreach (var message in batch.Messages)
                    {
                        _testHelpers!.SendMessageToTopicAsync(topicName, message).GetAwaiter().GetResult();
                    }

                    Task.Delay(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();

                    // Receive messages
                    var received = _testHelpers!.ReceiveMessagesFromSubscriptionAsync(
                        topicName, highValueSub, batch.ExpectedCount, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();

                    // Cleanup
                    CleanupTopicAsync(topicName).GetAwaiter().GetResult();

                    // Property: All received messages have Value > threshold
                    var allAboveThreshold = received.All(msg =>
                    {
                        if (msg.ApplicationProperties.TryGetValue("Value", out var value))
                        {
                            return Convert.ToInt32(value) > threshold;
                        }
                        return false;
                    });

                    // Property: Count matches expected
                    var countCorrect = received.Count == batch.ExpectedCount;

                    return (allAboveThreshold && countCorrect).ToProperty();
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Property test failed: {ex.Message}");
                    return false.ToProperty();
                }
            });
    }

    #endregion

    #region Property 5: Azure Service Bus Fan-Out Completeness

    /// <summary>
    /// Property 5: Azure Service Bus Fan-Out Completeness
    /// For any event published to an Azure Service Bus topic with multiple subscriptions,
    /// the event should be delivered to all active subscriptions.
    /// Validates: Requirements 2.4
    /// </summary>
    [Property(MaxTest = 10, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property Property5_FanOutCompleteness_DeliversToAllSubscriptions()
    {
        return Prop.ForAll(
            AzureResourceGenerators.GenerateFanOutScenario().ToArbitrary(),
            (FanOutScenario scenario) =>
            {
                try
                {
                    var topicName = $"fanout-topic-{Guid.NewGuid():N}".Substring(0, 50);

                    // Setup topic and multiple subscriptions
                    CreateTopicAsync(topicName).GetAwaiter().GetResult();
                    
                    foreach (var subName in scenario.SubscriptionNames)
                    {
                        CreateSubscriptionWithNoFilterAsync(topicName, subName).GetAwaiter().GetResult();
                    }

                    // Send messages
                    foreach (var message in scenario.Messages)
                    {
                        _testHelpers!.SendMessageToTopicAsync(topicName, message).GetAwaiter().GetResult();
                    }

                    Task.Delay(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();

                    // Receive from all subscriptions
                    var receivedPerSubscription = new Dictionary<string, List<ServiceBusReceivedMessage>>();
                    
                    foreach (var subName in scenario.SubscriptionNames)
                    {
                        var received = _testHelpers!.ReceiveMessagesFromSubscriptionAsync(
                            topicName, subName, scenario.Messages.Count, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                        receivedPerSubscription[subName] = received;
                    }

                    // Cleanup
                    CleanupTopicAsync(topicName).GetAwaiter().GetResult();

                    // Property: Each subscription received all messages
                    var allSubscriptionsReceivedAll = receivedPerSubscription.All(kvp => 
                        kvp.Value.Count == scenario.Messages.Count);

                    // Property: Each subscription received the same message IDs
                    var sentMessageIds = scenario.Messages.Select(m => m.MessageId).OrderBy(id => id).ToList();
                    var allHaveSameMessages = receivedPerSubscription.All(kvp =>
                    {
                        var receivedIds = kvp.Value.Select(m => m.MessageId).OrderBy(id => id).ToList();
                        return sentMessageIds.SequenceEqual(receivedIds);
                    });

                    return (allSubscriptionsReceivedAll && allHaveSameMessages).ToProperty();
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Property test failed: {ex.Message}");
                    return false.ToProperty();
                }
            });
    }

    /// <summary>
    /// Property 5 Variant: Fan-out preserves message properties across all subscriptions
    /// Validates: Requirements 2.4
    /// </summary>
    [Property(MaxTest = 10, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property Property5_FanOutPreservesProperties_AcrossAllSubscriptions()
    {
        return Prop.ForAll(
            AzureResourceGenerators.GenerateFanOutScenario().ToArbitrary(),
            (FanOutScenario scenario) =>
            {
                try
                {
                    var topicName = $"fanout-props-topic-{Guid.NewGuid():N}".Substring(0, 50);

                    // Setup topic and subscriptions
                    CreateTopicAsync(topicName).GetAwaiter().GetResult();
                    
                    foreach (var subName in scenario.SubscriptionNames)
                    {
                        CreateSubscriptionWithNoFilterAsync(topicName, subName).GetAwaiter().GetResult();
                    }

                    // Send messages with custom properties
                    foreach (var message in scenario.Messages)
                    {
                        message.ApplicationProperties["CustomProperty"] = $"Value-{message.MessageId}";
                        message.ApplicationProperties["Timestamp"] = DateTimeOffset.UtcNow.ToString("O");
                        _testHelpers!.SendMessageToTopicAsync(topicName, message).GetAwaiter().GetResult();
                    }

                    Task.Delay(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();

                    // Receive from all subscriptions
                    var receivedPerSubscription = new Dictionary<string, List<ServiceBusReceivedMessage>>();
                    
                    foreach (var subName in scenario.SubscriptionNames)
                    {
                        var received = _testHelpers!.ReceiveMessagesFromSubscriptionAsync(
                            topicName, subName, scenario.Messages.Count, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                        receivedPerSubscription[subName] = received;
                    }

                    // Cleanup
                    CleanupTopicAsync(topicName).GetAwaiter().GetResult();

                    // Property: All subscriptions received messages with correct properties
                    var propertiesPreserved = receivedPerSubscription.All(kvp =>
                    {
                        return kvp.Value.All(msg =>
                        {
                            var hasCustomProperty = msg.ApplicationProperties.ContainsKey("CustomProperty");
                            var hasTimestamp = msg.ApplicationProperties.ContainsKey("Timestamp");
                            var customValueCorrect = msg.ApplicationProperties["CustomProperty"].ToString() == 
                                $"Value-{msg.MessageId}";
                            
                            return hasCustomProperty && hasTimestamp && customValueCorrect;
                        });
                    });

                    return propertiesPreserved.ToProperty();
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Property test failed: {ex.Message}");
                    return false.ToProperty();
                }
            });
    }

    #endregion

    #region Helper Methods

    private async Task CreateTopicAsync(string topicName)
    {
        try
        {
            if (!await _adminClient!.TopicExistsAsync(topicName))
            {
                var topicOptions = new CreateTopicOptions(topicName)
                {
                    DefaultMessageTimeToLive = TimeSpan.FromHours(1),
                    EnableBatchedOperations = true
                };

                await _adminClient.CreateTopicAsync(topicOptions);
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Error creating topic {topicName}: {ex.Message}");
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
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Error creating subscription {subscriptionName}: {ex.Message}");
        }
    }

    private async Task CreateSubscriptionWithNoFilterAsync(string topicName, string subscriptionName)
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
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Error creating subscription {subscriptionName}: {ex.Message}");
        }
    }

    private async Task CleanupTopicAsync(string topicName)
    {
        try
        {
            if (await _adminClient!.TopicExistsAsync(topicName))
            {
                await _adminClient.DeleteTopicAsync(topicName);
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Error cleaning up topic {topicName}: {ex.Message}");
        }
    }

    #endregion
}

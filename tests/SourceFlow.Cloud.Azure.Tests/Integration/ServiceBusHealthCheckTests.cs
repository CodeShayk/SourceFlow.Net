using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Azure.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Azure.Tests.Integration;

/// <summary>
/// Integration tests for Azure Service Bus health checks.
/// Validates Service Bus namespace connectivity, queue/topic existence, and permission validation.
/// **Validates: Requirements 4.1**
/// </summary>
public class ServiceBusHealthCheckTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILogger<ServiceBusHealthCheckTests> _logger;
    private IAzureTestEnvironment _testEnvironment = null!;
    private ServiceBusClient _serviceBusClient = null!;
    private ServiceBusAdministrationClient _adminClient = null!;
    private string _testQueueName = null!;
    private string _testTopicName = null!;

    public ServiceBusHealthCheckTests(ITestOutputHelper output)
    {
        _output = output;
        _logger = LoggerHelper.CreateLogger<ServiceBusHealthCheckTests>(output);
    }

    public async Task InitializeAsync()
    {
        var config = new AzureTestConfiguration
        {
            UseAzurite = true
        };

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        _testEnvironment = new AzureTestEnvironment(config, loggerFactory);
        await _testEnvironment.InitializeAsync();

        _serviceBusClient = _testEnvironment.CreateServiceBusClient();
        _adminClient = _testEnvironment.CreateServiceBusAdministrationClient();

        _testQueueName = $"health-check-queue-{Guid.NewGuid():N}";
        _testTopicName = $"health-check-topic-{Guid.NewGuid():N}";

        // Create test resources
        await _adminClient.CreateQueueAsync(_testQueueName);
        await _adminClient.CreateTopicAsync(_testTopicName);

        _logger.LogInformation("Test environment initialized with queue: {QueueName}, topic: {TopicName}",
            _testQueueName, _testTopicName);
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_adminClient != null)
            {
                await _adminClient.DeleteQueueAsync(_testQueueName);
                await _adminClient.DeleteTopicAsync(_testTopicName);
            }

            await _serviceBusClient.DisposeAsync();
            await _testEnvironment.CleanupAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during test cleanup");
        }
    }

    [Fact]
    public async Task ServiceBusNamespaceConnectivity_ShouldSucceed()
    {
        // Arrange
        _logger.LogInformation("Testing Service Bus namespace connectivity");

        // Act
        var isAvailable = await _testEnvironment.IsServiceBusAvailableAsync();

        // Assert
        Assert.True(isAvailable, "Service Bus namespace should be accessible");
        _logger.LogInformation("Service Bus namespace connectivity validated successfully");
    }

    [Fact]
    public async Task QueueExistence_WhenQueueExists_ShouldReturnTrue()
    {
        // Arrange
        _logger.LogInformation("Testing queue existence check for existing queue: {QueueName}", _testQueueName);

        // Act
        var exists = await _adminClient.QueueExistsAsync(_testQueueName);

        // Assert
        Assert.True(exists.Value, $"Queue {_testQueueName} should exist");
        _logger.LogInformation("Queue existence validated successfully");
    }

    [Fact]
    public async Task QueueExistence_WhenQueueDoesNotExist_ShouldReturnFalse()
    {
        // Arrange
        var nonExistentQueue = $"non-existent-queue-{Guid.NewGuid():N}";
        _logger.LogInformation("Testing queue existence check for non-existent queue: {QueueName}", nonExistentQueue);

        // Act
        var exists = await _adminClient.QueueExistsAsync(nonExistentQueue);

        // Assert
        Assert.False(exists.Value, $"Queue {nonExistentQueue} should not exist");
        _logger.LogInformation("Non-existent queue check validated successfully");
    }

    [Fact]
    public async Task TopicExistence_WhenTopicExists_ShouldReturnTrue()
    {
        // Arrange
        _logger.LogInformation("Testing topic existence check for existing topic: {TopicName}", _testTopicName);

        // Act
        var exists = await _adminClient.TopicExistsAsync(_testTopicName);

        // Assert
        Assert.True(exists.Value, $"Topic {_testTopicName} should exist");
        _logger.LogInformation("Topic existence validated successfully");
    }

    [Fact]
    public async Task TopicExistence_WhenTopicDoesNotExist_ShouldReturnFalse()
    {
        // Arrange
        var nonExistentTopic = $"non-existent-topic-{Guid.NewGuid():N}";
        _logger.LogInformation("Testing topic existence check for non-existent topic: {TopicName}", nonExistentTopic);

        // Act
        var exists = await _adminClient.TopicExistsAsync(nonExistentTopic);

        // Assert
        Assert.False(exists.Value, $"Topic {nonExistentTopic} should not exist");
        _logger.LogInformation("Non-existent topic check validated successfully");
    }

    [Fact]
    public async Task ServiceBusPermissions_SendPermission_ShouldSucceed()
    {
        // Arrange
        _logger.LogInformation("Testing Service Bus send permission on queue: {QueueName}", _testQueueName);
        var sender = _serviceBusClient.CreateSender(_testQueueName);

        // Act & Assert
        var testMessage = new ServiceBusMessage("Health check test message")
        {
            MessageId = Guid.NewGuid().ToString()
        };

        await sender.SendMessageAsync(testMessage);
        _logger.LogInformation("Send permission validated successfully");

        await sender.DisposeAsync();
    }

    [Fact]
    public async Task ServiceBusPermissions_ReceivePermission_ShouldSucceed()
    {
        // Arrange
        _logger.LogInformation("Testing Service Bus receive permission on queue: {QueueName}", _testQueueName);
        var sender = _serviceBusClient.CreateSender(_testQueueName);
        var receiver = _serviceBusClient.CreateReceiver(_testQueueName);

        // Send a test message first
        var testMessage = new ServiceBusMessage("Health check receive test")
        {
            MessageId = Guid.NewGuid().ToString()
        };
        await sender.SendMessageAsync(testMessage);

        // Act
        var receivedMessage = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.NotNull(receivedMessage);
        await receiver.CompleteMessageAsync(receivedMessage);
        _logger.LogInformation("Receive permission validated successfully");

        await sender.DisposeAsync();
        await receiver.DisposeAsync();
    }

    [Fact]
    public async Task ServiceBusPermissions_ManagePermission_ShouldSucceed()
    {
        // Arrange
        var tempQueueName = $"temp-health-check-{Guid.NewGuid():N}";
        _logger.LogInformation("Testing Service Bus manage permission by creating queue: {QueueName}", tempQueueName);

        // Act & Assert - Create queue
        var createResponse = await _adminClient.CreateQueueAsync(tempQueueName);
        Assert.NotNull(createResponse.Value);
        _logger.LogInformation("Queue created successfully, validating manage permission");

        // Verify queue exists
        var exists = await _adminClient.QueueExistsAsync(tempQueueName);
        Assert.True(exists.Value);

        // Cleanup
        await _adminClient.DeleteQueueAsync(tempQueueName);
        _logger.LogInformation("Manage permission validated successfully");
    }

    [Fact]
    public async Task ServiceBusHealthCheck_GetQueueProperties_ShouldReturnValidMetrics()
    {
        // Arrange
        _logger.LogInformation("Testing Service Bus health check by retrieving queue properties");

        // Act
        var queueProperties = await _adminClient.GetQueueRuntimePropertiesAsync(_testQueueName);

        // Assert
        Assert.NotNull(queueProperties.Value);
        Assert.Equal(_testQueueName, queueProperties.Value.Name);
        Assert.True(queueProperties.Value.ActiveMessageCount >= 0);
        Assert.True(queueProperties.Value.DeadLetterMessageCount >= 0);

        _logger.LogInformation("Queue properties retrieved: ActiveMessages={Active}, DeadLetterMessages={DeadLetter}",
            queueProperties.Value.ActiveMessageCount,
            queueProperties.Value.DeadLetterMessageCount);
    }

    [Fact]
    public async Task ServiceBusHealthCheck_GetTopicProperties_ShouldReturnValidMetrics()
    {
        // Arrange
        _logger.LogInformation("Testing Service Bus health check by retrieving topic properties");

        // Act
        var topicProperties = await _adminClient.GetTopicRuntimePropertiesAsync(_testTopicName);

        // Assert
        Assert.NotNull(topicProperties.Value);
        Assert.Equal(_testTopicName, topicProperties.Value.Name);
        Assert.True(topicProperties.Value.SubscriptionCount >= 0);

        _logger.LogInformation("Topic properties retrieved: SubscriptionCount={Count}",
            topicProperties.Value.SubscriptionCount);
    }

    [Fact]
    public async Task ServiceBusHealthCheck_ListQueues_ShouldIncludeTestQueue()
    {
        // Arrange
        _logger.LogInformation("Testing Service Bus health check by listing queues");

        // Act
        var queues = new List<string>();
        await foreach (var queue in _adminClient.GetQueuesAsync())
        {
            queues.Add(queue.Name);
        }

        // Assert
        Assert.Contains(_testQueueName, queues);
        _logger.LogInformation("Found {Count} queues, including test queue", queues.Count);
    }

    [Fact]
    public async Task ServiceBusHealthCheck_ListTopics_ShouldIncludeTestTopic()
    {
        // Arrange
        _logger.LogInformation("Testing Service Bus health check by listing topics");

        // Act
        var topics = new List<string>();
        await foreach (var topic in _adminClient.GetTopicsAsync())
        {
            topics.Add(topic.Name);
        }

        // Assert
        Assert.Contains(_testTopicName, topics);
        _logger.LogInformation("Found {Count} topics, including test topic", topics.Count);
    }

    [Fact]
    public async Task ServiceBusHealthCheck_EndToEndMessageFlow_ShouldSucceed()
    {
        // Arrange
        _logger.LogInformation("Testing end-to-end Service Bus health check with message flow");
        var sender = _serviceBusClient.CreateSender(_testQueueName);
        var receiver = _serviceBusClient.CreateReceiver(_testQueueName);

        var testMessage = new ServiceBusMessage("End-to-end health check")
        {
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = Guid.NewGuid().ToString()
        };

        // Act - Send
        await sender.SendMessageAsync(testMessage);
        _logger.LogInformation("Message sent with ID: {MessageId}", testMessage.MessageId);

        // Act - Receive
        var receivedMessage = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.NotNull(receivedMessage);
        Assert.Equal(testMessage.MessageId, receivedMessage.MessageId);
        Assert.Equal(testMessage.CorrelationId, receivedMessage.CorrelationId);

        await receiver.CompleteMessageAsync(receivedMessage);
        _logger.LogInformation("End-to-end health check completed successfully");

        await sender.DisposeAsync();
        await receiver.DisposeAsync();
    }
}

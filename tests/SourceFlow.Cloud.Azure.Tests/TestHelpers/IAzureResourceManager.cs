namespace SourceFlow.Cloud.Azure.Tests.TestHelpers;

/// <summary>
/// Interface for Azure resource management in test environments.
/// Provides abstraction for creating, deleting, and managing Azure resources during testing.
/// Supports Service Bus queues, topics, subscriptions, and Key Vault keys.
/// </summary>
public interface IAzureResourceManager
{
    /// <summary>
    /// Creates a Service Bus queue with the specified configuration.
    /// </summary>
    /// <param name="queueName">Name of the queue to create.</param>
    /// <param name="options">Queue configuration options.</param>
    /// <returns>Resource ID of the created queue.</returns>
    Task<string> CreateServiceBusQueueAsync(string queueName, ServiceBusQueueOptions options);

    /// <summary>
    /// Creates a Service Bus topic with the specified configuration.
    /// </summary>
    /// <param name="topicName">Name of the topic to create.</param>
    /// <param name="options">Topic configuration options.</param>
    /// <returns>Resource ID of the created topic.</returns>
    Task<string> CreateServiceBusTopicAsync(string topicName, ServiceBusTopicOptions options);

    /// <summary>
    /// Creates a Service Bus subscription for a topic with the specified configuration.
    /// </summary>
    /// <param name="topicName">Name of the parent topic.</param>
    /// <param name="subscriptionName">Name of the subscription to create.</param>
    /// <param name="options">Subscription configuration options.</param>
    /// <returns>Resource ID of the created subscription.</returns>
    Task<string> CreateServiceBusSubscriptionAsync(string topicName, string subscriptionName, ServiceBusSubscriptionOptions options);

    /// <summary>
    /// Deletes an Azure resource by its resource ID.
    /// </summary>
    /// <param name="resourceId">Resource ID to delete.</param>
    Task DeleteResourceAsync(string resourceId);

    /// <summary>
    /// Lists all resources managed by this resource manager.
    /// </summary>
    /// <returns>Collection of resource IDs.</returns>
    Task<IEnumerable<string>> ListResourcesAsync();

    /// <summary>
    /// Creates a Key Vault key with the specified configuration.
    /// </summary>
    /// <param name="keyName">Name of the key to create.</param>
    /// <param name="options">Key configuration options.</param>
    /// <returns>Resource ID of the created key.</returns>
    Task<string> CreateKeyVaultKeyAsync(string keyName, KeyVaultKeyOptions options);

    /// <summary>
    /// Validates that a resource exists.
    /// </summary>
    /// <param name="resourceId">Resource ID to validate.</param>
    /// <returns>True if the resource exists, false otherwise.</returns>
    Task<bool> ValidateResourceExistsAsync(string resourceId);

    /// <summary>
    /// Gets the tags associated with a resource.
    /// </summary>
    /// <param name="resourceId">Resource ID to query.</param>
    /// <returns>Dictionary of tag key-value pairs.</returns>
    Task<Dictionary<string, string>> GetResourceTagsAsync(string resourceId);

    /// <summary>
    /// Sets tags on a resource.
    /// </summary>
    /// <param name="resourceId">Resource ID to tag.</param>
    /// <param name="tags">Dictionary of tag key-value pairs to set.</param>
    Task SetResourceTagsAsync(string resourceId, Dictionary<string, string> tags);
}

/// <summary>
/// Configuration options for Service Bus queue creation.
/// </summary>
public class ServiceBusQueueOptions
{
    /// <summary>
    /// Indicates whether the queue requires sessions for ordered message processing.
    /// </summary>
    public bool RequiresSession { get; set; }

    /// <summary>
    /// Maximum number of delivery attempts before moving message to dead letter queue.
    /// </summary>
    public int MaxDeliveryCount { get; set; } = 10;

    /// <summary>
    /// Duration for which a message is locked for processing.
    /// </summary>
    public TimeSpan LockDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Time-to-live for messages in the queue.
    /// </summary>
    public TimeSpan DefaultMessageTimeToLive { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Enables dead lettering when messages expire.
    /// </summary>
    public bool EnableDeadLetteringOnMessageExpiration { get; set; } = true;

    /// <summary>
    /// Enables batched operations for improved throughput.
    /// </summary>
    public bool EnableBatchedOperations { get; set; } = true;

    /// <summary>
    /// Enables duplicate detection based on message ID.
    /// </summary>
    public bool EnableDuplicateDetection { get; set; }

    /// <summary>
    /// Duration of the duplicate detection history window.
    /// </summary>
    public TimeSpan DuplicateDetectionHistoryTimeWindow { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Configuration options for Service Bus topic creation.
/// </summary>
public class ServiceBusTopicOptions
{
    /// <summary>
    /// Time-to-live for messages in the topic.
    /// </summary>
    public TimeSpan DefaultMessageTimeToLive { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Enables batched operations for improved throughput.
    /// </summary>
    public bool EnableBatchedOperations { get; set; } = true;

    /// <summary>
    /// Maximum size of the topic in megabytes.
    /// </summary>
    public int MaxSizeInMegabytes { get; set; } = 1024;

    /// <summary>
    /// Enables duplicate detection based on message ID.
    /// </summary>
    public bool EnableDuplicateDetection { get; set; }

    /// <summary>
    /// Duration of the duplicate detection history window.
    /// </summary>
    public TimeSpan DuplicateDetectionHistoryTimeWindow { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Configuration options for Service Bus subscription creation.
/// </summary>
public class ServiceBusSubscriptionOptions
{
    /// <summary>
    /// Maximum number of delivery attempts before moving message to dead letter queue.
    /// </summary>
    public int MaxDeliveryCount { get; set; } = 10;

    /// <summary>
    /// Duration for which a message is locked for processing.
    /// </summary>
    public TimeSpan LockDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Enables dead lettering when messages expire.
    /// </summary>
    public bool EnableDeadLetteringOnMessageExpiration { get; set; } = true;

    /// <summary>
    /// Enables batched operations for improved throughput.
    /// </summary>
    public bool EnableBatchedOperations { get; set; } = true;

    /// <summary>
    /// Queue name to forward messages to (optional).
    /// </summary>
    public string? ForwardTo { get; set; }

    /// <summary>
    /// SQL filter expression for subscription filtering (optional).
    /// </summary>
    public string? FilterExpression { get; set; }
}

/// <summary>
/// Configuration options for Key Vault key creation.
/// </summary>
public class KeyVaultKeyOptions
{
    /// <summary>
    /// Size of the RSA key in bits.
    /// </summary>
    public int KeySize { get; set; } = 2048;

    /// <summary>
    /// Expiration date for the key (optional).
    /// </summary>
    public DateTimeOffset? ExpiresOn { get; set; }

    /// <summary>
    /// Indicates whether the key is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Tags to associate with the key.
    /// </summary>
    public Dictionary<string, string> Tags { get; set; } = new();
}

using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus.Administration;
using Azure.Security.KeyVault.Keys;
using Microsoft.Extensions.Logging;

namespace SourceFlow.Cloud.Azure.Tests.TestHelpers;

/// <summary>
/// Azure resource manager for creating and managing test resources.
/// Supports Service Bus queues, topics, subscriptions, and Key Vault keys.
/// Provides automatic resource tracking and cleanup.
/// </summary>
public class AzureResourceManager : IAzureResourceManager, IAsyncDisposable
{
    private readonly AzureTestConfiguration _configuration;
    private readonly TokenCredential _credential;
    private readonly ILogger<AzureResourceManager> _logger;
    private readonly ServiceBusAdministrationClient _serviceBusAdminClient;
    private readonly KeyClient? _keyClient;
    private readonly HashSet<string> _createdResources = new();
    private readonly SemaphoreSlim _resourceLock = new(1, 1);

    public AzureResourceManager(
        AzureTestConfiguration configuration,
        TokenCredential credential,
        ILogger<AzureResourceManager> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _serviceBusAdminClient = new ServiceBusAdministrationClient(
            _configuration.FullyQualifiedNamespace,
            _credential);

        if (!string.IsNullOrEmpty(_configuration.KeyVaultUrl))
        {
            _keyClient = new KeyClient(new Uri(_configuration.KeyVaultUrl), _credential);
        }
    }

    public async Task<string> CreateServiceBusQueueAsync(string queueName, ServiceBusQueueOptions options)
    {
        _logger.LogInformation("Creating Service Bus queue: {QueueName}", queueName);

        try
        {
            var createOptions = new CreateQueueOptions(queueName)
            {
                RequiresSession = options.RequiresSession,
                MaxDeliveryCount = options.MaxDeliveryCount,
                LockDuration = options.LockDuration,
                DefaultMessageTimeToLive = options.DefaultMessageTimeToLive,
                DeadLetteringOnMessageExpiration = options.EnableDeadLetteringOnMessageExpiration,
                EnableBatchedOperations = options.EnableBatchedOperations
            };

            if (options.EnableDuplicateDetection)
            {
                createOptions.RequiresDuplicateDetection = true;
                createOptions.DuplicateDetectionHistoryTimeWindow = options.DuplicateDetectionHistoryTimeWindow;
            }

            var queue = await _serviceBusAdminClient.CreateQueueAsync(createOptions);
            var resourceId = GenerateQueueResourceId(queueName);

            await TrackResourceAsync(resourceId);

            _logger.LogInformation("Created Service Bus queue: {QueueName} with resource ID: {ResourceId}",
                queueName, resourceId);

            return resourceId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Service Bus queue: {QueueName}", queueName);
            throw;
        }
    }

    public async Task<string> CreateServiceBusTopicAsync(string topicName, ServiceBusTopicOptions options)
    {
        _logger.LogInformation("Creating Service Bus topic: {TopicName}", topicName);

        try
        {
            var createOptions = new CreateTopicOptions(topicName)
            {
                DefaultMessageTimeToLive = options.DefaultMessageTimeToLive,
                EnableBatchedOperations = options.EnableBatchedOperations,
                MaxSizeInMegabytes = options.MaxSizeInMegabytes
            };

            if (options.EnableDuplicateDetection)
            {
                createOptions.RequiresDuplicateDetection = true;
                createOptions.DuplicateDetectionHistoryTimeWindow = options.DuplicateDetectionHistoryTimeWindow;
            }

            var topic = await _serviceBusAdminClient.CreateTopicAsync(createOptions);
            var resourceId = GenerateTopicResourceId(topicName);

            await TrackResourceAsync(resourceId);

            _logger.LogInformation("Created Service Bus topic: {TopicName} with resource ID: {ResourceId}",
                topicName, resourceId);

            return resourceId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Service Bus topic: {TopicName}", topicName);
            throw;
        }
    }

    public async Task<string> CreateServiceBusSubscriptionAsync(
        string topicName,
        string subscriptionName,
        ServiceBusSubscriptionOptions options)
    {
        _logger.LogInformation("Creating Service Bus subscription: {SubscriptionName} for topic: {TopicName}",
            subscriptionName, topicName);

        try
        {
            var createOptions = new CreateSubscriptionOptions(topicName, subscriptionName)
            {
                MaxDeliveryCount = options.MaxDeliveryCount,
                LockDuration = options.LockDuration,
                DeadLetteringOnMessageExpiration = options.EnableDeadLetteringOnMessageExpiration,
                EnableBatchedOperations = options.EnableBatchedOperations
            };

            if (!string.IsNullOrEmpty(options.ForwardTo))
            {
                createOptions.ForwardTo = options.ForwardTo;
            }

            var subscription = await _serviceBusAdminClient.CreateSubscriptionAsync(createOptions);

            // Add filter if specified
            if (!string.IsNullOrEmpty(options.FilterExpression))
            {
                var ruleOptions = new CreateRuleOptions("CustomFilter", new SqlRuleFilter(options.FilterExpression));
                await _serviceBusAdminClient.CreateRuleAsync(topicName, subscriptionName, ruleOptions);
            }

            var resourceId = GenerateSubscriptionResourceId(topicName, subscriptionName);

            await TrackResourceAsync(resourceId);

            _logger.LogInformation(
                "Created Service Bus subscription: {SubscriptionName} for topic: {TopicName} with resource ID: {ResourceId}",
                subscriptionName, topicName, resourceId);

            return resourceId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Service Bus subscription: {SubscriptionName} for topic: {TopicName}",
                subscriptionName, topicName);
            throw;
        }
    }

    public async Task DeleteResourceAsync(string resourceId)
    {
        _logger.LogInformation("Deleting resource: {ResourceId}", resourceId);

        try
        {
            var resourceType = GetResourceType(resourceId);

            switch (resourceType)
            {
                case "queue":
                    var queueName = ExtractResourceName(resourceId);
                    await _serviceBusAdminClient.DeleteQueueAsync(queueName);
                    break;

                case "topic":
                    var topicName = ExtractResourceName(resourceId);
                    await _serviceBusAdminClient.DeleteTopicAsync(topicName);
                    break;

                case "subscription":
                    var (topic, subscription) = ExtractSubscriptionNames(resourceId);
                    await _serviceBusAdminClient.DeleteSubscriptionAsync(topic, subscription);
                    break;

                case "key":
                    if (_keyClient != null)
                    {
                        var keyName = ExtractResourceName(resourceId);
                        var operation = await _keyClient.StartDeleteKeyAsync(keyName);
                        await operation.WaitForCompletionAsync();
                    }
                    break;

                default:
                    _logger.LogWarning("Unknown resource type for deletion: {ResourceId}", resourceId);
                    break;
            }

            await UntrackResourceAsync(resourceId);

            _logger.LogInformation("Deleted resource: {ResourceId}", resourceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete resource: {ResourceId}", resourceId);
            throw;
        }
    }

    public async Task<IEnumerable<string>> ListResourcesAsync()
    {
        await _resourceLock.WaitAsync();
        try
        {
            return _createdResources.ToList();
        }
        finally
        {
            _resourceLock.Release();
        }
    }

    public async Task<string> CreateKeyVaultKeyAsync(string keyName, KeyVaultKeyOptions options)
    {
        if (_keyClient == null)
        {
            throw new InvalidOperationException("Key Vault client is not configured");
        }

        _logger.LogInformation("Creating Key Vault key: {KeyName}", keyName);

        try
        {
            var createOptions = new CreateRsaKeyOptions(keyName)
            {
                KeySize = options.KeySize,
                ExpiresOn = options.ExpiresOn,
                Enabled = options.Enabled
            };

            foreach (var tag in options.Tags)
            {
                createOptions.Tags[tag.Key] = tag.Value;
            }

            var key = await _keyClient.CreateRsaKeyAsync(createOptions);
            var resourceId = GenerateKeyResourceId(keyName);

            await TrackResourceAsync(resourceId);

            _logger.LogInformation("Created Key Vault key: {KeyName} with resource ID: {ResourceId}",
                keyName, resourceId);

            return resourceId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Key Vault key: {KeyName}", keyName);
            throw;
        }
    }

    public async Task<bool> ValidateResourceExistsAsync(string resourceId)
    {
        try
        {
            var resourceType = GetResourceType(resourceId);

            switch (resourceType)
            {
                case "queue":
                    var queueName = ExtractResourceName(resourceId);
                    await _serviceBusAdminClient.GetQueueAsync(queueName);
                    return true;

                case "topic":
                    var topicName = ExtractResourceName(resourceId);
                    await _serviceBusAdminClient.GetTopicAsync(topicName);
                    return true;

                case "subscription":
                    var (topic, subscription) = ExtractSubscriptionNames(resourceId);
                    await _serviceBusAdminClient.GetSubscriptionAsync(topic, subscription);
                    return true;

                case "key":
                    if (_keyClient != null)
                    {
                        var keyName = ExtractResourceName(resourceId);
                        await _keyClient.GetKeyAsync(keyName);
                        return true;
                    }
                    return false;

                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    public async Task<Dictionary<string, string>> GetResourceTagsAsync(string resourceId)
    {
        var resourceType = GetResourceType(resourceId);

        if (resourceType == "key" && _keyClient != null)
        {
            var keyName = ExtractResourceName(resourceId);
            var key = await _keyClient.GetKeyAsync(keyName);
            return key.Value.Properties.Tags.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        // Service Bus resources don't support tags in the same way
        return new Dictionary<string, string>();
    }

    public async Task SetResourceTagsAsync(string resourceId, Dictionary<string, string> tags)
    {
        var resourceType = GetResourceType(resourceId);

        if (resourceType == "key" && _keyClient != null)
        {
            var keyName = ExtractResourceName(resourceId);
            var key = await _keyClient.GetKeyAsync(keyName);
            
            var properties = key.Value.Properties;
            properties.Tags.Clear();
            
            foreach (var tag in tags)
            {
                properties.Tags[tag.Key] = tag.Value;
            }

            await _keyClient.UpdateKeyPropertiesAsync(properties);
            _logger.LogInformation("Updated tags for key: {KeyName}", keyName);
        }
        else
        {
            _logger.LogWarning("Resource type {ResourceType} does not support tags", resourceType);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("Cleaning up all tracked resources");

        var resources = await ListResourcesAsync();
        foreach (var resourceId in resources)
        {
            try
            {
                await DeleteResourceAsync(resourceId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup resource during disposal: {ResourceId}", resourceId);
            }
        }

        _resourceLock.Dispose();
    }

    private async Task TrackResourceAsync(string resourceId)
    {
        await _resourceLock.WaitAsync();
        try
        {
            _createdResources.Add(resourceId);
        }
        finally
        {
            _resourceLock.Release();
        }
    }

    private async Task UntrackResourceAsync(string resourceId)
    {
        await _resourceLock.WaitAsync();
        try
        {
            _createdResources.Remove(resourceId);
        }
        finally
        {
            _resourceLock.Release();
        }
    }

    private string GenerateQueueResourceId(string queueName)
    {
        return $"/subscriptions/{_configuration.ResourceGroupName}/resourceGroups/{_configuration.ResourceGroupName}/" +
               $"providers/Microsoft.ServiceBus/namespaces/{_configuration.FullyQualifiedNamespace.Split('.')[0]}/queues/{queueName}";
    }

    private string GenerateTopicResourceId(string topicName)
    {
        return $"/subscriptions/{_configuration.ResourceGroupName}/resourceGroups/{_configuration.ResourceGroupName}/" +
               $"providers/Microsoft.ServiceBus/namespaces/{_configuration.FullyQualifiedNamespace.Split('.')[0]}/topics/{topicName}";
    }

    private string GenerateSubscriptionResourceId(string topicName, string subscriptionName)
    {
        return $"/subscriptions/{_configuration.ResourceGroupName}/resourceGroups/{_configuration.ResourceGroupName}/" +
               $"providers/Microsoft.ServiceBus/namespaces/{_configuration.FullyQualifiedNamespace.Split('.')[0]}/topics/{topicName}/subscriptions/{subscriptionName}";
    }

    private string GenerateKeyResourceId(string keyName)
    {
        var vaultName = new Uri(_configuration.KeyVaultUrl).Host.Split('.')[0];
        return $"/subscriptions/{_configuration.ResourceGroupName}/resourceGroups/{_configuration.ResourceGroupName}/" +
               $"providers/Microsoft.KeyVault/vaults/{vaultName}/keys/{keyName}";
    }

    private string GetResourceType(string resourceId)
    {
        if (resourceId.Contains("/queues/"))
            return "queue";
        if (resourceId.Contains("/topics/") && resourceId.Contains("/subscriptions/"))
            return "subscription";
        if (resourceId.Contains("/topics/"))
            return "topic";
        if (resourceId.Contains("/keys/"))
            return "key";
        
        return "unknown";
    }

    private string ExtractResourceName(string resourceId)
    {
        return resourceId.Split('/').Last();
    }

    private (string topic, string subscription) ExtractSubscriptionNames(string resourceId)
    {
        var parts = resourceId.Split('/');
        var topicIndex = Array.IndexOf(parts, "topics");
        var subscriptionIndex = Array.IndexOf(parts, "subscriptions");
        
        return (parts[topicIndex + 1], parts[subscriptionIndex + 1]);
    }
}

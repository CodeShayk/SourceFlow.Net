using Azure.Messaging.ServiceBus;
using Azure.Security.KeyVault.Keys;
using Azure.Identity;
using Azure;
using System.Net.Sockets;

namespace SourceFlow.Cloud.Azure.Tests.TestHelpers;

/// <summary>
/// Configuration for Azure test environments.
/// </summary>
public class AzureTestConfiguration
{
    /// <summary>
    /// Indicates whether to use Azurite emulator instead of real Azure services.
    /// </summary>
    public bool UseAzurite { get; set; } = true;

    /// <summary>
    /// Service Bus connection string (for connection string authentication).
    /// </summary>
    public string ServiceBusConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Service Bus fully qualified namespace (e.g., "myservicebus.servicebus.windows.net").
    /// </summary>
    public string FullyQualifiedNamespace { get; set; } = string.Empty;

    /// <summary>
    /// Key Vault URL (e.g., "https://mykeyvault.vault.azure.net/").
    /// </summary>
    public string KeyVaultUrl { get; set; } = string.Empty;

    /// <summary>
    /// Indicates whether to use managed identity for authentication.
    /// </summary>
    public bool UseManagedIdentity { get; set; }

    /// <summary>
    /// Client ID for user-assigned managed identity (optional).
    /// </summary>
    public string UserAssignedIdentityClientId { get; set; } = string.Empty;

    /// <summary>
    /// Azure region for resource provisioning.
    /// </summary>
    public string AzureRegion { get; set; } = "eastus";

    /// <summary>
    /// Resource group name for test resources.
    /// </summary>
    public string ResourceGroupName { get; set; } = "sourceflow-tests";

    /// <summary>
    /// Queue names for testing.
    /// </summary>
    public Dictionary<string, string> QueueNames { get; set; } = new();

    /// <summary>
    /// Topic names for testing.
    /// </summary>
    public Dictionary<string, string> TopicNames { get; set; } = new();

    /// <summary>
    /// Subscription names for testing.
    /// </summary>
    public Dictionary<string, string> SubscriptionNames { get; set; } = new();

    /// <summary>
    /// Performance test configuration.
    /// </summary>
    public AzurePerformanceTestConfiguration Performance { get; set; } = new();

    /// <summary>
    /// Security test configuration.
    /// </summary>
    public AzureSecurityTestConfiguration Security { get; set; } = new();

    /// <summary>
    /// Resilience test configuration.
    /// </summary>
    public AzureResilienceTestConfiguration Resilience { get; set; } = new();

    /// <summary>
    /// Creates a default configuration for testing.
    /// Reads from environment variables if available, otherwise uses Azurite defaults.
    /// </summary>
    public static AzureTestConfiguration CreateDefault()
    {
        var config = new AzureTestConfiguration();

        // Check for Azure connection strings in environment variables
        var serviceBusConnectionString = Environment.GetEnvironmentVariable("AZURE_SERVICEBUS_CONNECTION_STRING");
        var keyVaultUrl = Environment.GetEnvironmentVariable("AZURE_KEYVAULT_URL");
        var fullyQualifiedNamespace = Environment.GetEnvironmentVariable("AZURE_SERVICEBUS_NAMESPACE");

        if (!string.IsNullOrEmpty(serviceBusConnectionString))
        {
            config.UseAzurite = false;
            config.ServiceBusConnectionString = serviceBusConnectionString;
        }

        if (!string.IsNullOrEmpty(fullyQualifiedNamespace))
        {
            config.UseAzurite = false;
            config.FullyQualifiedNamespace = fullyQualifiedNamespace;
            config.UseManagedIdentity = true;
        }

        if (!string.IsNullOrEmpty(keyVaultUrl))
        {
            config.KeyVaultUrl = keyVaultUrl;
        }

        return config;
    }

    /// <summary>
    /// Checks if Azure Service Bus is available with a timeout.
    /// </summary>
    /// <param name="timeout">Maximum time to wait for connection.</param>
    /// <returns>True if Service Bus is available, false otherwise.</returns>
    public async Task<bool> IsServiceBusAvailableAsync(TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            
            ServiceBusClient client;
            if (!string.IsNullOrEmpty(ServiceBusConnectionString))
            {
                client = new ServiceBusClient(ServiceBusConnectionString);
            }
            else if (!string.IsNullOrEmpty(FullyQualifiedNamespace))
            {
                client = new ServiceBusClient(FullyQualifiedNamespace, new DefaultAzureCredential());
            }
            else if (UseAzurite)
            {
                // Azurite default endpoint
                client = new ServiceBusClient("Endpoint=sb://localhost:8080;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=test");
            }
            else
            {
                return false;
            }

            await using (client)
            {
                // Try to create a sender to test connectivity
                var sender = client.CreateSender("test-availability-check");
                await using (sender)
                {
                    // Just creating the sender doesn't test connectivity
                    // We need to attempt an operation, but we'll catch the exception
                    // if the queue doesn't exist (which is fine for availability check)
                    try
                    {
                        await sender.SendMessageAsync(new ServiceBusMessage("ping"), cts.Token);
                    }
                    catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
                    {
                        // Queue doesn't exist, but we connected successfully
                        return true;
                    }
                    
                    return true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout occurred
            return false;
        }
        catch (SocketException)
        {
            // Connection refused
            return false;
        }
        catch (Exception)
        {
            // Other connection errors
            return false;
        }
    }

    /// <summary>
    /// Checks if Azure Key Vault is available with a timeout.
    /// </summary>
    /// <param name="timeout">Maximum time to wait for connection.</param>
    /// <returns>True if Key Vault is available, false otherwise.</returns>
    public async Task<bool> IsKeyVaultAvailableAsync(TimeSpan timeout)
    {
        if (string.IsNullOrEmpty(KeyVaultUrl))
        {
            return false;
        }

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            
            var client = new KeyClient(new Uri(KeyVaultUrl), new DefaultAzureCredential());
            
            // Try to list keys to test connectivity
            await foreach (var keyProperties in client.GetPropertiesOfKeysAsync(cts.Token))
            {
                // If we can enumerate at least one key (or get an empty list), we're connected
                break;
            }
            
            return true;
        }
        catch (OperationCanceledException)
        {
            // Timeout occurred
            return false;
        }
        catch (SocketException)
        {
            // Connection refused
            return false;
        }
        catch (RequestFailedException ex) when (ex.Status == 401 || ex.Status == 403)
        {
            // Authentication/authorization error, but we connected
            return true;
        }
        catch (Exception)
        {
            // Other connection errors
            return false;
        }
    }

    /// <summary>
    /// Checks if Azurite emulator is available with a timeout.
    /// </summary>
    /// <param name="timeout">Maximum time to wait for connection.</param>
    /// <returns>True if Azurite is available, false otherwise.</returns>
    public async Task<bool> IsAzuriteAvailableAsync(TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            
            // Try to connect to Azurite Service Bus endpoint
            var client = new ServiceBusClient("Endpoint=sb://localhost:8080;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=test");
            
            await using (client)
            {
                var sender = client.CreateSender("test-availability-check");
                await using (sender)
                {
                    try
                    {
                        await sender.SendMessageAsync(new ServiceBusMessage("ping"), cts.Token);
                    }
                    catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
                    {
                        // Queue doesn't exist, but we connected successfully
                        return true;
                    }
                    
                    return true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout occurred
            return false;
        }
        catch (SocketException)
        {
            // Connection refused - Azurite not running
            return false;
        }
        catch (Exception)
        {
            // Other connection errors
            return false;
        }
    }
}

/// <summary>
/// Performance test configuration.
/// </summary>
public class AzurePerformanceTestConfiguration
{
    /// <summary>
    /// Maximum number of concurrent senders.
    /// </summary>
    public int MaxConcurrentSenders { get; set; } = 100;

    /// <summary>
    /// Maximum number of concurrent receivers.
    /// </summary>
    public int MaxConcurrentReceivers { get; set; } = 50;

    /// <summary>
    /// Test duration.
    /// </summary>
    public TimeSpan TestDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Number of warmup messages before actual test.
    /// </summary>
    public int WarmupMessages { get; set; } = 100;

    /// <summary>
    /// Enables auto-scaling tests.
    /// </summary>
    public bool EnableAutoScalingTests { get; set; } = true;

    /// <summary>
    /// Enables latency tests.
    /// </summary>
    public bool EnableLatencyTests { get; set; } = true;

    /// <summary>
    /// Enables throughput tests.
    /// </summary>
    public bool EnableThroughputTests { get; set; } = true;

    /// <summary>
    /// Enables resource utilization tests.
    /// </summary>
    public bool EnableResourceUtilizationTests { get; set; } = true;

    /// <summary>
    /// Message sizes to test (in bytes).
    /// </summary>
    public List<int> MessageSizes { get; set; } = new() { 1024, 10240, 102400 }; // 1KB, 10KB, 100KB
}

/// <summary>
/// Security test configuration.
/// </summary>
public class AzureSecurityTestConfiguration
{
    /// <summary>
    /// Tests system-assigned managed identity.
    /// </summary>
    public bool TestSystemAssignedIdentity { get; set; } = true;

    /// <summary>
    /// Tests user-assigned managed identity.
    /// </summary>
    public bool TestUserAssignedIdentity { get; set; }

    /// <summary>
    /// Tests RBAC permissions.
    /// </summary>
    public bool TestRBACPermissions { get; set; } = true;

    /// <summary>
    /// Tests Key Vault access.
    /// </summary>
    public bool TestKeyVaultAccess { get; set; } = true;

    /// <summary>
    /// Tests sensitive data masking.
    /// </summary>
    public bool TestSensitiveDataMasking { get; set; } = true;

    /// <summary>
    /// Tests audit logging.
    /// </summary>
    public bool TestAuditLogging { get; set; } = true;

    /// <summary>
    /// Test key names for Key Vault.
    /// </summary>
    public List<string> TestKeyNames { get; set; } = new() { "test-key-1", "test-key-2" };

    /// <summary>
    /// Required Service Bus RBAC roles.
    /// </summary>
    public List<string> RequiredServiceBusRoles { get; set; } = new()
    {
        "Azure Service Bus Data Sender",
        "Azure Service Bus Data Receiver"
    };

    /// <summary>
    /// Required Key Vault RBAC roles.
    /// </summary>
    public List<string> RequiredKeyVaultRoles { get; set; } = new()
    {
        "Key Vault Crypto User"
    };
}

/// <summary>
/// Resilience test configuration.
/// </summary>
public class AzureResilienceTestConfiguration
{
    /// <summary>
    /// Tests circuit breaker patterns.
    /// </summary>
    public bool TestCircuitBreaker { get; set; } = true;

    /// <summary>
    /// Tests retry policies.
    /// </summary>
    public bool TestRetryPolicies { get; set; } = true;

    /// <summary>
    /// Tests throttling handling.
    /// </summary>
    public bool TestThrottlingHandling { get; set; } = true;

    /// <summary>
    /// Tests network partition recovery.
    /// </summary>
    public bool TestNetworkPartitions { get; set; } = true;

    /// <summary>
    /// Circuit breaker failure threshold.
    /// </summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    /// <summary>
    /// Circuit breaker timeout before attempting recovery.
    /// </summary>
    public TimeSpan CircuitBreakerTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Maximum retry attempts.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Base delay for exponential backoff.
    /// </summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);
}

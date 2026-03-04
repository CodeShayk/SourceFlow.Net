using System.Collections.Concurrent;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Azure.Tests.TestHelpers;

/// <summary>
/// Test implementation of Azure resource manager for validating resource management properties.
/// This is a mock/test double that simulates Azure resource management behavior.
/// </summary>
public class TestAzureResourceManager : IDisposable
{
    private readonly ConcurrentDictionary<string, AzureTestResource> _trackedResources = new();
    private readonly ConcurrentDictionary<string, bool> _protectedResources = new();
    private bool _disposed;

    public TestAzureResourceManager()
    {
    }

    /// <summary>
    /// Creates a resource and returns its unique identifier.
    /// Resource creation is idempotent - creating the same resource twice returns the same ID.
    /// </summary>
    public string CreateResource(AzureTestResource resource)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TestAzureResourceManager));

        // Generate a unique resource ID based on type and name
        var resourceId = GenerateResourceId(resource);

        // Idempotent creation - if resource already exists, return existing ID
        if (_trackedResources.ContainsKey(resourceId))
        {
            return resourceId;
        }

        // Add resource to tracking
        if (_trackedResources.TryAdd(resourceId, resource))
        {
            return resourceId;
        }

        // Concurrent creation detected - return existing
        return resourceId;
    }

    /// <summary>
    /// Gets all currently tracked resources.
    /// </summary>
    public IEnumerable<string> GetTrackedResources()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TestAzureResourceManager));

        return _trackedResources.Keys.ToList();
    }

    /// <summary>
    /// Marks a resource as protected to simulate cleanup failures.
    /// </summary>
    public void MarkResourceAsProtected(string resourceId)
    {
        _protectedResources.TryAdd(resourceId, true);
    }

    /// <summary>
    /// Cleans up all tracked resources.
    /// Returns a result indicating success and any failures.
    /// </summary>
    public CleanupResult CleanupAllResources()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TestAzureResourceManager));

        var result = new CleanupResult { Success = true };
        var resourcesToCleanup = _trackedResources.Keys.ToList();

        foreach (var resourceId in resourcesToCleanup)
        {
            // Check if resource is protected (simulates cleanup failure)
            if (_protectedResources.ContainsKey(resourceId))
            {
                result.Success = false;
                result.FailedResources.Add(resourceId);
                result.Message += $"Failed to cleanup protected resource: {resourceId}; ";
                continue;
            }

            // Remove from tracking
            if (_trackedResources.TryRemove(resourceId, out var resource))
            {
                result.CleanedResources.Add(resourceId);
            }
            else
            {
                result.Success = false;
                result.FailedResources.Add(resourceId);
                result.Message += $"Failed to remove resource from tracking: {resourceId}; ";
            }
        }

        if (result.Success)
        {
            result.Message = $"Successfully cleaned up {result.CleanedResources.Count} resources";
        }

        return result;
    }

    /// <summary>
    /// Forces cleanup of all resources, including protected ones.
    /// Used for test isolation to ensure no resources leak between tests.
    /// </summary>
    public void ForceCleanupAll()
    {
        _protectedResources.Clear();
        _trackedResources.Clear();
    }

    /// <summary>
    /// Checks if the manager can detect existing resources.
    /// In a real implementation, this would query Azure to discover existing resources.
    /// </summary>
    public bool CanDetectExistingResources()
    {
        // In this test implementation, we simulate the ability to detect existing resources
        // A real implementation would use Azure SDK to query for resources
        return true;
    }

    /// <summary>
    /// Generates a deterministic resource ID based on resource type and name.
    /// </summary>
    private string GenerateResourceId(AzureTestResource resource)
    {
        // Format: /subscriptions/test/resourceGroups/test/providers/Microsoft.{Provider}/{Type}/{Name}
        var provider = resource.Type switch
        {
            AzureResourceType.ServiceBusQueue => "ServiceBus/namespaces/test-namespace/queues",
            AzureResourceType.ServiceBusTopic => "ServiceBus/namespaces/test-namespace/topics",
            AzureResourceType.ServiceBusSubscription => "ServiceBus/namespaces/test-namespace/topics/test-topic/subscriptions",
            AzureResourceType.KeyVaultKey => "KeyVault/vaults/test-vault/keys",
            AzureResourceType.KeyVaultSecret => "KeyVault/vaults/test-vault/secrets",
            _ => "Unknown"
        };

        return $"/subscriptions/test-subscription/resourceGroups/test-rg/providers/Microsoft.{provider}/{resource.Name}";
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
    }
}

/// <summary>
/// Result of a cleanup operation.
/// </summary>
public class CleanupResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> CleanedResources { get; set; } = new();
    public List<string> FailedResources { get; set; } = new();
}

/// <summary>
/// Exception thrown when a resource conflict is detected.
/// </summary>
public class ResourceConflictException : Exception
{
    public ResourceConflictException(string message) : base(message)
    {
    }

    public ResourceConflictException(string message, Exception innerException) 
        : base(message, innerException)
    {
    }
}

namespace SourceFlow.Cloud.AWS.Tests.TestHelpers;

/// <summary>
/// Interface for managing LocalStack container lifecycle
/// Provides comprehensive container management for AWS service emulation
/// </summary>
public interface ILocalStackManager : IAsyncDisposable
{
    /// <summary>
    /// Whether LocalStack container is currently running
    /// </summary>
    bool IsRunning { get; }
    
    /// <summary>
    /// LocalStack container endpoint URL
    /// </summary>
    string Endpoint { get; }
    
    /// <summary>
    /// Start LocalStack container with the specified configuration
    /// </summary>
    /// <param name="config">LocalStack configuration</param>
    Task StartAsync(LocalStackConfiguration config);
    
    /// <summary>
    /// Stop LocalStack container and clean up resources
    /// </summary>
    Task StopAsync();
    
    /// <summary>
    /// Check if a specific AWS service is available in LocalStack
    /// </summary>
    /// <param name="serviceName">AWS service name (e.g., "sqs", "sns", "kms")</param>
    /// <returns>True if service is available and ready</returns>
    Task<bool> IsServiceAvailableAsync(string serviceName);
    
    /// <summary>
    /// Wait for multiple AWS services to become available
    /// </summary>
    /// <param name="services">Service names to wait for</param>
    /// <param name="timeout">Maximum time to wait</param>
    Task WaitForServicesAsync(string[] services, TimeSpan? timeout = null);
    
    /// <summary>
    /// Get the endpoint URL for a specific AWS service
    /// </summary>
    /// <param name="serviceName">AWS service name</param>
    /// <returns>Service endpoint URL</returns>
    string GetServiceEndpoint(string serviceName);
    
    /// <summary>
    /// Get health status for all enabled services
    /// </summary>
    /// <returns>Dictionary of service names and their health status</returns>
    Task<Dictionary<string, LocalStackServiceHealth>> GetServicesHealthAsync();
    
    /// <summary>
    /// Reset LocalStack data (clear all resources)
    /// </summary>
    Task ResetDataAsync();
    
    /// <summary>
    /// Get LocalStack container logs
    /// </summary>
    /// <param name="tail">Number of lines to retrieve from the end</param>
    /// <returns>Container logs</returns>
    Task<string> GetLogsAsync(int tail = 100);
}

/// <summary>
/// LocalStack service health information
/// </summary>
public class LocalStackServiceHealth
{
    /// <summary>
    /// Service name
    /// </summary>
    public string ServiceName { get; set; } = "";
    
    /// <summary>
    /// Whether the service is available
    /// </summary>
    public bool IsAvailable { get; set; }
    
    /// <summary>
    /// Service status message
    /// </summary>
    public string Status { get; set; } = "";
    
    /// <summary>
    /// Last health check timestamp
    /// </summary>
    public DateTime LastChecked { get; set; }
    
    /// <summary>
    /// Response time for health check
    /// </summary>
    public TimeSpan ResponseTime { get; set; }
}

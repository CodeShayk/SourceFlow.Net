using Microsoft.Extensions.DependencyInjection;

namespace SourceFlow.Cloud.AWS.Tests.TestHelpers;

/// <summary>
/// Base interface for cloud test environments
/// Provides common functionality for managing cloud service test environments
/// </summary>
public interface ICloudTestEnvironment : IAsyncDisposable
{
    /// <summary>
    /// Whether this environment uses local emulators
    /// </summary>
    bool IsLocalEmulator { get; }
    
    /// <summary>
    /// Initialize the test environment
    /// </summary>
    Task InitializeAsync();
    
    /// <summary>
    /// Check if the environment is available and ready for testing
    /// </summary>
    Task<bool> IsAvailableAsync();
    
    /// <summary>
    /// Create a service collection configured for this test environment
    /// </summary>
    IServiceCollection CreateTestServices();
    
    /// <summary>
    /// Clean up all test resources
    /// </summary>
    Task CleanupAsync();
}
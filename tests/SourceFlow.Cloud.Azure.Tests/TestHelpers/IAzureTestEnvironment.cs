using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Secrets;

namespace SourceFlow.Cloud.Azure.Tests.TestHelpers;

/// <summary>
/// Interface for Azure test environment management.
/// Provides abstraction for both Azurite emulator and real Azure cloud environments.
/// </summary>
public interface IAzureTestEnvironment
{
    /// <summary>
    /// Initializes the test environment (starts Azurite or validates Azure connectivity).
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Cleans up the test environment (stops Azurite or cleans up Azure resources).
    /// </summary>
    Task CleanupAsync();

    /// <summary>
    /// Indicates whether this environment uses the Azurite emulator.
    /// </summary>
    bool IsAzuriteEmulator { get; }

    /// <summary>
    /// Gets the Service Bus connection string for the environment.
    /// </summary>
    string GetServiceBusConnectionString();

    /// <summary>
    /// Gets the Service Bus fully qualified namespace.
    /// </summary>
    string GetServiceBusFullyQualifiedNamespace();

    /// <summary>
    /// Gets the Key Vault URL for the environment.
    /// </summary>
    string GetKeyVaultUrl();

    /// <summary>
    /// Checks if Service Bus is available and accessible.
    /// </summary>
    Task<bool> IsServiceBusAvailableAsync();

    /// <summary>
    /// Checks if Key Vault is available and accessible.
    /// </summary>
    Task<bool> IsKeyVaultAvailableAsync();

    /// <summary>
    /// Checks if managed identity is configured and working.
    /// </summary>
    Task<bool> IsManagedIdentityConfiguredAsync();

    /// <summary>
    /// Gets the Azure credential for authentication.
    /// </summary>
    Task<TokenCredential> GetAzureCredentialAsync();

    /// <summary>
    /// Gets environment metadata for diagnostics and reporting.
    /// </summary>
    Task<Dictionary<string, string>> GetEnvironmentMetadataAsync();

    /// <summary>
    /// Creates a configured Service Bus client for the environment.
    /// </summary>
    ServiceBusClient CreateServiceBusClient();

    /// <summary>
    /// Creates a configured Service Bus administration client for the environment.
    /// </summary>
    ServiceBusAdministrationClient CreateServiceBusAdministrationClient();

    /// <summary>
    /// Creates a configured Key Vault key client for the environment.
    /// </summary>
    KeyClient CreateKeyClient();

    /// <summary>
    /// Creates a configured Key Vault secret client for the environment.
    /// </summary>
    SecretClient CreateSecretClient();

    /// <summary>
    /// Gets the Azure credential for authentication (synchronous version).
    /// </summary>
    TokenCredential GetAzureCredential();

    /// <summary>
    /// Checks if the environment has Service Bus permissions.
    /// </summary>
    bool HasServiceBusPermissions();

    /// <summary>
    /// Checks if the environment has Key Vault permissions.
    /// </summary>
    bool HasKeyVaultPermissions();
}

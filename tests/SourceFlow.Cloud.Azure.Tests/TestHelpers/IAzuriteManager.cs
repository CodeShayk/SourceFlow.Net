namespace SourceFlow.Cloud.Azure.Tests.TestHelpers;

/// <summary>
/// Interface for managing Azurite emulator lifecycle and configuration.
/// </summary>
public interface IAzuriteManager
{
    /// <summary>
    /// Starts the Azurite emulator.
    /// </summary>
    Task StartAsync();

    /// <summary>
    /// Stops the Azurite emulator.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Configures Service Bus emulation in Azurite.
    /// </summary>
    Task ConfigureServiceBusAsync();

    /// <summary>
    /// Configures Key Vault emulation in Azurite.
    /// </summary>
    Task ConfigureKeyVaultAsync();

    /// <summary>
    /// Checks if Azurite is currently running.
    /// </summary>
    Task<bool> IsRunningAsync();

    /// <summary>
    /// Gets the Azurite Service Bus connection string.
    /// </summary>
    string GetServiceBusConnectionString();

    /// <summary>
    /// Gets the Azurite Key Vault URL.
    /// </summary>
    string GetKeyVaultUrl();
}

using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Azure.Tests.TestHelpers;

/// <summary>
/// Base class for Azure integration tests that require external services.
/// Validates service availability before running tests and skips gracefully if unavailable.
/// </summary>
public abstract class AzureIntegrationTestBase : IAsyncLifetime
{
    protected readonly ITestOutputHelper Output;
    protected readonly AzureTestConfiguration Configuration;

    protected AzureIntegrationTestBase(ITestOutputHelper output)
    {
        Output = output;
        Configuration = AzureTestConfiguration.CreateDefault();
    }

    /// <summary>
    /// Initializes the test by validating service availability.
    /// Override this method to add custom initialization logic.
    /// </summary>
    public virtual async Task InitializeAsync()
    {
        await ValidateServiceAvailabilityAsync();
    }

    /// <summary>
    /// Cleans up test resources.
    /// Override this method to add custom cleanup logic.
    /// </summary>
    public virtual Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Validates that required Azure services are available.
    /// Override this method to customize which services to check.
    /// </summary>
    protected virtual async Task ValidateServiceAvailabilityAsync()
    {
        // Default implementation - subclasses should override
        await Task.CompletedTask;
    }

    /// <summary>
    /// Creates a skip message with actionable guidance for the user.
    /// </summary>
    protected string CreateSkipMessage(string serviceName, bool requiresAzurite, bool requiresAzure)
    {
        var message = $"{serviceName} is not available.\n\n";
        message += "Options:\n";

        if (requiresAzurite)
        {
            message += "1. Start Azurite emulator:\n";
            message += "   npm install -g azurite\n";
            message += "   azurite --silent --location c:\\azurite\n\n";
        }

        if (requiresAzure)
        {
            message += $"2. Configure real Azure {serviceName}:\n";
            
            if (serviceName.Contains("Service Bus"))
            {
                message += "   set AZURE_SERVICEBUS_NAMESPACE=myservicebus.servicebus.windows.net\n";
                message += "   OR\n";
                message += "   set AZURE_SERVICEBUS_CONNECTION_STRING=Endpoint=sb://...\n\n";
            }
            
            if (serviceName.Contains("Key Vault"))
            {
                message += "   set AZURE_KEYVAULT_URL=https://mykeyvault.vault.azure.net/\n\n";
            }
        }

        message += "3. Skip integration tests:\n";
        message += "   dotnet test --filter \"Category!=Integration\"\n\n";

        message += "For more information, see: tests/SourceFlow.Cloud.Azure.Tests/README.md";

        return message;
    }
}

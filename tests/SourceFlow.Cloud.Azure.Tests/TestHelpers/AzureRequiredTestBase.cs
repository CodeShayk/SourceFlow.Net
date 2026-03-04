using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Azure.Tests.TestHelpers;

/// <summary>
/// Base class for tests that require real Azure services.
/// Validates Azure service availability before running tests.
/// </summary>
public abstract class AzureRequiredTestBase : AzureIntegrationTestBase
{
    private readonly bool _requiresServiceBus;
    private readonly bool _requiresKeyVault;

    protected AzureRequiredTestBase(
        ITestOutputHelper output,
        bool requiresServiceBus = true,
        bool requiresKeyVault = false) : base(output)
    {
        _requiresServiceBus = requiresServiceBus;
        _requiresKeyVault = requiresKeyVault;
    }

    /// <summary>
    /// Validates that required Azure services are available.
    /// </summary>
    protected override async Task ValidateServiceAvailabilityAsync()
    {
        if (_requiresServiceBus)
        {
            Output.WriteLine("Checking Azure Service Bus availability...");
            var isServiceBusAvailable = await Configuration.IsServiceBusAvailableAsync(AzureTestDefaults.ConnectionTimeout);

            if (!isServiceBusAvailable)
            {
                var skipMessage = CreateSkipMessage("Azure Service Bus", requiresAzurite: false, requiresAzure: true);
                Output.WriteLine($"SKIPPED: {skipMessage}");
                throw new InvalidOperationException($"Test skipped: {skipMessage}");
            }

            Output.WriteLine("Azure Service Bus is available.");
        }

        if (_requiresKeyVault)
        {
            Output.WriteLine("Checking Azure Key Vault availability...");
            var isKeyVaultAvailable = await Configuration.IsKeyVaultAvailableAsync(AzureTestDefaults.ConnectionTimeout);

            if (!isKeyVaultAvailable)
            {
                var skipMessage = CreateSkipMessage("Azure Key Vault", requiresAzurite: false, requiresAzure: true);
                Output.WriteLine($"SKIPPED: {skipMessage}");
                throw new InvalidOperationException($"Test skipped: {skipMessage}");
            }

            Output.WriteLine("Azure Key Vault is available.");
        }
    }
}

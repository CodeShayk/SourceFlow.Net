using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Azure.Tests.TestHelpers;

/// <summary>
/// Base class for tests that require Azurite emulator.
/// Validates Azurite availability before running tests.
/// </summary>
public abstract class AzuriteRequiredTestBase : AzureIntegrationTestBase
{
    protected AzuriteRequiredTestBase(ITestOutputHelper output) : base(output)
    {
    }

    /// <summary>
    /// Validates that Azurite emulator is available.
    /// </summary>
    protected override async Task ValidateServiceAvailabilityAsync()
    {
        Output.WriteLine("Checking Azurite availability...");

        var isAvailable = await Configuration.IsAzuriteAvailableAsync(AzureTestDefaults.ConnectionTimeout);

        if (!isAvailable)
        {
            var skipMessage = CreateSkipMessage("Azurite emulator", requiresAzurite: true, requiresAzure: false);
            Output.WriteLine($"SKIPPED: {skipMessage}");
            
            // Mark test as inconclusive by throwing an exception
            // xUnit will show this as a failed test with the message
            throw new InvalidOperationException($"Test skipped: {skipMessage}");
        }

        Output.WriteLine("Azurite is available.");
    }
}

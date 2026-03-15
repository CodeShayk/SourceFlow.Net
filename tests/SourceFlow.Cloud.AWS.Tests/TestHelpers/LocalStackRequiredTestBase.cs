using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.AWS.Tests.TestHelpers;

/// <summary>
/// Base class for tests that require LocalStack emulator.
/// Validates LocalStack availability before running tests.
/// </summary>
public abstract class LocalStackRequiredTestBase : AwsIntegrationTestBase
{
    protected LocalStackRequiredTestBase(ITestOutputHelper output) : base(output)
    {
    }

    /// <summary>
    /// Validates that LocalStack emulator is available.
    /// </summary>
    protected override async Task ValidateServiceAvailabilityAsync()
    {
        Output.WriteLine("Checking LocalStack availability...");

        var isAvailable = await Configuration.IsLocalStackAvailableAsync(AwsTestDefaults.ConnectionTimeout);

        if (!isAvailable)
        {
            var skipMessage = CreateSkipMessage("LocalStack emulator", requiresLocalStack: true, requiresAws: false);
            Output.WriteLine($"SKIPPED: {skipMessage}");
            throw new InvalidOperationException($"Test skipped: {skipMessage}");
        }

        Output.WriteLine("LocalStack is available.");
    }
}

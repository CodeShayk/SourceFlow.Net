using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.AWS.Tests.TestHelpers;

/// <summary>
/// Base class for tests that require real AWS services.
/// Validates AWS service availability before running tests.
/// </summary>
public abstract class AwsRequiredTestBase : AwsIntegrationTestBase
{
    private readonly bool _requiresSqs;
    private readonly bool _requiresSns;
    private readonly bool _requiresKms;

    protected AwsRequiredTestBase(
        ITestOutputHelper output,
        bool requiresSqs = true,
        bool requiresSns = false,
        bool requiresKms = false) : base(output)
    {
        _requiresSqs = requiresSqs;
        _requiresSns = requiresSns;
        _requiresKms = requiresKms;
    }

    /// <summary>
    /// Validates that required AWS services are available.
    /// </summary>
    protected override async Task ValidateServiceAvailabilityAsync()
    {
        if (_requiresSqs)
        {
            Output.WriteLine("Checking AWS SQS availability...");
            var isSqsAvailable = await Configuration.IsSqsAvailableAsync(AwsTestDefaults.ConnectionTimeout);

            if (!isSqsAvailable)
            {
                var skipMessage = CreateSkipMessage("AWS SQS", requiresLocalStack: false, requiresAws: true);
                Output.WriteLine($"SKIPPED: {skipMessage}");
                throw new InvalidOperationException($"Test skipped: {skipMessage}");
            }

            Output.WriteLine("AWS SQS is available.");
        }

        if (_requiresSns)
        {
            Output.WriteLine("Checking AWS SNS availability...");
            var isSnsAvailable = await Configuration.IsSnsAvailableAsync(AwsTestDefaults.ConnectionTimeout);

            if (!isSnsAvailable)
            {
                var skipMessage = CreateSkipMessage("AWS SNS", requiresLocalStack: false, requiresAws: true);
                Output.WriteLine($"SKIPPED: {skipMessage}");
                throw new InvalidOperationException($"Test skipped: {skipMessage}");
            }

            Output.WriteLine("AWS SNS is available.");
        }

        if (_requiresKms)
        {
            Output.WriteLine("Checking AWS KMS availability...");
            var isKmsAvailable = await Configuration.IsKmsAvailableAsync(AwsTestDefaults.ConnectionTimeout);

            if (!isKmsAvailable)
            {
                var skipMessage = CreateSkipMessage("AWS KMS", requiresLocalStack: false, requiresAws: true);
                Output.WriteLine($"SKIPPED: {skipMessage}");
                throw new InvalidOperationException($"Test skipped: {skipMessage}");
            }

            Output.WriteLine("AWS KMS is available.");
        }
    }
}

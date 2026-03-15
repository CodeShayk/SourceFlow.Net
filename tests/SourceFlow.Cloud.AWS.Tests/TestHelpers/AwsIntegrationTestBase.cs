using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.AWS.Tests.TestHelpers;

/// <summary>
/// Base class for AWS integration tests that require external services.
/// Validates service availability before running tests and skips gracefully if unavailable.
/// </summary>
public abstract class AwsIntegrationTestBase : IAsyncLifetime
{
    protected readonly ITestOutputHelper Output;
    protected readonly AwsTestConfiguration Configuration;

    protected AwsIntegrationTestBase(ITestOutputHelper output)
    {
        Output = output;
        Configuration = new AwsTestConfiguration();
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
    /// Validates that required AWS services are available.
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
    protected string CreateSkipMessage(string serviceName, bool requiresLocalStack, bool requiresAws)
    {
        var message = $"{serviceName} is not available.\n\n";
        message += "Options:\n";

        if (requiresLocalStack)
        {
            message += "1. Start LocalStack:\n";
            message += "   docker run -d -p 4566:4566 localstack/localstack\n";
            message += "   OR\n";
            message += "   localstack start\n\n";
        }

        if (requiresAws)
        {
            message += $"2. Configure real AWS {serviceName}:\n";
            
            if (serviceName.Contains("SQS") || serviceName.Contains("SNS") || serviceName.Contains("KMS"))
            {
                message += "   set AWS_ACCESS_KEY_ID=your-access-key\n";
                message += "   set AWS_SECRET_ACCESS_KEY=your-secret-key\n";
                message += "   set AWS_REGION=us-east-1\n\n";
            }
        }

        message += "3. Skip integration tests:\n";
        message += "   dotnet test --filter \"Category!=Integration\"\n\n";

        message += "For more information, see: tests/SourceFlow.Cloud.AWS.Tests/README.md";

        return message;
    }
}

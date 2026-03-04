namespace SourceFlow.Cloud.AWS.Tests.TestHelpers;

/// <summary>
/// Constants for test categorization using xUnit traits.
/// Allows filtering tests based on external dependencies.
/// </summary>
public static class TestCategories
{
    /// <summary>
    /// Unit tests with no external dependencies (mocked services).
    /// Can run without any AWS infrastructure.
    /// </summary>
    public const string Unit = "Unit";

    /// <summary>
    /// Integration tests that require external services (LocalStack or real AWS).
    /// Use --filter "Category!=Integration" to skip these tests.
    /// </summary>
    public const string Integration = "Integration";

    /// <summary>
    /// Tests that require LocalStack emulator to be running.
    /// Use --filter "Category!=RequiresLocalStack" to skip these tests.
    /// </summary>
    public const string RequiresLocalStack = "RequiresLocalStack";

    /// <summary>
    /// Tests that require real AWS services (SQS, SNS, KMS, etc.).
    /// Use --filter "Category!=RequiresAWS" to skip these tests.
    /// </summary>
    public const string RequiresAWS = "RequiresAWS";
}

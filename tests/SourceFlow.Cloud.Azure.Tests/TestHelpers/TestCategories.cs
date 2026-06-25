namespace SourceFlow.Cloud.Azure.Tests.TestHelpers;

/// <summary>
/// Constants for test categorization using xUnit traits.
/// Allows filtering tests based on external dependencies.
/// </summary>
public static class TestCategories
{
    /// <summary>
    /// Unit tests with no external dependencies (mocked services).
    /// Can run without any Azure infrastructure.
    /// </summary>
    public const string Unit = "Unit";

    /// <summary>
    /// Integration tests that require external services (Azurite or real Azure).
    /// Use --filter "Category!=Integration" to skip these tests.
    /// </summary>
    public const string Integration = "Integration";

    /// <summary>
    /// Tests that require Azurite emulator to be running.
    /// Use --filter "Category!=RequiresAzurite" to skip these tests.
    /// </summary>
    public const string RequiresAzurite = "RequiresAzurite";

    /// <summary>
    /// Tests that require real Azure services (Service Bus, Key Vault, etc.).
    /// Use --filter "Category!=RequiresAzure" to skip these tests.
    /// </summary>
    public const string RequiresAzure = "RequiresAzure";
}

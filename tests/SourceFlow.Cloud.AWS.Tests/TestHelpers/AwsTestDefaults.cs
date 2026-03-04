namespace SourceFlow.Cloud.AWS.Tests.TestHelpers;

/// <summary>
/// Default configuration values for AWS tests.
/// </summary>
public static class AwsTestDefaults
{
    /// <summary>
    /// Default timeout for initial connection attempts to AWS services.
    /// Tests will fail fast if services don't respond within this time.
    /// </summary>
    public static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Default timeout for AWS operations during tests.
    /// </summary>
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Default timeout for long-running performance tests.
    /// </summary>
    public static readonly TimeSpan PerformanceTestTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Default number of retry attempts for transient failures.
    /// </summary>
    public const int DefaultRetryAttempts = 3;

    /// <summary>
    /// Default delay between retry attempts.
    /// </summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(1);
}

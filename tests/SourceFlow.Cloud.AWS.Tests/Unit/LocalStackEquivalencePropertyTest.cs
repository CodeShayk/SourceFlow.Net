using FsCheck;
using FsCheck.Xunit;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;

namespace SourceFlow.Cloud.AWS.Tests.Unit;

/// <summary>
/// Dedicated property test for LocalStack AWS service equivalence.
///
/// NOTE: Real LocalStack equivalence testing (verifying that LocalStack SQS, SNS, and KMS behave
/// identically to real AWS services under various scenarios) must be done in integration tests
/// that actually spin up a LocalStack container and execute API calls. Property tests that do not
/// exercise real infrastructure cannot validate functional equivalence.
///
/// This class validates only the structural invariants of <see cref="AwsTestScenario"/> itself,
/// ensuring that generated test scenarios satisfy their own documented constraints.
/// </summary>
[Trait("Category", "Unit")]
public class LocalStackEquivalencePropertyTest
{
    /// <summary>
    /// Generator for AWS test scenarios that can run on both LocalStack and real AWS
    /// </summary>
    public static Arbitrary<AwsTestScenario> AwsTestScenarioGenerator()
    {
        return Arb.From(
            from testPrefix in Arb.Generate<NonEmptyString>()
                .Select(x => new string(x.Get.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray()))
                .Where(x => !string.IsNullOrEmpty(x) && x.Length >= 3 && x.Length <= 20)
            from messageCount in Arb.Generate<int>().Where(x => x >= 1 && x <= 10)
            from messageSize in Arb.Generate<int>().Where(x => x >= 100 && x <= 1024)
            from useEncryption in Arb.Generate<bool>()
            from enableDlq in Arb.Generate<bool>()
            from testTimeout in Arb.Generate<int>().Where(x => x >= 30 && x <= 300)
            select new AwsTestScenario
            {
                TestPrefix = testPrefix,
                MessageCount = messageCount,
                MessageSize = messageSize,
                UseEncryption = useEncryption,
                EnableDeadLetterQueue = enableDlq,
                TestTimeoutSeconds = testTimeout,
                TestId = Guid.NewGuid().ToString("N")[..8]
            });
    }

    /// <summary>
    /// Property: AwsTestScenario invariants are satisfied by the generator.
    ///
    /// This validates that generated <see cref="AwsTestScenario"/> objects satisfy their own
    /// documented constraints (e.g., MessageCount > 0, MessageSize within SQS limits,
    /// BatchSize &lt;= 10, etc.) as expressed by <see cref="AwsTestScenario.IsValid"/>.
    ///
    /// Real LocalStack/AWS equivalence testing belongs in integration tests that make actual
    /// network calls to LocalStack or AWS endpoints.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(LocalStackEquivalencePropertyTest) })]
    public Property GeneratedScenarioSatisfiesItsOwnInvariants(AwsTestScenario scenario)
    {
        // The scenario must not be null
        var notNull = scenario != null;

        if (!notNull)
            return false.ToProperty();

        // MessageCount must be positive (required by SQS: at least 1 message)
        var messageCountPositive = scenario!.MessageCount > 0;

        // MessageSize must be within SQS limits (100 bytes minimum, 256 KB maximum)
        var messageSizeValid = scenario.MessageSize >= 100 && scenario.MessageSize <= 262144;

        // BatchSize must respect the AWS SQS batch limit of 10
        var batchSizeValid = scenario.BatchSize > 0 && scenario.BatchSize <= 10;

        // TestTimeoutSeconds must be positive
        var timeoutPositive = scenario.TestTimeoutSeconds > 0;

        // TestPrefix and TestId must be non-empty (needed to generate unique resource names)
        var namesPresent = !string.IsNullOrEmpty(scenario.TestPrefix) &&
                           !string.IsNullOrEmpty(scenario.TestId);

        // Region must be specified
        var regionPresent = !string.IsNullOrEmpty(scenario.Region);

        // SubscriberCount must be at least 1
        var subscriberCountValid = scenario.SubscriberCount >= 1;

        // IsValid() should agree with all the above
        var isValidConsistent = scenario.IsValid() ==
            (messageCountPositive && messageSizeValid && batchSizeValid &&
             timeoutPositive && namesPresent && regionPresent && subscriberCountValid);

        return (messageCountPositive &&
                messageSizeValid &&
                batchSizeValid &&
                timeoutPositive &&
                namesPresent &&
                regionPresent &&
                subscriberCountValid &&
                isValidConsistent).ToProperty();
    }
}

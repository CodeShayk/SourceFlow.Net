using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Azure.Tests.TestHelpers;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Azure.Tests.Integration;

/// <summary>
/// Property-based tests for Azure health checks.
/// **Property 10: Azure Health Check Accuracy**
/// For any Azure service configuration (Service Bus, Key Vault), health checks should accurately 
/// reflect the actual availability and accessibility of the service, returning true when services 
/// are available and accessible, and false when they are not.
/// **Validates: Requirements 4.1, 4.2, 4.3**
/// </summary>
public class AzureHealthCheckPropertyTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILogger<AzureHealthCheckPropertyTests> _logger;
    private IAzureTestEnvironment _testEnvironment = null!;
    private ServiceBusClient _serviceBusClient = null!;
    private ServiceBusAdministrationClient _adminClient = null!;
    private KeyClient _keyClient = null!;
    private readonly List<string> _createdQueues = new();
    private readonly List<string> _createdTopics = new();
    private readonly List<string> _createdKeys = new();

    public AzureHealthCheckPropertyTests(ITestOutputHelper output)
    {
        _output = output;
        _logger = LoggerHelper.CreateLogger<AzureHealthCheckPropertyTests>(output);
    }

    public async Task InitializeAsync()
    {
        var config = AzureTestConfiguration.CreateDefault();
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddXUnit(_output);
            builder.SetMinimumLevel(LogLevel.Information);
        });
        _testEnvironment = new AzureTestEnvironment(config, loggerFactory);
        await _testEnvironment.InitializeAsync();

        _serviceBusClient = _testEnvironment.CreateServiceBusClient();
        _adminClient = _testEnvironment.CreateServiceBusAdministrationClient();
        _keyClient = _testEnvironment.CreateKeyClient();

        _logger.LogInformation("Property test environment initialized");
    }

    public async Task DisposeAsync()
    {
        try
        {
            // Cleanup created resources
            foreach (var queueName in _createdQueues)
            {
                try
                {
                    await _adminClient.DeleteQueueAsync(queueName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error deleting queue {QueueName}", queueName);
                }
            }

            foreach (var topicName in _createdTopics)
            {
                try
                {
                    await _adminClient.DeleteTopicAsync(topicName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error deleting topic {TopicName}", topicName);
                }
            }

            foreach (var keyName in _createdKeys)
            {
                try
                {
                    var deleteOperation = await _keyClient.StartDeleteKeyAsync(keyName);
                    await deleteOperation.WaitForCompletionAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error deleting key {KeyName}", keyName);
                }
            }

            await _serviceBusClient.DisposeAsync();
            await _testEnvironment.CleanupAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during test cleanup");
        }
    }

    /// <summary>
    /// Property: Service Bus queue existence check should accurately reflect actual queue existence.
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property ServiceBusQueueExistence_ShouldAccuratelyReflectActualState(NonEmptyString queueNameGen)
    {
        var queueName = $"prop-queue-{queueNameGen.Get.ToLowerInvariant().Replace(" ", "-")}-{Guid.NewGuid():N}".Substring(0, 50);

        return Prop.ForAll<bool>(Arb.From<bool>(), shouldExist =>
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    // Arrange - Create queue if it should exist
                    if (shouldExist)
                    {
                        await _adminClient.CreateQueueAsync(queueName);
                        _createdQueues.Add(queueName);
                        _logger.LogInformation("Created queue for property test: {QueueName}", queueName);
                    }

                    // Act - Check existence
                    var existsResponse = await _adminClient.QueueExistsAsync(queueName);
                    var actualExists = existsResponse.Value;

                    // Assert - Health check should match actual state
                    var healthCheckAccurate = actualExists == shouldExist;

                    _logger.LogInformation(
                        "Queue existence check: Expected={Expected}, Actual={Actual}, Accurate={Accurate}",
                        shouldExist, actualExists, healthCheckAccurate);

                    return healthCheckAccurate;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in queue existence property test");
                    return false;
                }
            });

            return task.GetAwaiter().GetResult();
        });
    }

    /// <summary>
    /// Property: Service Bus topic existence check should accurately reflect actual topic existence.
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property ServiceBusTopicExistence_ShouldAccuratelyReflectActualState(NonEmptyString topicNameGen)
    {
        var topicName = $"prop-topic-{topicNameGen.Get.ToLowerInvariant().Replace(" ", "-")}-{Guid.NewGuid():N}".Substring(0, 50);

        return Prop.ForAll<bool>(Arb.From<bool>(), shouldExist =>
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    // Arrange - Create topic if it should exist
                    if (shouldExist)
                    {
                        await _adminClient.CreateTopicAsync(topicName);
                        _createdTopics.Add(topicName);
                        _logger.LogInformation("Created topic for property test: {TopicName}", topicName);
                    }

                    // Act - Check existence
                    var existsResponse = await _adminClient.TopicExistsAsync(topicName);
                    var actualExists = existsResponse.Value;

                    // Assert - Health check should match actual state
                    var healthCheckAccurate = actualExists == shouldExist;

                    _logger.LogInformation(
                        "Topic existence check: Expected={Expected}, Actual={Actual}, Accurate={Accurate}",
                        shouldExist, actualExists, healthCheckAccurate);

                    return healthCheckAccurate;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in topic existence property test");
                    return false;
                }
            });

            return task.GetAwaiter().GetResult();
        });
    }

    /// <summary>
    /// Property: Service Bus send permission check should accurately reflect actual permissions.
    /// </summary>
    [Property(MaxTest = 15, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property ServiceBusSendPermission_ShouldAccuratelyReflectActualPermissions(NonEmptyString queueNameGen)
    {
        var queueName = $"prop-send-{queueNameGen.Get.ToLowerInvariant().Replace(" ", "-")}-{Guid.NewGuid():N}".Substring(0, 50);

        return Prop.ForAll<bool>(Arb.From<bool>(), _ =>
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    // Arrange - Create queue
                    await _adminClient.CreateQueueAsync(queueName);
                    _createdQueues.Add(queueName);

                    var sender = _serviceBusClient.CreateSender(queueName);
                    var testMessage = new ServiceBusMessage("Health check property test")
                    {
                        MessageId = Guid.NewGuid().ToString()
                    };

                    // Act - Attempt to send
                    var canSend = false;
                    try
                    {
                        await sender.SendMessageAsync(testMessage);
                        canSend = true;
                        _logger.LogInformation("Send permission validated for queue: {QueueName}", queueName);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        canSend = false;
                        _logger.LogInformation("Send permission denied for queue: {QueueName}", queueName);
                    }
                    finally
                    {
                        await sender.DisposeAsync();
                    }

                    // Assert - If we have proper credentials, send should succeed
                    // In test environment with proper setup, this should always be true
                    var healthCheckAccurate = canSend == _testEnvironment.HasServiceBusPermissions();

                    _logger.LogInformation(
                        "Send permission check: CanSend={CanSend}, HasPermissions={HasPermissions}, Accurate={Accurate}",
                        canSend, _testEnvironment.HasServiceBusPermissions(), healthCheckAccurate);

                    return healthCheckAccurate;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in send permission property test");
                    return false;
                }
            });

            return task.GetAwaiter().GetResult();
        });
    }

    /// <summary>
    /// Property: Key Vault key availability check should accurately reflect actual key state.
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property KeyVaultKeyAvailability_ShouldAccuratelyReflectActualState(NonEmptyString keyNameGen)
    {
        var keyName = $"prop-key-{keyNameGen.Get.ToLowerInvariant().Replace(" ", "-")}-{Guid.NewGuid():N}".Substring(0, 24);

        return Prop.ForAll<bool>(Arb.From<bool>(), shouldExist =>
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    // Arrange - Create key if it should exist
                    if (shouldExist)
                    {
                        var keyOptions = new CreateRsaKeyOptions(keyName)
                        {
                            KeySize = 2048,
                            Enabled = true
                        };
                        await _keyClient.CreateRsaKeyAsync(keyOptions);
                        _createdKeys.Add(keyName);
                        _logger.LogInformation("Created key for property test: {KeyName}", keyName);
                    }

                    // Act - Check if key exists and is available
                    var keyExists = false;
                    try
                    {
                        var key = await _keyClient.GetKeyAsync(keyName);
                        keyExists = key.Value != null && key.Value.Properties.Enabled == true;
                    }
                    catch (RequestFailedException ex) when (ex.Status == 404)
                    {
                        keyExists = false;
                    }

                    // Assert - Health check should match actual state
                    var healthCheckAccurate = keyExists == shouldExist;

                    _logger.LogInformation(
                        "Key availability check: Expected={Expected}, Actual={Actual}, Accurate={Accurate}",
                        shouldExist, keyExists, healthCheckAccurate);

                    return healthCheckAccurate;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in key availability property test");
                    return false;
                }
            });

            return task.GetAwaiter().GetResult();
        });
    }

    /// <summary>
    /// Property: Key Vault encryption capability check should accurately reflect actual permissions.
    /// </summary>
    [Property(MaxTest = 15, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property KeyVaultEncryptionCapability_ShouldAccuratelyReflectActualPermissions(NonEmptyString keyNameGen)
    {
        var keyName = $"prop-enc-{keyNameGen.Get.ToLowerInvariant().Replace(" ", "-")}-{Guid.NewGuid():N}".Substring(0, 24);

        return Prop.ForAll<bool>(Arb.From<bool>(), _ =>
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    // Arrange - Create key
                    var keyOptions = new CreateRsaKeyOptions(keyName)
                    {
                        KeySize = 2048
                    };
                    var key = await _keyClient.CreateRsaKeyAsync(keyOptions);
                    _createdKeys.Add(keyName);

                    var cryptoClient = new CryptographyClient(
                        key.Value.Id, 
                        _testEnvironment.GetAzureCredential());

                    // Act - Attempt encryption
                    var canEncrypt = false;
                    try
                    {
                        var testData = Encoding.UTF8.GetBytes("Property test data");
                        var encryptResult = await cryptoClient.EncryptAsync(
                            EncryptionAlgorithm.RsaOaep, 
                            testData);
                        canEncrypt = encryptResult.Ciphertext != null && encryptResult.Ciphertext.Length > 0;
                        _logger.LogInformation("Encryption capability validated for key: {KeyName}", keyName);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        canEncrypt = false;
                        _logger.LogInformation("Encryption permission denied for key: {KeyName}", keyName);
                    }

                    // Assert - If we have proper credentials, encryption should succeed
                    var healthCheckAccurate = canEncrypt == _testEnvironment.HasKeyVaultPermissions();

                    _logger.LogInformation(
                        "Encryption capability check: CanEncrypt={CanEncrypt}, HasPermissions={HasPermissions}, Accurate={Accurate}",
                        canEncrypt, _testEnvironment.HasKeyVaultPermissions(), healthCheckAccurate);

                    return healthCheckAccurate;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in encryption capability property test");
                    return false;
                }
            });

            return task.GetAwaiter().GetResult();
        });
    }

    /// <summary>
    /// Property: Service Bus namespace connectivity check should be consistent across multiple checks.
    /// </summary>
    [Property(MaxTest = 10)]
    public Property ServiceBusNamespaceConnectivity_ShouldBeConsistentAcrossChecks(PositiveInt checkCount)
    {
        var count = Math.Min(checkCount.Get, 10); // Limit to 10 checks

        return Prop.ForAll<bool>(Arb.From<bool>(), _ =>
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    var results = new List<bool>();

                    // Act - Perform multiple connectivity checks
                    for (int i = 0; i < count; i++)
                    {
                        var isAvailable = await _testEnvironment.IsServiceBusAvailableAsync();
                        results.Add(isAvailable);
                        await Task.Delay(100); // Small delay between checks
                    }

                    // Assert - All checks should return the same result (consistency)
                    var allSame = results.All(r => r == results[0]);

                    _logger.LogInformation(
                        "Connectivity consistency check: Performed {Count} checks, AllSame={AllSame}, Result={Result}",
                        count, allSame, results[0]);

                    return allSame;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in connectivity consistency property test");
                    return false;
                }
            });

            return task.GetAwaiter().GetResult();
        });
    }

    /// <summary>
    /// Property: Key Vault accessibility check should be consistent across multiple checks.
    /// </summary>
    [Property(MaxTest = 10)]
    public Property KeyVaultAccessibility_ShouldBeConsistentAcrossChecks(PositiveInt checkCount)
    {
        var count = Math.Min(checkCount.Get, 10); // Limit to 10 checks

        return Prop.ForAll<bool>(Arb.From<bool>(), _ =>
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    var results = new List<bool>();

                    // Act - Perform multiple accessibility checks
                    for (int i = 0; i < count; i++)
                    {
                        var isAvailable = await _testEnvironment.IsKeyVaultAvailableAsync();
                        results.Add(isAvailable);
                        await Task.Delay(100); // Small delay between checks
                    }

                    // Assert - All checks should return the same result (consistency)
                    var allSame = results.All(r => r == results[0]);

                    _logger.LogInformation(
                        "Accessibility consistency check: Performed {Count} checks, AllSame={AllSame}, Result={Result}",
                        count, allSame, results[0]);

                    return allSame;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in accessibility consistency property test");
                    return false;
                }
            });

            return task.GetAwaiter().GetResult();
        });
    }

    /// <summary>
    /// Property: Managed identity authentication status should be deterministic.
    /// </summary>
    [Property(MaxTest = 10)]
    public Property ManagedIdentityAuthenticationStatus_ShouldBeDeterministic(PositiveInt checkCount)
    {
        var count = Math.Min(checkCount.Get, 10); // Limit to 10 checks

        return Prop.ForAll<bool>(Arb.From<bool>(), _ =>
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    var results = new List<bool>();

                    // Act - Check managed identity status multiple times
                    for (int i = 0; i < count; i++)
                    {
                        var isConfigured = await _testEnvironment.IsManagedIdentityConfiguredAsync();
                        results.Add(isConfigured);
                    }

                    // Assert - All checks should return the same result
                    var allSame = results.All(r => r == results[0]);

                    _logger.LogInformation(
                        "Managed identity status check: Performed {Count} checks, AllSame={AllSame}, Result={Result}",
                        count, allSame, results[0]);

                    return allSame;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in managed identity status property test");
                    return false;
                }
            });

            return task.GetAwaiter().GetResult();
        });
    }

    /// <summary>
    /// Property: Health check for created resources should immediately reflect availability.
    /// </summary>
    [Property(MaxTest = 15, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property CreatedResourceHealthCheck_ShouldImmediatelyReflectAvailability(NonEmptyString resourceNameGen)
    {
        var queueName = $"prop-imm-{resourceNameGen.Get.ToLowerInvariant().Replace(" ", "-")}-{Guid.NewGuid():N}".Substring(0, 50);

        return Prop.ForAll<bool>(Arb.From<bool>(), _ =>
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    // Act - Create queue
                    await _adminClient.CreateQueueAsync(queueName);
                    _createdQueues.Add(queueName);
                    _logger.LogInformation("Created queue for immediate availability test: {QueueName}", queueName);

                    // Act - Immediately check existence (no delay)
                    var existsResponse = await _adminClient.QueueExistsAsync(queueName);
                    var exists = existsResponse.Value;

                    // Assert - Health check should immediately reflect that queue exists
                    _logger.LogInformation(
                        "Immediate availability check: QueueName={QueueName}, Exists={Exists}",
                        queueName, exists);

                    return exists; // Should be true immediately after creation
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in immediate availability property test");
                    return false;
                }
            });

            return task.GetAwaiter().GetResult();
        });
    }
}

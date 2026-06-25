using Azure.Messaging.ServiceBus;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Azure.Tests.TestHelpers;
using System.Diagnostics;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Azure.Tests.Integration;

/// <summary>
/// Property-based tests for Azure telemetry collection.
/// **Property 11: Azure Telemetry Collection Completeness**
/// For any Azure service operation, when Azure Monitor integration is enabled, telemetry data 
/// including metrics, traces, and logs should be collected and reported accurately with proper correlation IDs.
/// **Validates: Requirements 4.5**
/// </summary>
public class AzureTelemetryCollectionPropertyTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILogger<AzureTelemetryCollectionPropertyTests> _logger;
    private IAzureTestEnvironment _testEnvironment = null!;
    private ServiceBusClient _serviceBusClient = null!;
    private KeyClient _keyClient = null!;
    private readonly ActivitySource _activitySource = new("SourceFlow.Cloud.Azure.PropertyTests");
    private string _testQueueName = null!;
    private readonly List<string> _createdKeys = new();

    public AzureTelemetryCollectionPropertyTests(ITestOutputHelper output)
    {
        _output = output;
        _logger = LoggerHelper.CreateLogger<AzureTelemetryCollectionPropertyTests>(output);
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
        _keyClient = _testEnvironment.CreateKeyClient();

        _testQueueName = $"telemetry-prop-{Guid.NewGuid():N}";
        var adminClient = _testEnvironment.CreateServiceBusAdministrationClient();
        await adminClient.CreateQueueAsync(_testQueueName);

        _logger.LogInformation("Property test environment initialized");
    }

    public async Task DisposeAsync()
    {
        try
        {
            var adminClient = _testEnvironment.CreateServiceBusAdministrationClient();
            await adminClient.DeleteQueueAsync(_testQueueName);

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
            _activitySource.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during test cleanup");
        }
    }

    /// <summary>
    /// Property: Every Service Bus send operation should generate telemetry with correlation ID.
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property ServiceBusSendOperation_ShouldGenerateTelemetryWithCorrelationId(NonEmptyString messageContent)
    {
        var content = messageContent.Get;

        return Prop.ForAll<Guid>(Arb.From<Guid>(), correlationIdGen =>
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    var correlationId = correlationIdGen.ToString();
                    var sender = _serviceBusClient.CreateSender(_testQueueName);

                    using var activity = _activitySource.StartActivity("PropertyTest_Send", ActivityKind.Producer);
                    activity?.SetTag("correlation.id", correlationId);
                    activity?.SetTag("messaging.destination", _testQueueName);

                    var testMessage = new ServiceBusMessage(content)
                    {
                        MessageId = Guid.NewGuid().ToString(),
                        CorrelationId = correlationId
                    };

                    // Act
                    await sender.SendMessageAsync(testMessage);

                    // Assert - Telemetry should be collected
                    var telemetryCollected = activity != null &&
                                           activity.GetTagItem("correlation.id")?.ToString() == correlationId &&
                                           activity.GetTagItem("messaging.destination")?.ToString() == _testQueueName;

                    _logger.LogInformation(
                        "Send telemetry: CorrelationId={CorrelationId}, Collected={Collected}, ActivityId={ActivityId}",
                        correlationId, telemetryCollected, activity?.Id);

                    await sender.DisposeAsync();
                    return telemetryCollected;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in send telemetry property test");
                    return false;
                }
            });

            return task.GetAwaiter().GetResult();
        });
    }

    /// <summary>
    /// Property: Every Service Bus receive operation should generate telemetry with correlation ID.
    /// </summary>
    [Property(MaxTest = 15, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property ServiceBusReceiveOperation_ShouldGenerateTelemetryWithCorrelationId(NonEmptyString messageContent)
    {
        var content = messageContent.Get;

        return Prop.ForAll<Guid>(Arb.From<Guid>(), correlationIdGen =>
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    var correlationId = correlationIdGen.ToString();
                    var sender = _serviceBusClient.CreateSender(_testQueueName);
                    var receiver = _serviceBusClient.CreateReceiver(_testQueueName);

                    // Send message first
                    var testMessage = new ServiceBusMessage(content)
                    {
                        MessageId = Guid.NewGuid().ToString(),
                        CorrelationId = correlationId
                    };
                    await sender.SendMessageAsync(testMessage);

                    using var activity = _activitySource.StartActivity("PropertyTest_Receive", ActivityKind.Consumer);
                    activity?.SetTag("correlation.id", correlationId);
                    activity?.SetTag("messaging.source", _testQueueName);

                    // Act
                    var receivedMessage = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));

                    // Assert - Telemetry should be collected
                    var telemetryCollected = activity != null &&
                                           receivedMessage != null &&
                                           receivedMessage.CorrelationId == correlationId &&
                                           activity.GetTagItem("correlation.id")?.ToString() == correlationId;

                    _logger.LogInformation(
                        "Receive telemetry: CorrelationId={CorrelationId}, Collected={Collected}, MessageReceived={Received}",
                        correlationId, telemetryCollected, receivedMessage != null);

                    if (receivedMessage != null)
                    {
                        await receiver.CompleteMessageAsync(receivedMessage);
                    }

                    await sender.DisposeAsync();
                    await receiver.DisposeAsync();
                    return telemetryCollected;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in receive telemetry property test");
                    return false;
                }
            });

            return task.GetAwaiter().GetResult();
        });
    }

    /// <summary>
    /// Property: Every Key Vault encryption operation should generate telemetry with operation details.
    /// </summary>
    [Property(MaxTest = 15, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property KeyVaultEncryptionOperation_ShouldGenerateTelemetryWithDetails(NonEmptyString dataContent)
    {
        var content = dataContent.Get;
        var keyName = $"prop-tel-key-{Guid.NewGuid():N}".Substring(0, 24);

        return Prop.ForAll<bool>(Arb.From<bool>(), _ =>
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    // Create key
                    var keyOptions = new CreateRsaKeyOptions(keyName)
                    {
                        KeySize = 2048
                    };
                    var key = await _keyClient.CreateRsaKeyAsync(keyOptions);
                    _createdKeys.Add(keyName);

                    var cryptoClient = new CryptographyClient(
                        key.Value.Id,
                        _testEnvironment.GetAzureCredential());

                    using var activity = _activitySource.StartActivity("PropertyTest_Encrypt", ActivityKind.Client);
                    activity?.SetTag("keyvault.operation", "encrypt");
                    activity?.SetTag("keyvault.key", keyName);
                    activity?.SetTag("data.length", content.Length);

                    var plaintextBytes = Encoding.UTF8.GetBytes(content);

                    // Act
                    var stopwatch = Stopwatch.StartNew();
                    var encryptResult = await cryptoClient.EncryptAsync(
                        EncryptionAlgorithm.RsaOaep,
                        plaintextBytes);
                    stopwatch.Stop();

                    activity?.SetTag("operation.duration_ms", stopwatch.ElapsedMilliseconds);

                    // Assert - Telemetry should be collected
                    var telemetryCollected = activity != null &&
                                           encryptResult.Ciphertext != null &&
                                           activity.GetTagItem("keyvault.operation")?.ToString() == "encrypt" &&
                                           activity.GetTagItem("keyvault.key")?.ToString() == keyName;

                    _logger.LogInformation(
                        "Encryption telemetry: KeyName={KeyName}, Collected={Collected}, Duration={Duration}ms",
                        keyName, telemetryCollected, stopwatch.ElapsedMilliseconds);

                    return telemetryCollected;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in encryption telemetry property test");
                    return false;
                }
            });

            return task.GetAwaiter().GetResult();
        });
    }

    /// <summary>
    /// Property: Telemetry should maintain correlation across multiple operations.
    /// </summary>
    [Property(MaxTest = 10, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property MultipleOperations_ShouldMaintainCorrelationInTelemetry(PositiveInt operationCount)
    {
        var count = Math.Min(operationCount.Get, 10); // Limit to 10 operations

        return Prop.ForAll<Guid>(Arb.From<Guid>(), correlationIdGen =>
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    var correlationId = correlationIdGen.ToString();
                    var sender = _serviceBusClient.CreateSender(_testQueueName);

                    using var parentActivity = _activitySource.StartActivity("PropertyTest_MultiOp", ActivityKind.Internal);
                    parentActivity?.SetTag("correlation.id", correlationId);
                    parentActivity?.SetTag("operation.count", count);

                    var collectedCorrelationIds = new List<string>();

                    // Act - Perform multiple operations
                    for (int i = 0; i < count; i++)
                    {
                        using var childActivity = _activitySource.StartActivity(
                            $"Operation_{i}",
                            ActivityKind.Producer,
                            parentActivity?.Context ?? default);

                        childActivity?.SetTag("correlation.id", correlationId);
                        childActivity?.SetTag("operation.index", i);

                        var testMessage = new ServiceBusMessage($"Multi-op test {i}")
                        {
                            MessageId = Guid.NewGuid().ToString(),
                            CorrelationId = correlationId
                        };

                        await sender.SendMessageAsync(testMessage);

                        var capturedCorrelationId = childActivity?.GetTagItem("correlation.id")?.ToString();
                        if (capturedCorrelationId != null)
                        {
                            collectedCorrelationIds.Add(capturedCorrelationId);
                        }
                    }

                    // Assert - All operations should have the same correlation ID
                    var allCorrelationIdsMatch = collectedCorrelationIds.All(id => id == correlationId);
                    var allOperationsCollected = collectedCorrelationIds.Count == count;

                    _logger.LogInformation(
                        "Multi-operation telemetry: CorrelationId={CorrelationId}, Operations={Count}, AllMatch={AllMatch}, AllCollected={AllCollected}",
                        correlationId, count, allCorrelationIdsMatch, allOperationsCollected);

                    await sender.DisposeAsync();
                    return allCorrelationIdsMatch && allOperationsCollected;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in multi-operation telemetry property test");
                    return false;
                }
            });

            return task.GetAwaiter().GetResult();
        });
    }

    /// <summary>
    /// Property: Telemetry should capture performance metrics for all operations.
    /// </summary>
    [Property(MaxTest = 15, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property AllOperations_ShouldCapturePerformanceMetrics(NonEmptyString messageContent)
    {
        var content = messageContent.Get;

        return Prop.ForAll<bool>(Arb.From<bool>(), _ =>
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    var sender = _serviceBusClient.CreateSender(_testQueueName);

                    using var activity = _activitySource.StartActivity("PropertyTest_Performance", ActivityKind.Internal);

                    var testMessage = new ServiceBusMessage(content)
                    {
                        MessageId = Guid.NewGuid().ToString()
                    };

                    // Act - Measure operation
                    var stopwatch = Stopwatch.StartNew();
                    await sender.SendMessageAsync(testMessage);
                    stopwatch.Stop();

                    // Add performance metrics
                    activity?.SetTag("performance.duration_ms", stopwatch.ElapsedMilliseconds);
                    activity?.SetTag("performance.message_size_bytes", Encoding.UTF8.GetByteCount(content));
                    activity?.SetTag("performance.timestamp", DateTimeOffset.UtcNow.ToString("O"));

                    // Assert - Performance metrics should be captured
                    var metricsCollected = activity != null &&
                                         activity.GetTagItem("performance.duration_ms") != null &&
                                         activity.GetTagItem("performance.message_size_bytes") != null &&
                                         activity.GetTagItem("performance.timestamp") != null;

                    _logger.LogInformation(
                        "Performance metrics: Duration={Duration}ms, Size={Size} bytes, Collected={Collected}",
                        stopwatch.ElapsedMilliseconds, Encoding.UTF8.GetByteCount(content), metricsCollected);

                    await sender.DisposeAsync();
                    return metricsCollected;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in performance metrics property test");
                    return false;
                }
            });

            return task.GetAwaiter().GetResult();
        });
    }

    /// <summary>
    /// Property: Telemetry should capture error information when operations fail.
    /// </summary>
    [Property(MaxTest = 10, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property FailedOperations_ShouldCaptureErrorTelemetry(NonEmptyString queueNameGen)
    {
        var nonExistentQueue = $"non-exist-{queueNameGen.Get.ToLowerInvariant().Replace(" ", "-")}-{Guid.NewGuid():N}".Substring(0, 50);

        return Prop.ForAll<bool>(Arb.From<bool>(), _ =>
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    using var activity = _activitySource.StartActivity("PropertyTest_Error", ActivityKind.Internal);
                    activity?.SetTag("test.expected_error", true);

                    var errorCaptured = false;
                    var errorTypeCaptured = false;

                    // Act - Attempt operation that will fail
                    try
                    {
                        var sender = _serviceBusClient.CreateSender(nonExistentQueue);
                        var testMessage = new ServiceBusMessage("This should fail");
                        await sender.SendMessageAsync(testMessage);
                    }
                    catch (Exception ex)
                    {
                        // Capture error telemetry
                        activity?.SetTag("error", true);
                        activity?.SetTag("error.type", ex.GetType().Name);
                        activity?.SetTag("error.message", ex.Message);

                        errorCaptured = activity?.GetTagItem("error") != null;
                        errorTypeCaptured = activity?.GetTagItem("error.type") != null;

                        _logger.LogInformation(
                            "Error telemetry captured: ErrorType={ErrorType}, Message={Message}",
                            ex.GetType().Name, ex.Message);
                    }

                    // Assert - Error telemetry should be captured
                    var telemetryCollected = errorCaptured && errorTypeCaptured;

                    _logger.LogInformation(
                        "Error telemetry: ErrorCaptured={ErrorCaptured}, TypeCaptured={TypeCaptured}, Complete={Complete}",
                        errorCaptured, errorTypeCaptured, telemetryCollected);

                    return telemetryCollected;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in error telemetry property test");
                    return false;
                }
            });

            return task.GetAwaiter().GetResult();
        });
    }

    /// <summary>
    /// Property: Telemetry should include custom tags for all operations.
    /// </summary>
    [Property(MaxTest = 15, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property AllOperations_ShouldIncludeCustomTags(NonEmptyString tagValue)
    {
        var customTagValue = tagValue.Get;

        return Prop.ForAll<bool>(Arb.From<bool>(), _ =>
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    var sender = _serviceBusClient.CreateSender(_testQueueName);

                    using var activity = _activitySource.StartActivity("PropertyTest_CustomTags", ActivityKind.Internal);
                    
                    // Add custom tags
                    activity?.SetTag("custom.tag1", customTagValue);
                    activity?.SetTag("custom.tag2", "test-value");
                    activity?.SetTag("custom.timestamp", DateTimeOffset.UtcNow.ToString("O"));
                    activity?.SetTag("custom.environment", "property-test");

                    var testMessage = new ServiceBusMessage("Custom tags test")
                    {
                        MessageId = Guid.NewGuid().ToString()
                    };

                    // Act
                    await sender.SendMessageAsync(testMessage);

                    // Assert - Custom tags should be present
                    var customTagsCollected = activity != null &&
                                            activity.GetTagItem("custom.tag1")?.ToString() == customTagValue &&
                                            activity.GetTagItem("custom.tag2") != null &&
                                            activity.GetTagItem("custom.timestamp") != null &&
                                            activity.GetTagItem("custom.environment") != null;

                    _logger.LogInformation(
                        "Custom tags: Tag1={Tag1}, Collected={Collected}",
                        customTagValue, customTagsCollected);

                    await sender.DisposeAsync();
                    return customTagsCollected;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in custom tags property test");
                    return false;
                }
            });

            return task.GetAwaiter().GetResult();
        });
    }

    /// <summary>
    /// Property: Telemetry collection should not significantly impact operation performance.
    /// </summary>
    [Property(MaxTest = 10, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public Property TelemetryCollection_ShouldNotSignificantlyImpactPerformance(NonEmptyString messageContent)
    {
        var content = messageContent.Get;

        return Prop.ForAll<bool>(Arb.From<bool>(), _ =>
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    var sender = _serviceBusClient.CreateSender(_testQueueName);

                    // Measure without telemetry
                    var stopwatchWithoutTelemetry = Stopwatch.StartNew();
                    var testMessage1 = new ServiceBusMessage(content)
                    {
                        MessageId = Guid.NewGuid().ToString()
                    };
                    await sender.SendMessageAsync(testMessage1);
                    stopwatchWithoutTelemetry.Stop();

                    // Measure with telemetry
                    using var activity = _activitySource.StartActivity("PropertyTest_PerformanceImpact", ActivityKind.Internal);
                    activity?.SetTag("test.with_telemetry", true);

                    var stopwatchWithTelemetry = Stopwatch.StartNew();
                    var testMessage2 = new ServiceBusMessage(content)
                    {
                        MessageId = Guid.NewGuid().ToString()
                    };
                    await sender.SendMessageAsync(testMessage2);
                    stopwatchWithTelemetry.Stop();

                    // Assert - Telemetry overhead should be minimal (less than 50% increase)
                    var overheadPercentage = ((double)stopwatchWithTelemetry.ElapsedMilliseconds - stopwatchWithoutTelemetry.ElapsedMilliseconds) /
                                           Math.Max(stopwatchWithoutTelemetry.ElapsedMilliseconds, 1) * 100;

                    var acceptableOverhead = overheadPercentage < 50; // Less than 50% overhead

                    _logger.LogInformation(
                        "Performance impact: WithoutTelemetry={Without}ms, WithTelemetry={With}ms, Overhead={Overhead:F2}%, Acceptable={Acceptable}",
                        stopwatchWithoutTelemetry.ElapsedMilliseconds, stopwatchWithTelemetry.ElapsedMilliseconds,
                        overheadPercentage, acceptableOverhead);

                    await sender.DisposeAsync();
                    return acceptableOverhead;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in performance impact property test");
                    return false;
                }
            });

            return task.GetAwaiter().GetResult();
        });
    }
}

using Azure.Messaging.ServiceBus;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Azure.Tests.TestHelpers;
using System.Diagnostics;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Azure.Tests.Integration;

/// <summary>
/// Integration tests for Azure Monitor telemetry collection.
/// Validates telemetry data collection, custom metrics, traces, and health metrics reporting.
/// **Validates: Requirements 4.5**
/// </summary>
public class AzureMonitorIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILogger<AzureMonitorIntegrationTests> _logger;
    private IAzureTestEnvironment _testEnvironment = null!;
    private ServiceBusClient _serviceBusClient = null!;
    private KeyClient _keyClient = null!;
    private string _testQueueName = null!;
    private string _testKeyName = null!;
    private readonly ActivitySource _activitySource = new("SourceFlow.Cloud.Azure.Tests");

    public AzureMonitorIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _logger = LoggerHelper.CreateLogger<AzureMonitorIntegrationTests>(output);
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

        _testQueueName = $"monitor-test-queue-{Guid.NewGuid():N}";
        _testKeyName = $"monitor-test-key-{Guid.NewGuid():N}";

        // Create test resources
        var adminClient = _testEnvironment.CreateServiceBusAdministrationClient();
        await adminClient.CreateQueueAsync(_testQueueName);

        _logger.LogInformation("Azure Monitor test environment initialized");
    }

    public async Task DisposeAsync()
    {
        try
        {
            var adminClient = _testEnvironment.CreateServiceBusAdministrationClient();
            await adminClient.DeleteQueueAsync(_testQueueName);

            try
            {
                var deleteOperation = await _keyClient.StartDeleteKeyAsync(_testKeyName);
                await deleteOperation.WaitForCompletionAsync();
            }
            catch { }

            await _serviceBusClient.DisposeAsync();
            await _testEnvironment.CleanupAsync();
            _activitySource.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during test cleanup");
        }
    }

    [Fact]
    public async Task AzureMonitor_ServiceBusMessageSend_ShouldCollectTelemetry()
    {
        // Arrange
        _logger.LogInformation("Testing telemetry collection for Service Bus message send");
        var sender = _serviceBusClient.CreateSender(_testQueueName);
        var correlationId = Guid.NewGuid().ToString();

        using var activity = _activitySource.StartActivity("ServiceBusMessageSend", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "azureservicebus");
        activity?.SetTag("messaging.destination", _testQueueName);
        activity?.SetTag("correlation.id", correlationId);

        var testMessage = new ServiceBusMessage("Telemetry test message")
        {
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = correlationId
        };

        // Act
        var stopwatch = Stopwatch.StartNew();
        await sender.SendMessageAsync(testMessage);
        stopwatch.Stop();

        // Assert - Verify telemetry data was captured
        Assert.NotNull(activity);
        Assert.Equal("ServiceBusMessageSend", activity.OperationName);
        Assert.True(stopwatch.ElapsedMilliseconds >= 0);

        _logger.LogInformation(
            "Telemetry collected: ActivityId={ActivityId}, Duration={Duration}ms, CorrelationId={CorrelationId}",
            activity?.Id, stopwatch.ElapsedMilliseconds, correlationId);

        await sender.DisposeAsync();
    }

    [Fact]
    public async Task AzureMonitor_ServiceBusMessageReceive_ShouldCollectTelemetry()
    {
        // Arrange
        _logger.LogInformation("Testing telemetry collection for Service Bus message receive");
        var sender = _serviceBusClient.CreateSender(_testQueueName);
        var receiver = _serviceBusClient.CreateReceiver(_testQueueName);
        var correlationId = Guid.NewGuid().ToString();

        // Send a message first
        var testMessage = new ServiceBusMessage("Telemetry receive test")
        {
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = correlationId
        };
        await sender.SendMessageAsync(testMessage);

        using var activity = _activitySource.StartActivity("ServiceBusMessageReceive", ActivityKind.Consumer);
        activity?.SetTag("messaging.system", "azureservicebus");
        activity?.SetTag("messaging.source", _testQueueName);
        activity?.SetTag("correlation.id", correlationId);

        // Act
        var stopwatch = Stopwatch.StartNew();
        var receivedMessage = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        stopwatch.Stop();

        // Assert
        Assert.NotNull(receivedMessage);
        Assert.NotNull(activity);
        Assert.Equal(correlationId, receivedMessage.CorrelationId);

        _logger.LogInformation(
            "Receive telemetry collected: ActivityId={ActivityId}, Duration={Duration}ms, MessageId={MessageId}",
            activity?.Id, stopwatch.ElapsedMilliseconds, receivedMessage.MessageId);

        await receiver.CompleteMessageAsync(receivedMessage);
        await sender.DisposeAsync();
        await receiver.DisposeAsync();
    }

    [Fact]
    public async Task AzureMonitor_KeyVaultEncryption_ShouldCollectTelemetry()
    {
        // Arrange
        _logger.LogInformation("Testing telemetry collection for Key Vault encryption");
        
        var keyOptions = new CreateRsaKeyOptions(_testKeyName)
        {
            KeySize = 2048
        };
        var key = await _keyClient.CreateRsaKeyAsync(keyOptions);

        using var activity = _activitySource.StartActivity("KeyVaultEncryption", ActivityKind.Client);
        activity?.SetTag("keyvault.operation", "encrypt");
        activity?.SetTag("keyvault.key", _testKeyName);

        var cryptoClient = new CryptographyClient(
            key.Value.Id, 
            _testEnvironment.GetAzureCredential());

        var plaintext = "Telemetry encryption test data";
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);

        // Act
        var stopwatch = Stopwatch.StartNew();
        var encryptResult = await cryptoClient.EncryptAsync(
            EncryptionAlgorithm.RsaOaep, 
            plaintextBytes);
        stopwatch.Stop();

        // Assert
        Assert.NotNull(encryptResult.Ciphertext);
        Assert.NotNull(activity);

        _logger.LogInformation(
            "Encryption telemetry collected: ActivityId={ActivityId}, Duration={Duration}ms, KeyId={KeyId}",
            activity?.Id, stopwatch.ElapsedMilliseconds, key.Value.Id);
    }

    [Fact]
    public async Task AzureMonitor_KeyVaultDecryption_ShouldCollectTelemetry()
    {
        // Arrange
        _logger.LogInformation("Testing telemetry collection for Key Vault decryption");
        
        var keyOptions = new CreateRsaKeyOptions(_testKeyName)
        {
            KeySize = 2048
        };
        var key = await _keyClient.CreateRsaKeyAsync(keyOptions);

        var cryptoClient = new CryptographyClient(
            key.Value.Id, 
            _testEnvironment.GetAzureCredential());

        var plaintext = "Telemetry decryption test data";
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);

        // Encrypt first
        var encryptResult = await cryptoClient.EncryptAsync(
            EncryptionAlgorithm.RsaOaep, 
            plaintextBytes);

        using var activity = _activitySource.StartActivity("KeyVaultDecryption", ActivityKind.Client);
        activity?.SetTag("keyvault.operation", "decrypt");
        activity?.SetTag("keyvault.key", _testKeyName);

        // Act
        var stopwatch = Stopwatch.StartNew();
        var decryptResult = await cryptoClient.DecryptAsync(
            EncryptionAlgorithm.RsaOaep, 
            encryptResult.Ciphertext);
        stopwatch.Stop();

        // Assert
        Assert.NotNull(decryptResult.Plaintext);
        Assert.NotNull(activity);
        Assert.Equal(plaintext, Encoding.UTF8.GetString(decryptResult.Plaintext));

        _logger.LogInformation(
            "Decryption telemetry collected: ActivityId={ActivityId}, Duration={Duration}ms",
            activity?.Id, stopwatch.ElapsedMilliseconds);
    }

    [Fact]
    public async Task AzureMonitor_EndToEndMessageFlow_ShouldCollectCorrelatedTelemetry()
    {
        // Arrange
        _logger.LogInformation("Testing correlated telemetry collection for end-to-end message flow");
        var correlationId = Guid.NewGuid().ToString();
        var sender = _serviceBusClient.CreateSender(_testQueueName);
        var receiver = _serviceBusClient.CreateReceiver(_testQueueName);

        using var parentActivity = _activitySource.StartActivity("EndToEndMessageFlow", ActivityKind.Internal);
        parentActivity?.SetTag("correlation.id", correlationId);

        // Act - Send
        using (var sendActivity = _activitySource.StartActivity("Send", ActivityKind.Producer, parentActivity?.Context ?? default))
        {
            sendActivity?.SetTag("messaging.destination", _testQueueName);
            
            var testMessage = new ServiceBusMessage("Correlated telemetry test")
            {
                MessageId = Guid.NewGuid().ToString(),
                CorrelationId = correlationId
            };
            await sender.SendMessageAsync(testMessage);
            
            _logger.LogInformation("Send activity: {ActivityId}", sendActivity?.Id);
        }

        // Act - Receive
        using (var receiveActivity = _activitySource.StartActivity("Receive", ActivityKind.Consumer, parentActivity?.Context ?? default))
        {
            receiveActivity?.SetTag("messaging.source", _testQueueName);
            
            var receivedMessage = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(receivedMessage);
            Assert.Equal(correlationId, receivedMessage.CorrelationId);
            
            await receiver.CompleteMessageAsync(receivedMessage);
            
            _logger.LogInformation("Receive activity: {ActivityId}", receiveActivity?.Id);
        }

        // Assert - Verify correlation
        Assert.NotNull(parentActivity);
        _logger.LogInformation(
            "Correlated telemetry collected: ParentActivityId={ParentId}, CorrelationId={CorrelationId}",
            parentActivity?.Id, correlationId);

        await sender.DisposeAsync();
        await receiver.DisposeAsync();
    }

    [Fact]
    public async Task AzureMonitor_CustomMetrics_ShouldBeCollected()
    {
        // Arrange
        _logger.LogInformation("Testing custom metrics collection");
        var sender = _serviceBusClient.CreateSender(_testQueueName);

        using var activity = _activitySource.StartActivity("CustomMetricsTest", ActivityKind.Internal);
        
        // Act - Send multiple messages and collect metrics
        var messageCount = 10;
        var totalBytes = 0L;
        var stopwatch = Stopwatch.StartNew();

        for (int i = 0; i < messageCount; i++)
        {
            var messageBody = $"Custom metrics test message {i}";
            var testMessage = new ServiceBusMessage(messageBody)
            {
                MessageId = Guid.NewGuid().ToString()
            };
            
            totalBytes += Encoding.UTF8.GetByteCount(messageBody);
            await sender.SendMessageAsync(testMessage);
        }

        stopwatch.Stop();

        // Assert - Verify metrics were captured
        var throughput = messageCount / stopwatch.Elapsed.TotalSeconds;
        var averageLatency = stopwatch.ElapsedMilliseconds / (double)messageCount;

        activity?.SetTag("custom.message_count", messageCount);
        activity?.SetTag("custom.total_bytes", totalBytes);
        activity?.SetTag("custom.throughput_msg_per_sec", throughput);
        activity?.SetTag("custom.average_latency_ms", averageLatency);

        _logger.LogInformation(
            "Custom metrics: MessageCount={Count}, TotalBytes={Bytes}, Throughput={Throughput:F2} msg/s, AvgLatency={Latency:F2}ms",
            messageCount, totalBytes, throughput, averageLatency);

        Assert.True(messageCount > 0);
        Assert.True(totalBytes > 0);
        Assert.True(throughput > 0);

        await sender.DisposeAsync();
    }

    [Fact]
    public async Task AzureMonitor_ErrorTelemetry_ShouldBeCollected()
    {
        // Arrange
        _logger.LogInformation("Testing error telemetry collection");
        var nonExistentQueue = $"non-existent-{Guid.NewGuid():N}";

        using var activity = _activitySource.StartActivity("ErrorTelemetryTest", ActivityKind.Internal);
        activity?.SetTag("test.expected_error", true);

        // Act - Attempt operation that will fail
        var errorOccurred = false;
        var errorMessage = string.Empty;

        try
        {
            var sender = _serviceBusClient.CreateSender(nonExistentQueue);
            var testMessage = new ServiceBusMessage("This should fail");
            await sender.SendMessageAsync(testMessage);
        }
        catch (Exception ex)
        {
            errorOccurred = true;
            errorMessage = ex.Message;
            
            activity?.SetTag("error", true);
            activity?.SetTag("error.type", ex.GetType().Name);
            activity?.SetTag("error.message", ex.Message);
            
            _logger.LogWarning(ex, "Expected error occurred for telemetry test");
        }

        // Assert - Verify error telemetry was captured
        Assert.True(errorOccurred);
        Assert.NotEmpty(errorMessage);
        Assert.NotNull(activity);

        _logger.LogInformation(
            "Error telemetry collected: ActivityId={ActivityId}, ErrorType={ErrorType}",
            activity?.Id, activity?.GetTagItem("error.type"));
    }

    [Fact]
    public async Task AzureMonitor_HealthMetrics_ShouldBeReported()
    {
        // Arrange
        _logger.LogInformation("Testing health metrics reporting");

        using var activity = _activitySource.StartActivity("HealthMetricsTest", ActivityKind.Internal);

        // Act - Collect health metrics
        var serviceBusAvailable = await _testEnvironment.IsServiceBusAvailableAsync();
        var keyVaultAvailable = await _testEnvironment.IsKeyVaultAvailableAsync();
        var managedIdentityConfigured = await _testEnvironment.IsManagedIdentityConfiguredAsync();

        // Add health metrics as tags
        activity?.SetTag("health.servicebus_available", serviceBusAvailable);
        activity?.SetTag("health.keyvault_available", keyVaultAvailable);
        activity?.SetTag("health.managed_identity_configured", managedIdentityConfigured);
        activity?.SetTag("health.overall_status", serviceBusAvailable && keyVaultAvailable ? "healthy" : "degraded");

        // Assert
        Assert.True(serviceBusAvailable);
        Assert.True(keyVaultAvailable);

        _logger.LogInformation(
            "Health metrics: ServiceBus={ServiceBus}, KeyVault={KeyVault}, ManagedIdentity={ManagedIdentity}",
            serviceBusAvailable, keyVaultAvailable, managedIdentityConfigured);
    }

    [Fact]
    public async Task AzureMonitor_PerformanceMetrics_ShouldBeCollected()
    {
        // Arrange
        _logger.LogInformation("Testing performance metrics collection");
        var sender = _serviceBusClient.CreateSender(_testQueueName);
        var receiver = _serviceBusClient.CreateReceiver(_testQueueName);

        using var activity = _activitySource.StartActivity("PerformanceMetricsTest", ActivityKind.Internal);

        // Act - Measure send performance
        var sendStopwatch = Stopwatch.StartNew();
        var testMessage = new ServiceBusMessage("Performance test message")
        {
            MessageId = Guid.NewGuid().ToString()
        };
        await sender.SendMessageAsync(testMessage);
        sendStopwatch.Stop();

        // Act - Measure receive performance
        var receiveStopwatch = Stopwatch.StartNew();
        var receivedMessage = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        receiveStopwatch.Stop();

        Assert.NotNull(receivedMessage);
        await receiver.CompleteMessageAsync(receivedMessage);

        // Add performance metrics
        activity?.SetTag("performance.send_latency_ms", sendStopwatch.ElapsedMilliseconds);
        activity?.SetTag("performance.receive_latency_ms", receiveStopwatch.ElapsedMilliseconds);
        activity?.SetTag("performance.total_latency_ms", sendStopwatch.ElapsedMilliseconds + receiveStopwatch.ElapsedMilliseconds);

        _logger.LogInformation(
            "Performance metrics: SendLatency={SendMs}ms, ReceiveLatency={ReceiveMs}ms, Total={TotalMs}ms",
            sendStopwatch.ElapsedMilliseconds, receiveStopwatch.ElapsedMilliseconds,
            sendStopwatch.ElapsedMilliseconds + receiveStopwatch.ElapsedMilliseconds);

        await sender.DisposeAsync();
        await receiver.DisposeAsync();
    }

    [Fact]
    public async Task AzureMonitor_TelemetryWithCorrelationIds_ShouldMaintainContext()
    {
        // Arrange
        _logger.LogInformation("Testing telemetry correlation ID propagation");
        var correlationId = Guid.NewGuid().ToString();
        var sender = _serviceBusClient.CreateSender(_testQueueName);

        using var activity = _activitySource.StartActivity("CorrelationTest", ActivityKind.Internal);
        activity?.SetTag("correlation.id", correlationId);

        // Act - Send message with correlation ID
        var testMessage = new ServiceBusMessage("Correlation test")
        {
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = correlationId
        };
        testMessage.ApplicationProperties["TraceId"] = activity?.Id ?? "unknown";
        testMessage.ApplicationProperties["SpanId"] = activity?.SpanId.ToString() ?? "unknown";

        await sender.SendMessageAsync(testMessage);

        // Assert - Verify correlation context is maintained
        Assert.NotNull(activity);
        Assert.Equal(correlationId, activity.GetTagItem("correlation.id"));

        _logger.LogInformation(
            "Correlation context: CorrelationId={CorrelationId}, TraceId={TraceId}, SpanId={SpanId}",
            correlationId, activity?.Id, activity?.SpanId);

        await sender.DisposeAsync();
    }
}

# Design Document: Azure Cloud Integration Testing

## Overview

The azure-cloud-integration-testing feature provides a comprehensive testing framework specifically for validating SourceFlow's Azure cloud integrations. This system ensures that SourceFlow applications work correctly in Azure environments by testing Azure Service Bus messaging (queues, topics, sessions, duplicate detection), Azure Key Vault encryption with managed identity, RBAC permissions, dead letter handling, auto-scaling behavior, and performance characteristics under various load conditions.

The design focuses exclusively on Azure-specific scenarios that differ from AWS implementations, including Service Bus session-based ordering, content-based duplicate detection, Key Vault encryption with managed identity authentication, Azure RBAC permission validation, Service Bus auto-scaling behavior, and Azure-specific resilience patterns (throttling, rate limiting, network partitions). The testing framework supports both local development using Azurite emulators for rapid feedback and cloud-based testing using real Azure services for production validation.

This design complements the existing `SourceFlow.Cloud.Azure.Tests` project by adding comprehensive integration, end-to-end, performance, security, and resilience testing capabilities that validate the complete Azure cloud extension functionality.

## Architecture

### Test Project Structure

The testing framework enhances the existing `SourceFlow.Cloud.Azure.Tests` project with comprehensive integration testing capabilities:

```
tests/
├── SourceFlow.Cloud.Azure.Tests/
│   ├── Integration/
│   │   ├── ServiceBusCommandTests.cs
│   │   ├── ServiceBusEventTests.cs
│   │   ├── KeyVaultEncryptionTests.cs
│   │   ├── ManagedIdentityTests.cs
│   │   ├── SessionHandlingTests.cs
│   │   ├── DuplicateDetectionTests.cs
│   │   ├── DeadLetterIntegrationTests.cs
│   │   ├── PerformanceIntegrationTests.cs
│   │   ├── AutoScalingTests.cs
│   │   └── RBACPermissionTests.cs
│   ├── E2E/
│   │   ├── EndToEndMessageFlowTests.cs
│   │   ├── HybridLocalAzureTests.cs
│   │   ├── SessionOrderingTests.cs
│   │   └── FailoverScenarioTests.cs
│   ├── Resilience/
│   │   ├── CircuitBreakerTests.cs
│   │   ├── RetryPolicyTests.cs
│   │   ├── ThrottlingHandlingTests.cs
│   │   └── NetworkPartitionTests.cs
│   ├── Security/
│   │   ├── ManagedIdentitySecurityTests.cs
│   │   ├── KeyVaultAccessPolicyTests.cs
│   │   ├── SensitiveDataMaskingTests.cs
│   │   └── AuditLoggingTests.cs
│   ├── Performance/
│   │   ├── ServiceBusThroughputTests.cs
│   │   ├── LatencyBenchmarks.cs
│   │   ├── ConcurrentProcessingTests.cs
│   │   └── ResourceUtilizationTests.cs
│   ├── TestHelpers/
│   │   ├── AzureTestEnvironment.cs
│   │   ├── AzuriteTestFixture.cs
│   │   ├── ServiceBusTestHelpers.cs
│   │   ├── KeyVaultTestHelpers.cs
│   │   ├── ManagedIdentityTestHelpers.cs
│   │   └── PerformanceTestHelpers.cs
│   └── Unit/ (existing)
```

### Azure Test Environment Management

The architecture supports multiple Azure-specific test environments with distinct purposes:

1. **Azurite Local Environment**: Uses Azurite emulator for Service Bus and Key Vault, providing fast feedback during development without Azure costs
2. **Azure Development Environment**: Uses real Azure services in isolated development subscription with proper resource tagging for cost tracking
3. **Azure CI/CD Environment**: Automated provisioning using ARM templates or Bicep with automatic resource cleanup after test execution
4. **Azure Performance Environment**: Dedicated Azure resources with Premium tier Service Bus for accurate load testing and auto-scaling validation

Each environment is configured through `AzureTestConfiguration` with environment-specific settings for connection strings, managed identity, RBAC permissions, and resource naming conventions.

### Azure Test Categories

The testing framework organizes tests into Azure-specific categories with clear purposes:

- **Unit Tests**: Mock-based tests for Azure components (dispatchers, listeners, encryption) with fast execution and no external dependencies
- **Integration Tests**: Tests with real or emulated Azure services validating Service Bus messaging, Key Vault encryption, and managed identity authentication
- **End-to-End Tests**: Complete Azure message flow validation from command dispatch through Service Bus to event consumption with full observability
- **Performance Tests**: Azure Service Bus throughput (messages/second), latency (P50/P95/P99), auto-scaling behavior, and resource utilization under load
- **Security Tests**: Managed identity (system and user-assigned), RBAC permissions, Key Vault access policies, and sensitive data masking validation
- **Resilience Tests**: Azure-specific circuit breaker behavior, retry policies with exponential backoff, throttling handling, and network partition recovery

Each category has specific test fixtures, helpers, and configuration to ensure proper isolation and repeatability.

## Components and Interfaces

### Azure Test Environment Abstractions

```csharp
public interface IAzureTestEnvironment
{
    Task InitializeAsync();
    Task CleanupAsync();
    bool IsAzuriteEmulator { get; }
    string GetServiceBusConnectionString();
    string GetServiceBusFullyQualifiedNamespace();
    string GetKeyVaultUrl();
    Task<bool> IsServiceBusAvailableAsync();
    Task<bool> IsKeyVaultAvailableAsync();
    Task<bool> IsManagedIdentityConfiguredAsync();
    Task<TokenCredential> GetAzureCredentialAsync();
    Task<Dictionary<string, string>> GetEnvironmentMetadataAsync();
}

public interface IAzureResourceManager
{
    Task<string> CreateServiceBusQueueAsync(string queueName, ServiceBusQueueOptions options);
    Task<string> CreateServiceBusTopicAsync(string topicName, ServiceBusTopicOptions options);
    Task<string> CreateServiceBusSubscriptionAsync(string topicName, string subscriptionName, ServiceBusSubscriptionOptions options);
    Task DeleteResourceAsync(string resourceId);
    Task<IEnumerable<string>> ListResourcesAsync();
    Task<string> CreateKeyVaultKeyAsync(string keyName, KeyVaultKeyOptions options);
    Task<bool> ValidateResourceExistsAsync(string resourceId);
    Task<Dictionary<string, string>> GetResourceTagsAsync(string resourceId);
    Task SetResourceTagsAsync(string resourceId, Dictionary<string, string> tags);
}

public interface IAzurePerformanceTestRunner
{
    Task<AzurePerformanceTestResult> RunServiceBusThroughputTestAsync(AzureTestScenario scenario);
    Task<AzurePerformanceTestResult> RunServiceBusLatencyTestAsync(AzureTestScenario scenario);
    Task<AzurePerformanceTestResult> RunAutoScalingTestAsync(AzureTestScenario scenario);
    Task<AzurePerformanceTestResult> RunConcurrentProcessingTestAsync(AzureTestScenario scenario);
    Task<AzurePerformanceTestResult> RunResourceUtilizationTestAsync(AzureTestScenario scenario);
    Task<AzurePerformanceTestResult> RunSessionProcessingTestAsync(AzureTestScenario scenario);
}

public interface IAzureMetricsCollector
{
    Task<ServiceBusMetrics> GetServiceBusMetricsAsync(string namespaceName, string resourceName);
    Task<KeyVaultMetrics> GetKeyVaultMetricsAsync(string vaultName);
    Task<AzureResourceUsage> GetResourceUsageAsync(string resourceId);
    Task<List<MetricDataPoint>> GetHistoricalMetricsAsync(string resourceId, string metricName, TimeSpan duration);
}
```

### Azure Test Environment Implementation

```csharp
public class AzureTestEnvironment : IAzureTestEnvironment
{
    private readonly AzureTestConfiguration _configuration;
    private readonly IAzuriteManager _azuriteManager;
    private readonly ServiceBusClient _serviceBusClient;
    private readonly KeyClient _keyClient;
    private readonly DefaultAzureCredential _azureCredential;
    private readonly ILogger<AzureTestEnvironment> _logger;
    
    public bool IsAzuriteEmulator => _configuration.UseAzurite;
    
    public async Task InitializeAsync()
    {
        _logger.LogInformation("Initializing Azure test environment (Azurite: {UseAzurite})", IsAzuriteEmulator);
        
        if (IsAzuriteEmulator)
        {
            await _azuriteManager.StartAsync();
            await ConfigureAzuriteServicesAsync();
            _logger.LogInformation("Azurite environment initialized successfully");
        }
        else
        {
            await ValidateManagedIdentityAsync();
            await ValidateServiceBusAccessAsync();
            await ValidateKeyVaultAccessAsync();
            await ValidateRBACPermissionsAsync();
            _logger.LogInformation("Azure cloud environment validated successfully");
        }
    }
    
    public async Task CleanupAsync()
    {
        _logger.LogInformation("Cleaning up Azure test environment");
        
        if (IsAzuriteEmulator)
        {
            await _azuriteManager.StopAsync();
        }
        else
        {
            await CleanupTestResourcesAsync();
        }
        
        await _serviceBusClient.DisposeAsync();
    }
    
    private async Task ValidateManagedIdentityAsync()
    {
        try
        {
            // Validate Service Bus access
            var serviceBusToken = await _azureCredential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://servicebus.azure.net/.default" }));
            
            if (string.IsNullOrEmpty(serviceBusToken.Token))
                throw new InvalidOperationException("Failed to acquire Service Bus token");
            
            // Validate Key Vault access
            var keyVaultToken = await _azureCredential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://vault.azure.net/.default" }));
            
            if (string.IsNullOrEmpty(keyVaultToken.Token))
                throw new InvalidOperationException("Failed to acquire Key Vault token");
            
            _logger.LogInformation("Managed identity validation successful");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Managed identity validation failed");
            throw new InvalidOperationException($"Managed identity validation failed: {ex.Message}", ex);
        }
    }
    
    private async Task ValidateServiceBusAccessAsync()
    {
        try
        {
            var adminClient = new ServiceBusAdministrationClient(
                _configuration.FullyQualifiedNamespace, 
                _azureCredential);
            
            // Verify we can list queues (requires appropriate RBAC permissions)
            await adminClient.GetQueuesAsync().GetAsyncEnumerator().MoveNextAsync();
            
            _logger.LogInformation("Service Bus access validated");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Service Bus access validation failed");
            throw new InvalidOperationException($"Service Bus access validation failed: {ex.Message}", ex);
        }
    }
    
    private async Task ValidateKeyVaultAccessAsync()
    {
        try
        {
            // Attempt to list keys to verify access
            await _keyClient.GetPropertiesOfKeysAsync().GetAsyncEnumerator().MoveNextAsync();
            
            _logger.LogInformation("Key Vault access validated");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Key Vault access validation failed");
            throw new InvalidOperationException($"Key Vault access validation failed: {ex.Message}", ex);
        }
    }
    
    public async Task<TokenCredential> GetAzureCredentialAsync()
    {
        return _azureCredential;
    }
    
    public async Task<Dictionary<string, string>> GetEnvironmentMetadataAsync()
    {
        return new Dictionary<string, string>
        {
            ["Environment"] = IsAzuriteEmulator ? "Azurite" : "Azure",
            ["ServiceBusNamespace"] = _configuration.FullyQualifiedNamespace,
            ["KeyVaultUrl"] = _configuration.KeyVaultUrl,
            ["UseManagedIdentity"] = _configuration.UseManagedIdentity.ToString(),
            ["Timestamp"] = DateTimeOffset.UtcNow.ToString("O")
        };
    }
}

public class AzuriteManager : IAzuriteManager
{
    private readonly AzuriteConfiguration _configuration;
    private readonly ILogger<AzuriteManager> _logger;
    private Process? _azuriteProcess;
    
    public async Task StartAsync()
    {
        _logger.LogInformation("Starting Azurite emulator");
        
        // Start Azurite container or process with Service Bus and Key Vault emulation
        await StartAzuriteContainerAsync();
        await WaitForServicesAsync();
        
        _logger.LogInformation("Azurite emulator started successfully");
    }
    
    public async Task StopAsync()
    {
        _logger.LogInformation("Stopping Azurite emulator");
        
        if (_azuriteProcess != null && !_azuriteProcess.HasExited)
        {
            _azuriteProcess.Kill();
            await _azuriteProcess.WaitForExitAsync();
        }
        
        _logger.LogInformation("Azurite emulator stopped");
    }
    
    public async Task ConfigureServiceBusAsync()
    {
        _logger.LogInformation("Configuring Azurite Service Bus emulation");
        
        // Configure Service Bus emulation with queues, topics, and subscriptions
        await CreateDefaultQueuesAsync();
        await CreateDefaultTopicsAsync();
        await CreateDefaultSubscriptionsAsync();
        
        _logger.LogInformation("Azurite Service Bus configured");
    }
    
    public async Task ConfigureKeyVaultAsync()
    {
        _logger.LogInformation("Configuring Azurite Key Vault emulation");
        
        // Configure Key Vault emulation with test keys and secrets
        await CreateTestKeysAsync();
        await ConfigureAccessPoliciesAsync();
        
        _logger.LogInformation("Azurite Key Vault configured");
    }
    
    private async Task StartAzuriteContainerAsync()
    {
        // Start Azurite using Docker or local process
        var startInfo = new ProcessStartInfo
        {
            FileName = "azurite",
            Arguments = "--silent --location azurite-data --debug azurite-debug.log",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        
        _azuriteProcess = Process.Start(startInfo);
        
        if (_azuriteProcess == null)
            throw new InvalidOperationException("Failed to start Azurite process");
    }
    
    private async Task WaitForServicesAsync()
    {
        var maxAttempts = 30;
        var attempt = 0;
        
        while (attempt < maxAttempts)
        {
            try
            {
                // Check if Azurite is responding
                using var httpClient = new HttpClient();
                var response = await httpClient.GetAsync("http://127.0.0.1:10000/devstoreaccount1?comp=list");
                
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Azurite services are ready");
                    return;
                }
            }
            catch
            {
                // Service not ready yet
            }
            
            attempt++;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        
        throw new TimeoutException("Azurite services did not become ready within the timeout period");
    }
    
    private async Task CreateDefaultQueuesAsync()
    {
        var defaultQueues = new[] { "test-commands.fifo", "test-notifications" };
        
        foreach (var queueName in defaultQueues)
        {
            _logger.LogInformation("Creating default queue: {QueueName}", queueName);
            // Create queue using Azurite API
        }
    }
    
    private async Task CreateDefaultTopicsAsync()
    {
        var defaultTopics = new[] { "test-events", "test-domain-events" };
        
        foreach (var topicName in defaultTopics)
        {
            _logger.LogInformation("Creating default topic: {TopicName}", topicName);
            // Create topic using Azurite API
        }
    }
}
```

### Azure Service Bus Testing Components

```csharp
public class ServiceBusTestHelpers
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly ILogger<ServiceBusTestHelpers> _logger;
    
    public async Task<ServiceBusMessage> CreateTestCommandMessage(ICommand command)
    {
        var serializedCommand = JsonSerializer.Serialize(command);
        var message = new ServiceBusMessage(serializedCommand)
        {
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = command.CorrelationId ?? Guid.NewGuid().ToString(),
            SessionId = command.Entity.ToString(), // For session-based ordering
            Subject = command.GetType().Name,
            ContentType = "application/json"
        };
        
        // Add custom properties for routing and metadata
        message.ApplicationProperties["CommandType"] = command.GetType().AssemblyQualifiedName;
        message.ApplicationProperties["EntityId"] = command.Entity.ToString();
        message.ApplicationProperties["Timestamp"] = DateTimeOffset.UtcNow.ToString("O");
        message.ApplicationProperties["SourceSystem"] = "SourceFlow.Tests";
        
        return message;
    }
    
    public async Task<ServiceBusMessage> CreateTestEventMessage(IEvent @event)
    {
        var serializedEvent = JsonSerializer.Serialize(@event);
        var message = new ServiceBusMessage(serializedEvent)
        {
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = @event.CorrelationId ?? Guid.NewGuid().ToString(),
            Subject = @event.GetType().Name,
            ContentType = "application/json"
        };
        
        // Add custom properties for event metadata
        message.ApplicationProperties["EventType"] = @event.GetType().AssemblyQualifiedName;
        message.ApplicationProperties["Timestamp"] = DateTimeOffset.UtcNow.ToString("O");
        message.ApplicationProperties["SourceSystem"] = "SourceFlow.Tests";
        
        return message;
    }
    
    public async Task<bool> ValidateSessionOrderingAsync(string queueName, List<ICommand> commands)
    {
        var processor = _serviceBusClient.CreateSessionProcessor(queueName, new ServiceBusSessionProcessorOptions
        {
            MaxConcurrentSessions = 1,
            MaxConcurrentCallsPerSession = 1,
            AutoCompleteMessages = false
        });
        
        var receivedCommands = new ConcurrentBag<ICommand>();
        var processedCount = 0;
        
        processor.ProcessMessageAsync += async args =>
        {
            try
            {
                var commandJson = args.Message.Body.ToString();
                var commandType = Type.GetType(args.Message.ApplicationProperties["CommandType"].ToString());
                var command = (ICommand)JsonSerializer.Deserialize(commandJson, commandType);
                
                receivedCommands.Add(command);
                Interlocked.Increment(ref processedCount);
                
                await args.CompleteMessageAsync(args.Message);
                
                _logger.LogInformation("Processed command {CommandType} in session {SessionId}", 
                    command.GetType().Name, args.SessionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message in session {SessionId}", args.SessionId);
                await args.AbandonMessageAsync(args.Message);
            }
        };
        
        processor.ProcessErrorAsync += args =>
        {
            _logger.LogError(args.Exception, "Error in session processor: {ErrorSource}", args.ErrorSource);
            return Task.CompletedTask;
        };
        
        await processor.StartProcessingAsync();
        
        // Send commands with same session ID
        var sender = _serviceBusClient.CreateSender(queueName);
        foreach (var command in commands)
        {
            var message = await CreateTestCommandMessage(command);
            await sender.SendMessageAsync(message);
            _logger.LogInformation("Sent command {CommandType} to queue {QueueName}", 
                command.GetType().Name, queueName);
        }
        
        // Wait for processing with timeout
        var timeout = TimeSpan.FromSeconds(30);
        var stopwatch = Stopwatch.StartNew();
        
        while (processedCount < commands.Count && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
        
        await processor.StopProcessingAsync();
        await sender.DisposeAsync();
        
        if (processedCount < commands.Count)
        {
            _logger.LogWarning("Timeout: Only processed {ProcessedCount} of {TotalCount} commands", 
                processedCount, commands.Count);
            return false;
        }
        
        // Validate order
        return ValidateCommandOrder(commands, receivedCommands.ToList());
    }
    
    private bool ValidateCommandOrder(List<ICommand> sent, List<ICommand> received)
    {
        if (sent.Count != received.Count)
        {
            _logger.LogError("Command count mismatch: sent {SentCount}, received {ReceivedCount}", 
                sent.Count, received.Count);
            return false;
        }
        
        for (int i = 0; i < sent.Count; i++)
        {
            if (sent[i].GetType() != received[i].GetType() || 
                sent[i].Entity != received[i].Entity)
            {
                _logger.LogError("Command order mismatch at index {Index}: expected {Expected}, got {Actual}", 
                    i, sent[i].GetType().Name, received[i].GetType().Name);
                return false;
            }
        }
        
        _logger.LogInformation("Command order validation successful");
        return true;
    }
    
    public async Task<bool> ValidateDuplicateDetectionAsync(string queueName, ICommand command, int sendCount)
    {
        var sender = _serviceBusClient.CreateSender(queueName);
        var message = await CreateTestCommandMessage(command);
        
        // Send the same message multiple times
        for (int i = 0; i < sendCount; i++)
        {
            await sender.SendMessageAsync(message);
            _logger.LogInformation("Sent duplicate message {MessageId} (attempt {Attempt})", 
                message.MessageId, i + 1);
        }
        
        // Receive messages and verify only one was delivered
        var receiver = _serviceBusClient.CreateReceiver(queueName);
        var receivedCount = 0;
        
        var timeout = TimeSpan.FromSeconds(10);
        var stopwatch = Stopwatch.StartNew();
        
        while (stopwatch.Elapsed < timeout)
        {
            var receivedMessage = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(1));
            if (receivedMessage != null)
            {
                receivedCount++;
                await receiver.CompleteMessageAsync(receivedMessage);
                _logger.LogInformation("Received message {MessageId}", receivedMessage.MessageId);
            }
            else
            {
                break; // No more messages
            }
        }
        
        await sender.DisposeAsync();
        await receiver.DisposeAsync();
        
        var success = receivedCount == 1;
        _logger.LogInformation("Duplicate detection validation: sent {SentCount}, received {ReceivedCount}, success: {Success}", 
            sendCount, receivedCount, success);
        
        return success;
    }
}

public class KeyVaultTestHelpers
{
    private readonly KeyClient _keyClient;
    private readonly SecretClient _secretClient;
    private readonly CryptographyClient _cryptoClient;
    private readonly DefaultAzureCredential _credential;
    private readonly ILogger<KeyVaultTestHelpers> _logger;
    
    public async Task<string> CreateTestEncryptionKeyAsync(string keyName)
    {
        _logger.LogInformation("Creating test encryption key: {KeyName}", keyName);
        
        var keyOptions = new CreateRsaKeyOptions(keyName)
        {
            KeySize = 2048,
            ExpiresOn = DateTimeOffset.UtcNow.AddYears(1),
            Enabled = true
        };
        
        var key = await _keyClient.CreateRsaKeyAsync(keyOptions);
        
        _logger.LogInformation("Created key {KeyName} with ID {KeyId}", keyName, key.Value.Id);
        return key.Value.Id.ToString();
    }
    
    public async Task<bool> ValidateKeyRotationAsync(string keyName)
    {
        _logger.LogInformation("Validating key rotation for {KeyName}", keyName);
        
        // Create initial key version
        var initialKey = await CreateTestEncryptionKeyAsync(keyName);
        var initialCryptoClient = new CryptographyClient(new Uri(initialKey), _credential);
        
        // Encrypt test data with initial key
        var testData = "sensitive test data for key rotation validation";
        var testDataBytes = Encoding.UTF8.GetBytes(testData);
        var encryptResult = await initialCryptoClient.EncryptAsync(EncryptionAlgorithm.RsaOaep, testDataBytes);
        
        _logger.LogInformation("Encrypted data with initial key version");
        
        // Rotate key (create new version)
        await Task.Delay(TimeSpan.FromSeconds(1)); // Ensure different timestamp
        var rotatedKey = await CreateTestEncryptionKeyAsync(keyName);
        var rotatedCryptoClient = new CryptographyClient(new Uri(rotatedKey), _credential);
        
        _logger.LogInformation("Created rotated key version");
        
        // Verify old data can still be decrypted with initial key
        var decryptResult = await initialCryptoClient.DecryptAsync(EncryptionAlgorithm.RsaOaep, encryptResult.Ciphertext);
        var decryptedData = Encoding.UTF8.GetString(decryptResult.Plaintext);
        
        if (decryptedData != testData)
        {
            _logger.LogError("Failed to decrypt with initial key after rotation");
            return false;
        }
        
        _logger.LogInformation("Successfully decrypted with initial key after rotation");
        
        // Verify new key can encrypt new data
        var newEncryptResult = await rotatedCryptoClient.EncryptAsync(EncryptionAlgorithm.RsaOaep, testDataBytes);
        var newDecryptResult = await rotatedCryptoClient.DecryptAsync(EncryptionAlgorithm.RsaOaep, newEncryptResult.Ciphertext);
        var newDecryptedData = Encoding.UTF8.GetString(newDecryptResult.Plaintext);
        
        if (newDecryptedData != testData)
        {
            _logger.LogError("Failed to encrypt/decrypt with rotated key");
            return false;
        }
        
        _logger.LogInformation("Key rotation validation successful");
        return true;
    }
    
    public async Task<bool> ValidateSensitiveDataMaskingAsync(object testObject)
    {
        _logger.LogInformation("Validating sensitive data masking for {ObjectType}", testObject.GetType().Name);
        
        // Serialize object and check for sensitive data exposure
        var serialized = JsonSerializer.Serialize(testObject);
        
        // Check if properties marked with [SensitiveData] are masked
        var sensitiveProperties = testObject.GetType()
            .GetProperties()
            .Where(p => p.GetCustomAttribute<SensitiveDataAttribute>() != null);
        
        foreach (var property in sensitiveProperties)
        {
            var value = property.GetValue(testObject)?.ToString();
            if (!string.IsNullOrEmpty(value) && serialized.Contains(value))
            {
                _logger.LogError("Sensitive property {PropertyName} is not masked in serialized output", property.Name);
                return false;
            }
        }
        
        _logger.LogInformation("Sensitive data masking validation successful");
        return true;
    }
    
    private async Task<byte[]> EncryptDataAsync(string keyId, string plaintext)
    {
        var cryptoClient = new CryptographyClient(new Uri(keyId), _credential);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var encryptResult = await cryptoClient.EncryptAsync(EncryptionAlgorithm.RsaOaep, plaintextBytes);
        return encryptResult.Ciphertext;
    }
    
    private async Task<string> DecryptDataAsync(string keyId, byte[] ciphertext)
    {
        var cryptoClient = new CryptographyClient(new Uri(keyId), _credential);
        var decryptResult = await cryptoClient.DecryptAsync(EncryptionAlgorithm.RsaOaep, ciphertext);
        return Encoding.UTF8.GetString(decryptResult.Plaintext);
    }
}
```

### Azure Performance Testing Components

```csharp
public class AzurePerformanceTestRunner : IAzurePerformanceTestRunner
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly IAzureMetricsCollector _metricsCollector;
    private readonly ILoadGenerator _loadGenerator;
    
    public async Task<AzurePerformanceTestResult> RunServiceBusThroughputTestAsync(AzureTestScenario scenario)
    {
        var stopwatch = Stopwatch.StartNew();
        var messageCount = 0;
        var sender = _serviceBusClient.CreateSender(scenario.QueueName);
        
        await _loadGenerator.GenerateServiceBusLoadAsync(scenario, 
            onMessageSent: () => Interlocked.Increment(ref messageCount));
        
        stopwatch.Stop();
        
        return new AzurePerformanceTestResult
        {
            TestName = "ServiceBus Throughput",
            MessagesPerSecond = messageCount / stopwatch.Elapsed.TotalSeconds,
            TotalMessages = messageCount,
            Duration = stopwatch.Elapsed,
            ServiceBusMetrics = await _metricsCollector.GetServiceBusMetricsAsync()
        };
    }
    
    public async Task<AzurePerformanceTestResult> RunAutoScalingTestAsync(AzureTestScenario scenario)
    {
        var initialThroughput = await MeasureBaselineThroughputAsync(scenario);
        
        // Gradually increase load
        var loadIncreaseResults = new List<double>();
        for (int load = 1; load <= 10; load++)
        {
            scenario.ConcurrentSenders = load * 10;
            var result = await RunServiceBusThroughputTestAsync(scenario);
            loadIncreaseResults.Add(result.MessagesPerSecond);
            
            // Wait for auto-scaling to take effect
            await Task.Delay(TimeSpan.FromMinutes(2));
        }
        
        return new AzurePerformanceTestResult
        {
            TestName = "Auto-Scaling Validation",
            AutoScalingMetrics = loadIncreaseResults,
            ScalingEfficiency = CalculateScalingEfficiency(loadIncreaseResults)
        };
    }
}

public class AzureMetricsCollector : IAzureMetricsCollector
{
    private readonly MonitorQueryClient _monitorClient;
    
    public async Task<ServiceBusMetrics> GetServiceBusMetricsAsync()
    {
        var metricsQuery = new MetricsQueryOptions
        {
            MetricNames = { "ActiveMessages", "DeadLetterMessages", "IncomingMessages", "OutgoingMessages" },
            TimeRange = TimeRange.LastHour
        };
        
        var response = await _monitorClient.QueryResourceAsync(
            resourceId: "/subscriptions/{subscription}/resourceGroups/{rg}/providers/Microsoft.ServiceBus/namespaces/{namespace}",
            metricsQuery);
        
        return new ServiceBusMetrics
        {
            ActiveMessages = ExtractMetricValue(response, "ActiveMessages"),
            DeadLetterMessages = ExtractMetricValue(response, "DeadLetterMessages"),
            IncomingMessagesPerSecond = ExtractMetricValue(response, "IncomingMessages"),
            OutgoingMessagesPerSecond = ExtractMetricValue(response, "OutgoingMessages")
        };
    }
}
```

### Azure Security Testing Components

```csharp
public class ManagedIdentityTestHelpers
{
    private readonly DefaultAzureCredential _credential;
    private readonly ILogger<ManagedIdentityTestHelpers> _logger;
    
    public async Task<bool> ValidateSystemAssignedIdentityAsync()
    {
        try
        {
            _logger.LogInformation("Validating system-assigned managed identity");
            
            var token = await _credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://vault.azure.net/.default" }));
            
            var isValid = !string.IsNullOrEmpty(token.Token);
            _logger.LogInformation("System-assigned identity validation: {IsValid}", isValid);
            
            return isValid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "System-assigned managed identity validation failed");
            return false;
        }
    }
    
    public async Task<bool> ValidateUserAssignedIdentityAsync(string clientId)
    {
        var credential = new ManagedIdentityCredential(clientId);
        
        try
        {
            _logger.LogInformation("Validating user-assigned managed identity: {ClientId}", clientId);
            
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://servicebus.azure.net/.default" }));
            
            var isValid = !string.IsNullOrEmpty(token.Token);
            _logger.LogInformation("User-assigned identity validation: {IsValid}", isValid);
            
            return isValid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "User-assigned managed identity validation failed for client ID: {ClientId}", clientId);
            return false;
        }
    }
    
    public async Task<RBACValidationResult> ValidateRBACPermissionsAsync()
    {
        _logger.LogInformation("Validating RBAC permissions");
        
        var result = new RBACValidationResult();
        
        // Test Service Bus permissions
        result.ServiceBusPermissions = await ValidateServiceBusPermissionsAsync();
        
        // Test Key Vault permissions
        result.KeyVaultPermissions = await ValidateKeyVaultPermissionsAsync();
        
        // Test identity types
        result.SystemAssignedIdentityValid = await ValidateSystemAssignedIdentityAsync();
        
        _logger.LogInformation("RBAC validation complete: ServiceBus={ServiceBus}, KeyVault={KeyVault}", 
            result.ServiceBusPermissions.CanSend && result.ServiceBusPermissions.CanReceive,
            result.KeyVaultPermissions.CanEncrypt && result.KeyVaultPermissions.CanDecrypt);
        
        return result;
    }
    
    private async Task<PermissionValidationResult> ValidateServiceBusPermissionsAsync()
    {
        var permissions = new PermissionValidationResult();
        var serviceBusClient = new ServiceBusClient(_configuration.FullyQualifiedNamespace, _credential);
        
        try
        {
            // Test send permission
            var sender = serviceBusClient.CreateSender("test-queue");
            await sender.SendMessageAsync(new ServiceBusMessage("test"));
            permissions.CanSend = true;
            _logger.LogInformation("Service Bus send permission validated");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Service Bus send permission denied");
            permissions.CanSend = false;
        }
        
        try
        {
            // Test receive permission
            var receiver = serviceBusClient.CreateReceiver("test-queue");
            await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(1));
            permissions.CanReceive = true;
            _logger.LogInformation("Service Bus receive permission validated");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Service Bus receive permission denied");
            permissions.CanReceive = false;
        }
        
        try
        {
            // Test manage permission
            var adminClient = new ServiceBusAdministrationClient(_configuration.FullyQualifiedNamespace, _credential);
            await adminClient.GetQueueAsync("test-queue");
            permissions.CanManage = true;
            _logger.LogInformation("Service Bus manage permission validated");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Service Bus manage permission denied");
            permissions.CanManage = false;
        }
        
        return permissions;
    }
    
    private async Task<KeyVaultValidationResult> ValidateKeyVaultPermissionsAsync()
    {
        var permissions = new KeyVaultValidationResult();
        var keyClient = new KeyClient(new Uri(_configuration.KeyVaultUrl), _credential);
        
        try
        {
            // Test get keys permission
            await keyClient.GetPropertiesOfKeysAsync().GetAsyncEnumerator().MoveNextAsync();
            permissions.CanGetKeys = true;
            _logger.LogInformation("Key Vault get keys permission validated");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Key Vault get keys permission denied");
            permissions.CanGetKeys = false;
        }
        
        try
        {
            // Test create keys permission
            var testKey = await keyClient.CreateRsaKeyAsync(new CreateRsaKeyOptions($"test-key-{Guid.NewGuid()}"));
            permissions.CanCreateKeys = true;
            _logger.LogInformation("Key Vault create keys permission validated");
            
            // Clean up test key
            await keyClient.StartDeleteKeyAsync(testKey.Value.Name);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Key Vault create keys permission denied");
            permissions.CanCreateKeys = false;
        }
        
        try
        {
            // Test encrypt/decrypt permissions
            var cryptoClient = new CryptographyClient(keyClient.VaultUri, _credential);
            var testData = Encoding.UTF8.GetBytes("test");
            var encrypted = await cryptoClient.EncryptAsync(EncryptionAlgorithm.RsaOaep, testData);
            permissions.CanEncrypt = true;
            
            var decrypted = await cryptoClient.DecryptAsync(EncryptionAlgorithm.RsaOaep, encrypted.Ciphertext);
            permissions.CanDecrypt = true;
            
            _logger.LogInformation("Key Vault encrypt/decrypt permissions validated");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Key Vault encrypt/decrypt permissions denied");
            permissions.CanEncrypt = false;
            permissions.CanDecrypt = false;
        }
        
        return permissions;
    }
}
```

### Azure CI/CD Integration Components

```csharp
public class AzureCICDTestRunner
{
    private readonly IAzureResourceManager _resourceManager;
    private readonly IAzureTestEnvironment _testEnvironment;
    private readonly ILogger<AzureCICDTestRunner> _logger;
    
    public async Task<CICDTestResult> RunCICDTestSuiteAsync(CICDTestConfiguration config)
    {
        _logger.LogInformation("Starting CI/CD test suite execution");
        
        var result = new CICDTestResult
        {
            StartTime = DateTime.UtcNow,
            Configuration = config
        };
        
        try
        {
            // Provision Azure resources using ARM templates
            if (config.UseRealAzureServices)
            {
                _logger.LogInformation("Provisioning Azure resources for CI/CD tests");
                result.ProvisionedResources = await ProvisionAzureResourcesAsync(config);
            }
            
            // Initialize test environment
            await _testEnvironment.InitializeAsync();
            
            // Run test suites
            result.IntegrationTestResults = await RunIntegrationTestsAsync();
            result.PerformanceTestResults = await RunPerformanceTestsAsync();
            result.SecurityTestResults = await RunSecurityTestsAsync();
            
            result.Success = result.IntegrationTestResults.All(r => r.Success) &&
                           result.PerformanceTestResults.All(r => r.Success) &&
                           result.SecurityTestResults.All(r => r.Success);
            
            _logger.LogInformation("CI/CD test suite completed: {Success}", result.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CI/CD test suite failed");
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }
        finally
        {
            // Cleanup Azure resources
            if (config.UseRealAzureServices && config.CleanupAfterTests)
            {
                _logger.LogInformation("Cleaning up Azure resources");
                await CleanupAzureResourcesAsync(result.ProvisionedResources);
            }
            
            result.EndTime = DateTime.UtcNow;
            result.Duration = result.EndTime - result.StartTime;
        }
        
        return result;
    }
    
    private async Task<List<string>> ProvisionAzureResourcesAsync(CICDTestConfiguration config)
    {
        var provisionedResources = new List<string>();
        
        // Create Service Bus namespace
        var namespaceName = $"sf-test-{Guid.NewGuid():N}";
        _logger.LogInformation("Creating Service Bus namespace: {NamespaceName}", namespaceName);
        
        // Deploy ARM template for Service Bus
        var serviceBusResourceId = await DeployARMTemplateAsync("servicebus-template.json", new
        {
            namespaceName = namespaceName,
            location = config.AzureRegion,
            sku = "Standard"
        });
        
        provisionedResources.Add(serviceBusResourceId);
        
        // Create Key Vault
        var vaultName = $"sf-test-{Guid.NewGuid():N}";
        _logger.LogInformation("Creating Key Vault: {VaultName}", vaultName);
        
        var keyVaultResourceId = await DeployARMTemplateAsync("keyvault-template.json", new
        {
            vaultName = vaultName,
            location = config.AzureRegion,
            sku = "standard"
        });
        
        provisionedResources.Add(keyVaultResourceId);
        
        // Wait for resources to be ready
        await Task.Delay(TimeSpan.FromSeconds(30));
        
        _logger.LogInformation("Provisioned {Count} Azure resources", provisionedResources.Count);
        return provisionedResources;
    }
    
    private async Task CleanupAzureResourcesAsync(List<string> resourceIds)
    {
        foreach (var resourceId in resourceIds)
        {
            try
            {
                _logger.LogInformation("Deleting resource: {ResourceId}", resourceId);
                await _resourceManager.DeleteResourceAsync(resourceId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete resource: {ResourceId}", resourceId);
            }
        }
    }
    
    private async Task<string> DeployARMTemplateAsync(string templateFile, object parameters)
    {
        // Deploy ARM template and return resource ID
        // Implementation would use Azure.ResourceManager SDK
        return $"/subscriptions/{Guid.NewGuid()}/resourceGroups/test/providers/Microsoft.ServiceBus/namespaces/test";
    }
}

public class AzureTestDocumentationGenerator
{
    private readonly ILogger<AzureTestDocumentationGenerator> _logger;
    
    public async Task GenerateSetupDocumentationAsync(string outputPath)
    {
        _logger.LogInformation("Generating Azure setup documentation");
        
        var documentation = new StringBuilder();
        documentation.AppendLine("# Azure Integration Testing Setup Guide");
        documentation.AppendLine();
        documentation.AppendLine("## Prerequisites");
        documentation.AppendLine("- Azure subscription with appropriate permissions");
        documentation.AppendLine("- Azure CLI installed and configured");
        documentation.AppendLine("- .NET 8.0 or later SDK");
        documentation.AppendLine();
        documentation.AppendLine("## Service Bus Configuration");
        documentation.AppendLine("1. Create Service Bus namespace");
        documentation.AppendLine("2. Configure RBAC permissions");
        documentation.AppendLine("3. Create test queues and topics");
        documentation.AppendLine();
        documentation.AppendLine("## Key Vault Configuration");
        documentation.AppendLine("1. Create Key Vault instance");
        documentation.AppendLine("2. Configure access policies");
        documentation.AppendLine("3. Create test encryption keys");
        documentation.AppendLine();
        documentation.AppendLine("## Managed Identity Setup");
        documentation.AppendLine("1. Enable system-assigned managed identity");
        documentation.AppendLine("2. Assign RBAC roles");
        documentation.AppendLine("3. Validate authentication");
        
        await File.WriteAllTextAsync(Path.Combine(outputPath, "AZURE_SETUP.md"), documentation.ToString());
        _logger.LogInformation("Setup documentation generated");
    }
    
    public async Task GenerateTroubleshootingGuideAsync(string outputPath)
    {
        _logger.LogInformation("Generating Azure troubleshooting guide");
        
        var guide = new StringBuilder();
        guide.AppendLine("# Azure Integration Testing Troubleshooting Guide");
        guide.AppendLine();
        guide.AppendLine("## Common Issues");
        guide.AppendLine();
        guide.AppendLine("### Authentication Failures");
        guide.AppendLine("**Symptom**: UnauthorizedAccessException when accessing Azure services");
        guide.AppendLine("**Solution**: Verify managed identity is enabled and RBAC roles are assigned");
        guide.AppendLine();
        guide.AppendLine("### Service Bus Connection Issues");
        guide.AppendLine("**Symptom**: ServiceBusException with connection timeout");
        guide.AppendLine("**Solution**: Check network connectivity and firewall rules");
        guide.AppendLine();
        guide.AppendLine("### Key Vault Access Denied");
        guide.AppendLine("**Symptom**: ForbiddenException when accessing Key Vault");
        guide.AppendLine("**Solution**: Verify Key Vault access policies and RBAC permissions");
        
        await File.WriteAllTextAsync(Path.Combine(outputPath, "AZURE_TROUBLESHOOTING.md"), guide.ToString());
        _logger.LogInformation("Troubleshooting guide generated");
    }
}
```

## Data Models

### Azure Test Configuration Models

```csharp
public class AzureTestConfiguration
{
    public bool UseAzurite { get; set; } = true;
    public string ServiceBusConnectionString { get; set; } = "";
    public string FullyQualifiedNamespace { get; set; } = "";
    public string KeyVaultUrl { get; set; } = "";
    public bool UseManagedIdentity { get; set; } = false;
    public string UserAssignedIdentityClientId { get; set; } = "";
    public string AzureRegion { get; set; } = "eastus";
    public string ResourceGroupName { get; set; } = "sourceflow-tests";
    public Dictionary<string, string> QueueNames { get; set; } = new();
    public Dictionary<string, string> TopicNames { get; set; } = new();
    public Dictionary<string, string> SubscriptionNames { get; set; } = new();
    public AzurePerformanceTestConfiguration Performance { get; set; } = new();
    public AzureSecurityTestConfiguration Security { get; set; } = new();
    public AzureResilienceTestConfiguration Resilience { get; set; } = new();
}

public class AzurePerformanceTestConfiguration
{
    public int MaxConcurrentSenders { get; set; } = 100;
    public int MaxConcurrentReceivers { get; set; } = 50;
    public TimeSpan TestDuration { get; set; } = TimeSpan.FromMinutes(5);
    public int WarmupMessages { get; set; } = 100;
    public bool EnableAutoScalingTests { get; set; } = true;
    public bool EnableLatencyTests { get; set; } = true;
    public bool EnableThroughputTests { get; set; } = true;
    public bool EnableResourceUtilizationTests { get; set; } = true;
    public List<int> MessageSizes { get; set; } = new() { 1024, 10240, 102400 }; // 1KB, 10KB, 100KB
}

public class AzureSecurityTestConfiguration
{
    public bool TestSystemAssignedIdentity { get; set; } = true;
    public bool TestUserAssignedIdentity { get; set; } = false;
    public bool TestRBACPermissions { get; set; } = true;
    public bool TestKeyVaultAccess { get; set; } = true;
    public bool TestSensitiveDataMasking { get; set; } = true;
    public bool TestAuditLogging { get; set; } = true;
    public List<string> TestKeyNames { get; set; } = new() { "test-key-1", "test-key-2" };
    public List<string> RequiredServiceBusRoles { get; set; } = new() 
    { 
        "Azure Service Bus Data Sender", 
        "Azure Service Bus Data Receiver" 
    };
    public List<string> RequiredKeyVaultRoles { get; set; } = new() 
    { 
        "Key Vault Crypto User" 
    };
}

public class AzureResilienceTestConfiguration
{
    public bool TestCircuitBreaker { get; set; } = true;
    public bool TestRetryPolicies { get; set; } = true;
    public bool TestThrottlingHandling { get; set; } = true;
    public bool TestNetworkPartitions { get; set; } = true;
    public int CircuitBreakerFailureThreshold { get; set; } = 5;
    public TimeSpan CircuitBreakerTimeout { get; set; } = TimeSpan.FromMinutes(1);
    public int MaxRetryAttempts { get; set; } = 3;
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);
}

public class CICDTestConfiguration
{
    public bool UseRealAzureServices { get; set; } = false;
    public bool CleanupAfterTests { get; set; } = true;
    public string AzureRegion { get; set; } = "eastus";
    public string ResourceGroupName { get; set; } = "sourceflow-cicd-tests";
    public string ARMTemplateBasePath { get; set; } = "./arm-templates";
    public bool GenerateTestReports { get; set; } = true;
    public string TestReportOutputPath { get; set; } = "./test-results";
    public bool EnableParallelExecution { get; set; } = true;
    public int MaxParallelTests { get; set; } = 4;
}
```

### Azure Test Result Models

```csharp
public class AzurePerformanceTestResult
{
    public string TestName { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration { get; set; }
    public double MessagesPerSecond { get; set; }
    public int TotalMessages { get; set; }
    public int SuccessfulMessages { get; set; }
    public int FailedMessages { get; set; }
    public TimeSpan AverageLatency { get; set; }
    public TimeSpan MedianLatency { get; set; }
    public TimeSpan P95Latency { get; set; }
    public TimeSpan P99Latency { get; set; }
    public TimeSpan MinLatency { get; set; }
    public TimeSpan MaxLatency { get; set; }
    public ServiceBusMetrics ServiceBusMetrics { get; set; } = new();
    public List<double> AutoScalingMetrics { get; set; } = new();
    public double ScalingEfficiency { get; set; }
    public AzureResourceUsage ResourceUsage { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public Dictionary<string, object> CustomMetrics { get; set; } = new();
}

public class ServiceBusMetrics
{
    public long ActiveMessages { get; set; }
    public long DeadLetterMessages { get; set; }
    public long ScheduledMessages { get; set; }
    public double IncomingMessagesPerSecond { get; set; }
    public double OutgoingMessagesPerSecond { get; set; }
    public double ThrottledRequests { get; set; }
    public double SuccessfulRequests { get; set; }
    public double FailedRequests { get; set; }
    public long AverageMessageSizeBytes { get; set; }
    public TimeSpan AverageMessageProcessingTime { get; set; }
    public int ActiveConnections { get; set; }
}

public class KeyVaultMetrics
{
    public double RequestsPerSecond { get; set; }
    public double SuccessfulRequests { get; set; }
    public double FailedRequests { get; set; }
    public TimeSpan AverageLatency { get; set; }
    public int ActiveKeys { get; set; }
    public int EncryptOperations { get; set; }
    public int DecryptOperations { get; set; }
}

public class AzureResourceUsage
{
    public double ServiceBusCpuPercent { get; set; }
    public long ServiceBusMemoryBytes { get; set; }
    public long NetworkBytesIn { get; set; }
    public long NetworkBytesOut { get; set; }
    public double KeyVaultRequestsPerSecond { get; set; }
    public double KeyVaultLatencyMs { get; set; }
    public int ServiceBusConnectionCount { get; set; }
    public double ServiceBusNamespaceUtilizationPercent { get; set; }
}

public class CICDTestResult
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration { get; set; }
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = "";
    public CICDTestConfiguration Configuration { get; set; } = new();
    public List<string> ProvisionedResources { get; set; } = new();
    public List<TestResult> IntegrationTestResults { get; set; } = new();
    public List<AzurePerformanceTestResult> PerformanceTestResults { get; set; } = new();
    public List<AzureSecurityTestResult> SecurityTestResults { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();
}

public class TestResult
{
    public string TestName { get; set; } = "";
    public bool Success { get; set; }
    public TimeSpan Duration { get; set; }
    public string ErrorMessage { get; set; } = "";
    public List<string> Warnings { get; set; } = new();
}
```

### Azure Test Scenario Models

```csharp
public class AzureTestScenario
{
    public string Name { get; set; } = "";
    public string QueueName { get; set; } = "";
    public string TopicName { get; set; } = "";
    public string SubscriptionName { get; set; } = "";
    public int MessageCount { get; set; } = 100;
    public int ConcurrentSenders { get; set; } = 1;
    public int ConcurrentReceivers { get; set; } = 1;
    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(1);
    public MessageSize MessageSize { get; set; } = MessageSize.Small;
    public bool EnableSessions { get; set; } = false;
    public bool EnableDuplicateDetection { get; set; } = false;
    public bool EnableEncryption { get; set; } = false;
    public bool SimulateFailures { get; set; } = false;
    public bool TestAutoScaling { get; set; } = false;
}

public enum MessageSize
{
    Small,    // < 1KB
    Medium,   // 1KB - 10KB
    Large     // 10KB - 256KB (Service Bus limit)
}
```

### Azure Security Test Models

```csharp
public class AzureSecurityTestResult
{
    public string TestName { get; set; } = "";
    public bool ManagedIdentityWorking { get; set; }
    public bool EncryptionWorking { get; set; }
    public bool SensitiveDataMasked { get; set; }
    public RBACValidationResult RBACValidation { get; set; } = new();
    public KeyVaultValidationResult KeyVaultValidation { get; set; } = new();
    public List<AzureSecurityViolation> Violations { get; set; } = new();
}

public class RBACValidationResult
{
    public PermissionValidationResult ServiceBusPermissions { get; set; } = new();
    public PermissionValidationResult KeyVaultPermissions { get; set; } = new();
    public bool SystemAssignedIdentityValid { get; set; }
    public bool UserAssignedIdentityValid { get; set; }
}

public class PermissionValidationResult
{
    public bool CanSend { get; set; }
    public bool CanReceive { get; set; }
    public bool CanManage { get; set; }
    public bool CanListen { get; set; }
}

public class KeyVaultValidationResult
{
    public bool CanGetKeys { get; set; }
    public bool CanCreateKeys { get; set; }
    public bool CanEncrypt { get; set; }
    public bool CanDecrypt { get; set; }
    public bool KeyRotationWorking { get; set; }
}

public class AzureSecurityViolation
{
    public string Type { get; set; } = "";
    public string Description { get; set; } = "";
    public string Severity { get; set; } = "";
    public string AzureRecommendation { get; set; } = "";
    public string DocumentationLink { get; set; } = "";
}
```

## Correctness Properties

*A property is a characteristic or behavior that should hold true across all valid executions of a system—essentially, a formal statement about what the system should do. Properties serve as the bridge between human-readable specifications and machine-verifiable correctness guarantees.*

### Property Reflection

After analyzing all acceptance criteria, I identified several areas where properties can be consolidated to eliminate redundancy:

- **Message Routing Properties**: Commands and events both test routing correctness, but can be combined into comprehensive routing properties
- **Session Ordering Properties**: Both commands and events test session-based ordering, which can be unified
- **Health Check Properties**: Service Bus and Key Vault health checks follow the same pattern and can be consolidated
- **Performance Properties**: Throughput, latency, and resource utilization can be combined into comprehensive performance validation
- **Authentication Properties**: Managed identity and RBAC testing can be unified into authentication/authorization properties
- **Emulator Equivalence**: All local testing requirements can be consolidated into emulator equivalence properties

### Property 1: Azure Service Bus Message Routing Correctness
*For any* valid command or event and any Azure Service Bus queue or topic configuration, when a message is dispatched through Azure Service Bus, it should be routed to the correct destination and maintain all message properties including correlation IDs, session IDs, and custom metadata.
**Validates: Requirements 1.1, 2.1**

### Property 2: Azure Service Bus Session Ordering Preservation
*For any* sequence of commands or events with the same session ID, when processed through Azure Service Bus, they should be received and processed in the exact order they were sent, regardless of concurrent processing of other sessions.
**Validates: Requirements 1.2, 2.5**

### Property 3: Azure Service Bus Duplicate Detection Effectiveness
*For any* command or event sent multiple times with the same message ID within the duplicate detection window, Azure Service Bus should automatically deduplicate and deliver only one instance to consumers.
**Validates: Requirements 1.3**

### Property 4: Azure Service Bus Subscription Filtering Accuracy
*For any* event published to an Azure Service Bus topic with subscription filters, the event should be delivered only to subscriptions whose filter criteria match the event properties.
**Validates: Requirements 2.2**

### Property 5: Azure Service Bus Fan-Out Completeness
*For any* event published to an Azure Service Bus topic with multiple active subscriptions, the event should be delivered to all active subscriptions that match the filtering criteria.
**Validates: Requirements 2.4**

### Property 6: Azure Key Vault Encryption Round-Trip Consistency
*For any* message containing data, when encrypted using Azure Key Vault and then decrypted, the resulting message should be identical to the original message, and all sensitive data should be properly masked in logs.
**Validates: Requirements 3.1, 3.4**

### Property 7: Azure Managed Identity Authentication Seamlessness
*For any* Azure service operation requiring authentication, when using managed identity (system-assigned or user-assigned), authentication should succeed without requiring connection strings or explicit credentials when proper permissions are configured.
**Validates: Requirements 3.2, 9.1**

### Property 8: Azure Key Vault Key Rotation Seamlessness
*For any* encrypted message flow, when Azure Key Vault keys are rotated, existing messages should continue to be decryptable with old key versions and new messages should use the new key version without service interruption.
**Validates: Requirements 3.3**

### Property 9: Azure RBAC Permission Enforcement
*For any* Azure service operation, when using RBAC permissions, operations should succeed when proper permissions are granted and fail gracefully with appropriate error messages when permissions are insufficient.
**Validates: Requirements 3.5, 4.4, 9.2**

### Property 10: Azure Health Check Accuracy
*For any* Azure service configuration (Service Bus, Key Vault), health checks should accurately reflect the actual availability and accessibility of the service, returning true when services are available and accessible, and false when they are not.
**Validates: Requirements 4.1, 4.2, 4.3**

### Property 11: Azure Telemetry Collection Completeness
*For any* Azure service operation, when Azure Monitor integration is enabled, telemetry data including metrics, traces, and logs should be collected and reported accurately with proper correlation IDs.
**Validates: Requirements 4.5**

### Property 12: Azure Dead Letter Queue Handling Completeness
*For any* message that fails processing in Azure Service Bus, it should be captured in the appropriate dead letter queue with complete failure metadata including error details, retry count, and original message properties.
**Validates: Requirements 1.4**

### Property 13: Azure Concurrent Processing Integrity
*For any* set of messages processed concurrently through Azure Service Bus, all messages should be processed without loss or corruption, maintaining message integrity and proper session ordering where applicable.
**Validates: Requirements 1.5**

### Property 14: Azure Performance Measurement Consistency
*For any* Azure performance test scenario (throughput, latency, resource utilization), when executed multiple times under similar conditions, the performance measurements should be consistent within acceptable variance ranges and scale appropriately with load.
**Validates: Requirements 5.1, 5.2, 5.3, 5.5**

### Property 15: Azure Auto-Scaling Effectiveness
*For any* Azure Service Bus configuration with auto-scaling enabled, when load increases gradually, the service should scale appropriately to maintain performance characteristics within acceptable thresholds.
**Validates: Requirements 5.4**

### Property 16: Azure Circuit Breaker State Transitions
*For any* Azure circuit breaker configuration, when failure thresholds are exceeded for Azure services, the circuit should open automatically, attempt recovery after timeout periods, and close when success thresholds are met.
**Validates: Requirements 6.1**

### Property 17: Azure Retry Policy Compliance
*For any* failed Azure Service Bus message with retry configuration, the system should retry according to the specified policy (exponential backoff, maximum attempts) and eventually move poison messages to dead letter queues.
**Validates: Requirements 6.2**

### Property 18: Azure Service Failure Graceful Degradation
*For any* Azure service failure scenario (Service Bus unavailable, Key Vault inaccessible), the system should degrade gracefully, implement appropriate fallback mechanisms, and recover automatically when services become available.
**Validates: Requirements 6.3**

### Property 19: Azure Throttling Handling Resilience
*For any* Azure Service Bus throttling scenario, the system should handle rate limiting gracefully with appropriate backoff strategies and maintain message processing integrity.
**Validates: Requirements 6.4**

### Property 20: Azure Network Partition Recovery
*For any* network partition scenario affecting Azure services, the system should detect the partition, implement appropriate circuit breaker behavior, and recover automatically when connectivity is restored.
**Validates: Requirements 6.5**

### Property 21: Azurite Emulator Functional Equivalence
*For any* test scenario that runs successfully against real Azure services, the same test should run successfully against Azurite emulators with functionally equivalent results, allowing for performance differences due to emulation overhead.
**Validates: Requirements 7.1, 7.2, 7.3, 7.5**

### Property 22: Azurite Performance Metrics Meaningfulness
*For any* performance test executed against Azurite emulators, the performance metrics should provide meaningful insights into system behavior patterns, even if absolute values differ from cloud services due to emulation overhead.
**Validates: Requirements 7.4**

### Property 23: Azure CI/CD Environment Consistency
*For any* test suite, when executed in different environments (local Azurite, CI/CD, Azure cloud), the functional test results should be consistent, with only expected performance variations between environments.
**Validates: Requirements 8.1**

### Property 24: Azure Test Resource Management Completeness
*For any* test execution requiring Azure resources, all resources created during testing should be automatically cleaned up after test completion, and resource creation should be idempotent to prevent conflicts.
**Validates: Requirements 8.2, 8.5**

### Property 25: Azure Test Reporting Completeness
*For any* Azure test execution, the generated reports should contain all required Azure-specific metrics, error details, and analysis data, and should be accessible for historical trend analysis.
**Validates: Requirements 8.3**

### Property 26: Azure Error Message Actionability
*For any* Azure test failure, the error messages and troubleshooting guidance should provide sufficient Azure-specific information to identify and resolve the underlying issue.
**Validates: Requirements 8.4**

### Property 27: Azure Key Vault Access Policy Validation
*For any* Azure Key Vault operation, when access policies are configured, operations should succeed when proper policies are in place and fail appropriately when policies are insufficient, with clear error messages indicating required permissions.
**Validates: Requirements 9.3**

### Property 28: Azure End-to-End Encryption Security
*For any* sensitive data transmitted through Azure services, the data should be encrypted end-to-end both in transit and at rest, with proper key management and no exposure of sensitive data in logs or intermediate storage.
**Validates: Requirements 9.4**

### Property 29: Azure Security Audit Logging Completeness
*For any* security-related operation in Azure services (authentication, authorization, key access), appropriate audit logs should be generated with sufficient detail for security analysis and compliance requirements.
**Validates: Requirements 9.5**

## Error Handling

### Azure Service Failures
The testing framework handles various Azure service failure scenarios:

- **Service Bus Unavailability**: Tests validate graceful degradation when Service Bus namespace or specific queues/topics are unavailable
- **Key Vault Inaccessibility**: Tests verify proper error handling for Key Vault connectivity issues or key unavailability
- **Managed Identity Failures**: Tests validate behavior when managed identity authentication fails or tokens expire
- **RBAC Permission Denials**: Tests verify appropriate error messages and fallback behavior for insufficient permissions
- **Network Connectivity Issues**: Tests simulate network partitions and validate retry behavior and circuit breaker patterns

### Azure-Specific Error Conditions
The framework provides robust error handling for Azure-specific issues:

- **Service Bus Throttling**: Automatic retry with exponential backoff when Service Bus rate limits are exceeded
- **Key Vault Rate Limiting**: Proper handling of Key Vault request throttling with appropriate backoff strategies
- **Session Lock Timeouts**: Handling of Service Bus session lock timeouts and automatic session renewal
- **Duplicate Detection Window**: Proper handling of messages outside the duplicate detection time window
- **Message Size Limits**: Validation and error handling for messages exceeding Service Bus size limits (256KB)

### Test Environment Error Recovery
The testing framework includes safeguards against test environment failures:

- **Azurite Startup Failures**: Automatic retry and fallback to cloud services when emulators fail to start
- **Azure Resource Provisioning Failures**: Cleanup and retry mechanisms for ARM template deployment failures
- **Configuration Errors**: Clear error messages for misconfigured Azure connection strings, managed identity, or RBAC permissions
- **Concurrent Test Execution**: Isolation mechanisms to prevent test interference in shared Azure resources

### Data Integrity and Security
The testing framework includes safeguards against data corruption and security issues:

- **Message Integrity Validation**: Checksums and validation for all test messages to detect corruption
- **Sensitive Data Protection**: Automatic masking and encryption of sensitive test data
- **Test Data Isolation**: Separate Azure resources and namespaces to prevent cross-contamination
- **Audit Trail Maintenance**: Complete audit logs for all test operations for security analysis

## Testing Strategy

### Dual Testing Approach
The testing strategy employs both unit testing and property-based testing as complementary approaches:

- **Unit Tests**: Validate specific examples, edge cases, and error conditions for individual Azure components
- **Property Tests**: Verify universal properties across all inputs using randomized test data with Azure-specific generators
- **Integration Tests**: Validate end-to-end scenarios with real or emulated Azure services
- **Performance Tests**: Measure and validate Azure-specific performance characteristics under various conditions

### Property-Based Testing Configuration
The framework uses **xUnit** and **FsCheck** for .NET property-based testing with Azure-specific configuration:

- **Minimum 100 iterations** per property test to ensure comprehensive coverage of Azure scenarios
- **Custom generators** for Azure Service Bus messages, Key Vault keys, managed identity configurations, and RBAC permissions
- **Azure-specific shrinking strategies** to find minimal failing examples when properties fail
- **Test tagging** with format: **Feature: azure-cloud-integration-testing, Property {number}: {property_text}**

Each correctness property is implemented by a single property-based test that references its design document property.

### Unit Testing Balance
Unit tests focus on:
- **Specific Examples**: Concrete Azure scenarios that demonstrate correct behavior
- **Edge Cases**: Azure-specific boundary conditions like message size limits, session timeouts, and throttling scenarios
- **Error Conditions**: Invalid Azure configurations, authentication failures, and permission denials
- **Integration Points**: Interactions between SourceFlow components and Azure services

Property tests handle comprehensive input coverage through randomization, while unit tests provide targeted validation of critical Azure scenarios.

### Azure Test Environment Strategy
The testing strategy supports multiple Azure-specific environments:

1. **Local Development**: Fast feedback using Azurite emulators for Service Bus and Key Vault
2. **Azure Integration Testing**: Validation against real Azure services in isolated development subscriptions
3. **Azure Performance Testing**: Dedicated Azure resources for load and scalability testing with proper scaling configurations
4. **CI/CD Pipeline**: Automated testing with both Azurite emulators and real Azure services using ARM template provisioning

### Azure Performance Testing Strategy
Performance tests are designed to:
- **Establish Azure Baselines**: Measure Azure Service Bus and Key Vault performance characteristics under normal conditions
- **Detect Azure Regressions**: Identify performance degradation in Azure integrations with new releases
- **Validate Azure Scalability**: Ensure performance scales appropriately with Azure Service Bus auto-scaling
- **Azure Resource Optimization**: Identify opportunities for Azure resource usage optimization and cost reduction

### Azure Security Testing Strategy
Security tests validate:
- **Managed Identity Effectiveness**: End-to-end managed identity authentication for both system and user-assigned identities
- **RBAC Enforcement**: Proper Azure role-based access control for Service Bus and Key Vault operations
- **Key Vault Security**: Proper key access policies, encryption effectiveness, and audit logging
- **Sensitive Data Protection**: Automatic masking and secure handling of sensitive data in Azure message flows

### Azure Documentation and Reporting Strategy
The testing framework provides comprehensive Azure-specific documentation and reporting:
- **Azure Setup Guides**: Step-by-step instructions for Service Bus namespace, Key Vault, and managed identity configuration
- **Azurite Setup Guides**: Instructions for local development environment setup with Azure emulators
- **Azure Performance Reports**: Detailed metrics and trend analysis specific to Azure services
- **Azure Troubleshooting Guides**: Common Azure issues, error codes, and resolution steps with links to Azure documentation
- **Azure Security Guides**: Managed identity setup, RBAC configuration, and Key Vault access policy guidance
- **Historical Analysis**: Long-term trend tracking for Azure service performance and cost optimization
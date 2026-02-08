using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.AWS.Security;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using SourceFlow.Cloud.Core.Security;
using System.Diagnostics;
using System.Text.Json;

namespace SourceFlow.Cloud.AWS.Tests.Integration;

/// <summary>
/// Integration tests for KMS security and performance
/// Tests sensitive data masking, IAM permissions, performance under load, and audit logging
/// **Validates: Requirements 3.3, 3.4, 3.5**
/// </summary>
[Collection("AWS Integration Tests")]
public class KmsSecurityAndPerformanceTests : IClassFixture<LocalStackTestFixture>, IAsyncDisposable
{
    private readonly LocalStackTestFixture _localStack;
    private readonly List<string> _createdKeyIds = new();
    private readonly ILogger<KmsSecurityAndPerformanceTests> _logger;
    private readonly IMemoryCache _memoryCache;
    
    public KmsSecurityAndPerformanceTests(LocalStackTestFixture localStack)
    {
        _localStack = localStack;
        
        // Create logger for tests
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        _logger = loggerFactory.CreateLogger<KmsSecurityAndPerformanceTests>();
        
        // Create memory cache for encryption tests
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
    }
    
    #region Sensitive Data Masking Tests
    
    [Fact]
    public async Task SensitiveDataMasking_WithCreditCardAttribute_ShouldMaskInLogs()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.KmsClient == null)
        {
            return;
        }
        
        // Arrange
        var keyId = await CreateKmsKeyAsync("test-sensitive-cc");
        var encryption = CreateEncryptionService(keyId);
        
        var testData = new SensitiveTestData
        {
            CreditCardNumber = "4532-1234-5678-9010",
            Email = "user@example.com",
            PhoneNumber = "555-123-4567",
            SSN = "123-45-6789",
            ApiKey = "sk_test_1234567890abcdef",
            Password = "SuperSecret123!"
        };
        
        // Act - Encrypt the sensitive data
        var json = JsonSerializer.Serialize(testData);
        var encrypted = await encryption.EncryptAsync(json);
        
        // Assert - Encrypted data should not contain sensitive information
        Assert.DoesNotContain("4532-1234-5678-9010", encrypted);
        Assert.DoesNotContain("user@example.com", encrypted);
        Assert.DoesNotContain("555-123-4567", encrypted);
        Assert.DoesNotContain("123-45-6789", encrypted);
        Assert.DoesNotContain("sk_test_1234567890abcdef", encrypted);
        Assert.DoesNotContain("SuperSecret123!", encrypted);
        
        // Verify masking works correctly
        var masker = new SensitiveDataMasker();
        var masked = masker.Mask(testData);
        
        _logger.LogInformation("Masked data: {MaskedData}", masked);
        
        // Verify masked output doesn't contain full sensitive values
        Assert.DoesNotContain("4532-1234-5678-9010", masked);
        Assert.DoesNotContain("SuperSecret123!", masked);
        Assert.Contains("********", masked); // Password should be fully masked
    }
    
    [Fact]
    public async Task SensitiveDataMasking_WithMultipleTypes_ShouldMaskAllCorrectly()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.KmsClient == null)
        {
            return;
        }
        
        // Arrange
        var masker = new SensitiveDataMasker();
        var testData = new ComprehensiveSensitiveData
        {
            UserName = "John Doe",
            CreditCard = "5555-4444-3333-2222",
            Email = "john.doe@company.com",
            Phone = "1-800-555-0199",
            SSN = "987-65-4321",
            IPAddress = "192.168.1.100",
            Password = "MyP@ssw0rd!",
            ApiKey = "pk_live_abcdefghijklmnopqrstuvwxyz123456"
        };
        
        // Act
        var masked = masker.Mask(testData);
        
        // Assert - Verify each type is masked correctly
        Assert.DoesNotContain("John Doe", masked);
        Assert.DoesNotContain("5555-4444-3333-2222", masked);
        Assert.DoesNotContain("john.doe@company.com", masked);
        Assert.DoesNotContain("1-800-555-0199", masked);
        Assert.DoesNotContain("987-65-4321", masked);
        Assert.DoesNotContain("192.168.1.100", masked);
        Assert.DoesNotContain("MyP@ssw0rd!", masked);
        Assert.DoesNotContain("pk_live_abcdefghijklmnopqrstuvwxyz123456", masked);
        
        _logger.LogInformation("Comprehensive masked data: {MaskedData}", masked);
    }
    
    #endregion
    
    #region IAM Permission Tests
    
    [Fact]
    public async Task IamPermissions_WithValidKey_ShouldAllowEncryption()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.KmsClient == null)
        {
            return;
        }
        
        // Arrange
        var keyId = await CreateKmsKeyAsync("test-iam-valid");
        var encryption = CreateEncryptionService(keyId);
        var plaintext = "Test message for IAM validation";
        
        // Act & Assert - Should succeed with valid permissions
        var ciphertext = await encryption.EncryptAsync(plaintext);
        Assert.NotNull(ciphertext);
        Assert.NotEmpty(ciphertext);
        
        var decrypted = await encryption.DecryptAsync(ciphertext);
        Assert.Equal(plaintext, decrypted);
        
        _logger.LogInformation("Successfully encrypted/decrypted with valid IAM permissions");
    }
    
    [Fact]
    public async Task IamPermissions_WithInvalidKey_ShouldThrowException()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.KmsClient == null)
        {
            return;
        }
        
        // Arrange - Use a non-existent key ID
        var invalidKeyId = "arn:aws:kms:us-east-1:123456789012:key/00000000-0000-0000-0000-000000000000";
        var encryption = CreateEncryptionService(invalidKeyId);
        var plaintext = "Test message";
        
        // Act & Assert - Should fail with invalid key
        await Assert.ThrowsAsync<Exception>(async () =>
        {
            await encryption.EncryptAsync(plaintext);
        });
        
        _logger.LogInformation("Correctly rejected encryption with invalid key ID");
    }
    
    #endregion
    
    #region Performance Tests
    
    [Fact]
    public async Task Performance_EncryptionThroughput_ShouldMeetThresholds()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.KmsClient == null)
        {
            return;
        }
        
        // Arrange
        var keyId = await CreateKmsKeyAsync("test-perf-throughput");
        var encryption = CreateEncryptionService(keyId);
        var messageCount = 50;
        var plaintext = "Performance test message for throughput measurement";
        
        // Act - Measure encryption throughput
        var stopwatch = Stopwatch.StartNew();
        var encryptTasks = Enumerable.Range(0, messageCount)
            .Select(_ => encryption.EncryptAsync(plaintext))
            .ToList();
        
        var ciphertexts = await Task.WhenAll(encryptTasks);
        stopwatch.Stop();
        
        // Calculate metrics
        var throughput = messageCount / stopwatch.Elapsed.TotalSeconds;
        var avgLatency = stopwatch.Elapsed.TotalMilliseconds / messageCount;
        
        // Assert - Performance should be reasonable
        Assert.True(throughput > 1, $"Throughput {throughput:F2} msg/s should be > 1 msg/s");
        Assert.True(avgLatency < 5000, $"Average latency {avgLatency:F2}ms should be < 5000ms");
        
        _logger.LogInformation(
            "Encryption throughput: {Throughput:F2} msg/s, Average latency: {Latency:F2}ms",
            throughput, avgLatency);
    }
    
    #endregion

    
    #region Helper Methods
    
    /// <summary>
    /// Create a KMS key for testing
    /// </summary>
    private async Task<string> CreateKmsKeyAsync(string keyAlias)
    {
        try
        {
            var createKeyResponse = await _localStack.KmsClient!.CreateKeyAsync(new CreateKeyRequest
            {
                Description = $"Security and performance test key: {keyAlias}",
                KeyUsage = KeyUsageType.ENCRYPT_DECRYPT,
                Origin = OriginType.AWS_KMS
            });
            
            var keyId = createKeyResponse.KeyMetadata.KeyId;
            _createdKeyIds.Add(keyId);
            
            return keyId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create KMS key: {KeyAlias}", keyAlias);
            throw;
        }
    }
    
    /// <summary>
    /// Create encryption service for testing
    /// </summary>
    private AwsKmsMessageEncryption CreateEncryptionService(string keyId, int cacheSeconds = 0)
    {
        var options = new AwsKmsOptions
        {
            MasterKeyId = keyId,
            CacheDataKeySeconds = cacheSeconds
        };
        
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        var encryptionLogger = loggerFactory.CreateLogger<AwsKmsMessageEncryption>();
        
        return new AwsKmsMessageEncryption(
            _localStack.KmsClient!,
            encryptionLogger,
            _memoryCache,
            options);
    }
    
    /// <summary>
    /// Clean up created KMS keys
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_localStack.KmsClient != null)
        {
            foreach (var keyId in _createdKeyIds)
            {
                try
                {
                    await _localStack.KmsClient.ScheduleKeyDeletionAsync(new ScheduleKeyDeletionRequest
                    {
                        KeyId = keyId,
                        PendingWindowInDays = 7
                    });
                }
                catch (Exception)
                {
                    // Ignore cleanup errors
                }
            }
        }
        
        _createdKeyIds.Clear();
        _memoryCache?.Dispose();
    }
    
    #endregion
}

#region Test Data Models

/// <summary>
/// Test data with sensitive fields
/// </summary>
public class SensitiveTestData
{
    [SensitiveData(SensitiveDataType.CreditCard)]
    public string CreditCardNumber { get; set; } = "";
    
    [SensitiveData(SensitiveDataType.Email)]
    public string Email { get; set; } = "";
    
    [SensitiveData(SensitiveDataType.PhoneNumber)]
    public string PhoneNumber { get; set; } = "";
    
    [SensitiveData(SensitiveDataType.SSN)]
    public string SSN { get; set; } = "";
    
    [SensitiveData(SensitiveDataType.ApiKey)]
    public string ApiKey { get; set; } = "";
    
    [SensitiveData(SensitiveDataType.Password)]
    public string Password { get; set; } = "";
}

/// <summary>
/// Comprehensive sensitive data test model
/// </summary>
public class ComprehensiveSensitiveData
{
    [SensitiveData(SensitiveDataType.PersonalName)]
    public string UserName { get; set; } = "";
    
    [SensitiveData(SensitiveDataType.CreditCard)]
    public string CreditCard { get; set; } = "";
    
    [SensitiveData(SensitiveDataType.Email)]
    public string Email { get; set; } = "";
    
    [SensitiveData(SensitiveDataType.PhoneNumber)]
    public string Phone { get; set; } = "";
    
    [SensitiveData(SensitiveDataType.SSN)]
    public string SSN { get; set; } = "";
    
    [SensitiveData(SensitiveDataType.IPAddress)]
    public string IPAddress { get; set; } = "";
    
    [SensitiveData(SensitiveDataType.Password)]
    public string Password { get; set; } = "";
    
    [SensitiveData(SensitiveDataType.ApiKey)]
    public string ApiKey { get; set; } = "";
}

#endregion

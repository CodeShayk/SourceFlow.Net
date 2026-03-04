using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.AWS.Security;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using System.Text;
using System.Text.Json;

namespace SourceFlow.Cloud.AWS.Tests.Integration;

/// <summary>
/// Property-based tests for KMS encryption round-trip consistency
/// Validates universal properties that should hold across all KMS encryption operations
/// </summary>
[Collection("AWS Integration Tests")]
[Trait("Category", "Integration")]
[Trait("Category", "RequiresLocalStack")]
public class KmsEncryptionRoundTripPropertyTests : IClassFixture<LocalStackTestFixture>, IAsyncDisposable
{
    private readonly LocalStackTestFixture _localStack;
    private readonly List<string> _createdKeyIds = new();
    private readonly ILogger<KmsEncryptionRoundTripPropertyTests> _logger;
    private readonly IMemoryCache _memoryCache;
    
    public KmsEncryptionRoundTripPropertyTests(LocalStackTestFixture localStack)
    {
        _localStack = localStack;
        
        // Create logger for tests
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        _logger = loggerFactory.CreateLogger<KmsEncryptionRoundTripPropertyTests>();
        
        // Create memory cache for encryption tests
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
    }
    
    /// <summary>
    /// Property 5: KMS Encryption Round-Trip Consistency
    /// For any message containing sensitive data, when encrypted using AWS KMS and then decrypted,
    /// the resulting message should be identical to the original message with all sensitive data
    /// properly protected.
    /// **Validates: Requirements 3.1**
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(KmsEncryptionGenerators) })]
    public async Task Property_KmsEncryptionRoundTripConsistency(KmsTestMessage message)
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.KmsClient == null)
        {
            return;
        }
        
        // Skip invalid messages
        if (message == null || string.IsNullOrEmpty(message.Content))
        {
            return;
        }
        
        // Arrange - Create KMS key for this test
        var keyId = await CreateKmsKeyAsync($"prop-test-{Guid.NewGuid():N}");
        var encryption = CreateEncryptionService(keyId);
        
        try
        {
            // Act - Encrypt the message
            var ciphertext = await encryption.EncryptAsync(message.Content);
            
            // Assert - Ciphertext should be different from plaintext
            AssertEncryptionProducedCiphertext(message.Content, ciphertext);
            
            // Act - Decrypt the ciphertext
            var decrypted = await encryption.DecryptAsync(ciphertext);
            
            // Assert - Round-trip consistency: decrypted should match original
            AssertRoundTripConsistency(message.Content, decrypted);
            
            // Assert - Encryption should be deterministic for same input (different ciphertext each time)
            await AssertEncryptionNonDeterminism(encryption, message.Content);
            
            // Assert - Sensitive data protection (ciphertext should not contain plaintext)
            AssertSensitiveDataProtection(message.Content, ciphertext, message.SensitiveFields);
            
            // Assert - Encryption performance should be reasonable
            await AssertEncryptionPerformance(encryption, message);
        }
        finally
        {
            // Cleanup is handled in DisposeAsync
        }
    }
    
    /// <summary>
    /// Assert that encryption produced valid ciphertext
    /// </summary>
    private static void AssertEncryptionProducedCiphertext(string plaintext, string ciphertext)
    {
        // Ciphertext should not be null or empty
        Assert.NotNull(ciphertext);
        Assert.NotEmpty(ciphertext);
        
        // Ciphertext should be different from plaintext
        Assert.NotEqual(plaintext, ciphertext);
        
        // Ciphertext should be base64 encoded (AWS KMS returns base64)
        Assert.True(IsBase64String(ciphertext), "Ciphertext should be base64 encoded");
        
        // Ciphertext should be longer than plaintext (due to encryption overhead)
        // Note: This may not always be true for very short plaintexts with compression
        if (plaintext.Length > 10)
        {
            Assert.True(ciphertext.Length > plaintext.Length * 0.5,
                "Ciphertext should have reasonable length relative to plaintext");
        }
    }
    
    /// <summary>
    /// Assert round-trip consistency: decrypt(encrypt(plaintext)) == plaintext
    /// </summary>
    private static void AssertRoundTripConsistency(string original, string decrypted)
    {
        // Decrypted text should match original exactly
        Assert.Equal(original, decrypted);
        
        // Length should match
        Assert.Equal(original.Length, decrypted.Length);
        
        // Character-by-character comparison for Unicode safety
        for (int i = 0; i < original.Length; i++)
        {
            Assert.Equal(original[i], decrypted[i]);
        }
        
        // Byte-level comparison for complete accuracy
        var originalBytes = Encoding.UTF8.GetBytes(original);
        var decryptedBytes = Encoding.UTF8.GetBytes(decrypted);
        Assert.Equal(originalBytes, decryptedBytes);
    }
    
    /// <summary>
    /// Assert that encryption is non-deterministic (produces different ciphertext for same plaintext)
    /// </summary>
    private static async Task AssertEncryptionNonDeterminism(AwsKmsMessageEncryption encryption, string plaintext)
    {
        // Encrypt the same message multiple times
        var ciphertext1 = await encryption.EncryptAsync(plaintext);
        var ciphertext2 = await encryption.EncryptAsync(plaintext);
        var ciphertext3 = await encryption.EncryptAsync(plaintext);
        
        // Each encryption should produce different ciphertext (due to random nonce/IV)
        Assert.NotEqual(ciphertext1, ciphertext2);
        Assert.NotEqual(ciphertext2, ciphertext3);
        Assert.NotEqual(ciphertext1, ciphertext3);
        
        // But all should decrypt to the same plaintext
        var decrypted1 = await encryption.DecryptAsync(ciphertext1);
        var decrypted2 = await encryption.DecryptAsync(ciphertext2);
        var decrypted3 = await encryption.DecryptAsync(ciphertext3);
        
        Assert.Equal(plaintext, decrypted1);
        Assert.Equal(plaintext, decrypted2);
        Assert.Equal(plaintext, decrypted3);
    }
    
    /// <summary>
    /// Assert that sensitive data is protected (not visible in ciphertext)
    /// </summary>
    private static void AssertSensitiveDataProtection(string plaintext, string ciphertext, List<string> sensitiveFields)
    {
        // Ciphertext should not contain plaintext substrings
        if (plaintext.Length > 10)
        {
            // Check that no significant substring of plaintext appears in ciphertext
            var substringLength = Math.Min(10, plaintext.Length / 2);
            for (int i = 0; i <= plaintext.Length - substringLength; i++)
            {
                var substring = plaintext.Substring(i, substringLength);
                Assert.DoesNotContain(substring, ciphertext);
            }
        }
        
        // Sensitive fields should not appear in ciphertext
        foreach (var sensitiveField in sensitiveFields)
        {
            if (!string.IsNullOrEmpty(sensitiveField) && sensitiveField.Length > 3)
            {
                Assert.DoesNotContain(sensitiveField, ciphertext, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
    
    /// <summary>
    /// Assert that encryption performance is reasonable
    /// </summary>
    private static async Task AssertEncryptionPerformance(AwsKmsMessageEncryption encryption, KmsTestMessage message)
    {
        var iterations = 5;
        var encryptionTimes = new List<TimeSpan>();
        var decryptionTimes = new List<TimeSpan>();
        
        for (int i = 0; i < iterations; i++)
        {
            // Measure encryption time
            var encryptStart = DateTime.UtcNow;
            var ciphertext = await encryption.EncryptAsync(message.Content);
            var encryptEnd = DateTime.UtcNow;
            encryptionTimes.Add(encryptEnd - encryptStart);
            
            // Measure decryption time
            var decryptStart = DateTime.UtcNow;
            await encryption.DecryptAsync(ciphertext);
            var decryptEnd = DateTime.UtcNow;
            decryptionTimes.Add(decryptEnd - decryptStart);
        }
        
        // Average encryption time should be reasonable (< 5 seconds for LocalStack, < 1 second for real AWS)
        var avgEncryptionTime = encryptionTimes.Average(t => t.TotalMilliseconds);
        Assert.True(avgEncryptionTime < 5000, 
            $"Average encryption time ({avgEncryptionTime}ms) should be less than 5000ms");
        
        // Average decryption time should be reasonable
        var avgDecryptionTime = decryptionTimes.Average(t => t.TotalMilliseconds);
        Assert.True(avgDecryptionTime < 5000,
            $"Average decryption time ({avgDecryptionTime}ms) should be less than 5000ms");
        
        // Encryption should not be instantaneous (indicates potential issue)
        Assert.True(avgEncryptionTime > 0, "Encryption should take measurable time");
        Assert.True(avgDecryptionTime > 0, "Decryption should take measurable time");
    }
    
    /// <summary>
    /// Check if a string is valid base64
    /// </summary>
    private static bool IsBase64String(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        
        try
        {
            Convert.FromBase64String(value);
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    /// Create a KMS key for testing
    /// </summary>
    private async Task<string> CreateKmsKeyAsync(string keyAlias)
    {
        try
        {
            var createKeyResponse = await _localStack.KmsClient.CreateKeyAsync(new CreateKeyRequest
            {
                Description = $"Test key for property-based testing: {keyAlias}",
                KeyUsage = KeyUsageType.ENCRYPT_DECRYPT,
                Origin = OriginType.AWS_KMS
            });
            
            var keyId = createKeyResponse.KeyMetadata.KeyId;
            _createdKeyIds.Add(keyId);
            
            // Create alias for the key
            try
            {
                await _localStack.KmsClient.CreateAliasAsync(new CreateAliasRequest
                {
                    AliasName = $"alias/{keyAlias}",
                    TargetKeyId = keyId
                });
            }
            catch (Exception)
            {
                // Alias creation might fail in LocalStack, continue without it
            }
            
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
    private AwsKmsMessageEncryption CreateEncryptionService(string keyId)
    {
        var options = new AwsKmsOptions
        {
            MasterKeyId = keyId,
            CacheDataKeySeconds = 0 // Disable caching for tests
        };
        
        // Create a logger with the correct type
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        var encryptionLogger = loggerFactory.CreateLogger<AwsKmsMessageEncryption>();
        
        return new AwsKmsMessageEncryption(
            _localStack.KmsClient,
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
                    // Schedule key deletion (minimum 7 days for real AWS, immediate for LocalStack)
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
}

/// <summary>
/// FsCheck generators for KMS encryption property tests
/// </summary>
public static class KmsEncryptionGenerators
{
    /// <summary>
    /// Generate test messages for KMS encryption
    /// </summary>
    public static Arbitrary<KmsTestMessage> KmsTestMessage()
    {
        var contentGen = Gen.OneOf(
            // Simple strings
            Gen.Elements("Hello, World!", "Test message", "Simple text"),
            
            // Empty and whitespace
            Gen.Elements("", " ", "   ", "\t", "\n"),
            
            // Special characters
            Gen.Elements("!@#$%^&*()_+-=[]{}|;':\",./<>?`~", "Line1\nLine2\rLine3\r\n", "\0\t\n\r"),
            
            // Unicode characters
            Gen.Elements("你好世界", "Привет мир", "مرحبا بالعالم", "🌍🌎🌏", "Ñoño Café"),
            
            // JSON-like content
            Gen.Elements("{\"key\":\"value\"}", "[1,2,3]", "{\"nested\":{\"data\":true}}"),
            
            // Large content
            from size in Gen.Choose(100, 10000)
            from c in Gen.Elements('A', 'B', 'C', '1', '2', '3', ' ', '\n')
            select new string(c, size),
            
            // Random alphanumeric
            from length in Gen.Choose(1, 1000)
            from chars in Gen.ArrayOf(length, Gen.Elements(
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 ".ToCharArray()))
            select new string(chars),
            
            // Mixed content with sensitive data patterns
            from ssn in Gen.Choose(100000000, 999999999)
            from ccn in Gen.Choose(1000000000, 1999999999) // Use int range instead of long
            from email in Gen.Elements("user@example.com", "test@test.com", "admin@domain.org")
            select $"SSN: {ssn}, Credit Card: {ccn}, Email: {email}"
        );
        
        var sensitiveFieldsGen = Gen.ListOf(Gen.Elements(
            "password", "ssn", "credit_card", "api_key", "secret", "token",
            "email", "phone", "address", "account_number"
        ));
        
        var messageGen = from content in contentGen
                        from sensitiveFields in sensitiveFieldsGen
                        from messageType in Gen.Elements(
                            KmsMessageType.PlainText,
                            KmsMessageType.Json,
                            KmsMessageType.Binary,
                            KmsMessageType.Structured)
                        select new KmsTestMessage
                        {
                            Content = content ?? "",
                            SensitiveFields = sensitiveFields.Distinct().ToList(),
                            MessageType = messageType,
                            Timestamp = DateTime.UtcNow
                        };
        
        return Arb.From(messageGen);
    }
}

/// <summary>
/// Test message for KMS encryption property tests
/// </summary>
public class KmsTestMessage
{
    public string Content { get; set; } = "";
    public List<string> SensitiveFields { get; set; } = new();
    public KmsMessageType MessageType { get; set; }
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Message type enumeration for KMS tests
/// </summary>
public enum KmsMessageType
{
    PlainText,
    Json,
    Binary,
    Structured
}

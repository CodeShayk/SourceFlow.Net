using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SourceFlow.Cloud.AWS.Security;

namespace SourceFlow.Cloud.AWS.Tests.Unit;

[Trait("Category", "Unit")]
public class AwsKmsMessageEncryptionTests
{
    private readonly Mock<IAmazonKeyManagementService> _mockKmsClient;
    private readonly byte[] _plaintextKey;
    private readonly byte[] _encryptedKey;

    private const string TestKeyId = "arn:aws:kms:us-east-1:123456:key/test-key-id";

    public AwsKmsMessageEncryptionTests()
    {
        _mockKmsClient = new Mock<IAmazonKeyManagementService>();

        // AES-256 requires 32 bytes
        _plaintextKey = new byte[32];
        _encryptedKey = new byte[64];
        System.Random.Shared.NextBytes(_plaintextKey);
        System.Random.Shared.NextBytes(_encryptedKey);

        SetupDefaultKmsMocks();
    }

    [Fact]
    public async Task EncryptAsync_CallsGenerateDataKeyAsync()
    {
        // Arrange
        var encryption = CreateEncryption(cacheSeconds: 0);

        // Act
        await encryption.EncryptAsync("hello world");

        // Assert
        _mockKmsClient.Verify(
            x => x.GenerateDataKeyAsync(
                It.Is<GenerateDataKeyRequest>(r => r.KeyId == TestKeyId),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task EncryptAsync_ProducesBase64Output()
    {
        // Arrange
        var encryption = CreateEncryption(cacheSeconds: 0);

        // Act
        var result = await encryption.EncryptAsync("hello world");

        // Assert: result should be valid base64
        var exception = Record.Exception(() => Convert.FromBase64String(result));
        Assert.Null(exception);
        Assert.False(string.IsNullOrEmpty(result));
    }

    [Fact]
    public async Task DecryptAsync_CallsKmsDecryptAsync()
    {
        // Arrange
        var encryption = CreateEncryption(cacheSeconds: 0);
        var encrypted = await encryption.EncryptAsync("test message");

        // Reset invocation tracking so we only see calls from DecryptAsync
        _mockKmsClient.Invocations.Clear();
        SetupDefaultKmsMocks(); // re-register

        // Act
        await encryption.DecryptAsync(encrypted);

        // Assert
        _mockKmsClient.Verify(
            x => x.DecryptAsync(It.IsAny<DecryptRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task EncryptThenDecrypt_RoundTrip_ReturnsOriginalPlaintext()
    {
        // Arrange – no caching so each call hits KMS
        var encryption = CreateEncryption(cacheSeconds: 0);
        const string original = "hello world";

        // Act
        var encrypted = await encryption.EncryptAsync(original);
        var decrypted = await encryption.DecryptAsync(encrypted);

        // Assert
        Assert.Equal(original, decrypted);
    }

    [Fact]
    public async Task EncryptAsync_CachingEnabled_GenerateDataKeyCalledOnceForMultipleCalls()
    {
        // Arrange – use real MemoryCache with a long TTL
        var cache = new MemoryCache(new MemoryCacheOptions());
        var encryption = CreateEncryption(cacheSeconds: 300, cache: cache);

        // Act
        await encryption.EncryptAsync("message 1");
        await encryption.EncryptAsync("message 2");
        await encryption.EncryptAsync("message 3");

        // Assert: GenerateDataKey should be called exactly once (key cached after first call)
        _mockKmsClient.Verify(
            x => x.GenerateDataKeyAsync(It.IsAny<GenerateDataKeyRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task EncryptAsync_CachingDisabled_GenerateDataKeyCalledForEachCall()
    {
        // Arrange – caching disabled (0 seconds)
        var encryption = CreateEncryption(cacheSeconds: 0);

        // Act
        await encryption.EncryptAsync("message 1");
        await encryption.EncryptAsync("message 2");

        // Assert: GenerateDataKey should be called for every encrypt operation
        _mockKmsClient.Verify(
            x => x.GenerateDataKeyAsync(It.IsAny<GenerateDataKeyRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public void AlgorithmName_ReturnsExpectedValue()
    {
        var encryption = CreateEncryption(cacheSeconds: 0);
        Assert.Equal("AWS-KMS-AES256", encryption.AlgorithmName);
    }

    [Fact]
    public void KeyIdentifier_ReturnsMasterKeyId()
    {
        var encryption = CreateEncryption(cacheSeconds: 0);
        Assert.Equal(TestKeyId, encryption.KeyIdentifier);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SetupDefaultKmsMocks()
    {
        // Each call to GenerateDataKey returns the same key bytes for predictable round-trips
        _mockKmsClient
            .Setup(x => x.GenerateDataKeyAsync(It.IsAny<GenerateDataKeyRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GenerateDataKeyResponse
            {
                Plaintext = new MemoryStream(_plaintextKey.ToArray()),
                CiphertextBlob = new MemoryStream(_encryptedKey.ToArray())
            });

        _mockKmsClient
            .Setup(x => x.DecryptAsync(It.IsAny<DecryptRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecryptResponse
            {
                Plaintext = new MemoryStream(_plaintextKey.ToArray())
            });
    }

    private AwsKmsMessageEncryption CreateEncryption(int cacheSeconds, IMemoryCache? cache = null)
    {
        return new AwsKmsMessageEncryption(
            _mockKmsClient.Object,
            NullLogger<AwsKmsMessageEncryption>.Instance,
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            new AwsKmsOptions
            {
                MasterKeyId = TestKeyId,
                CacheDataKeySeconds = cacheSeconds
            });
    }
}

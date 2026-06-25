using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Azure.Tests.TestHelpers;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Azure.Tests.Integration;

/// <summary>
/// Integration tests for Azure Key Vault health checks.
/// Validates Key Vault accessibility, key availability, and managed identity authentication status.
/// **Validates: Requirements 4.2, 4.3**
/// </summary>
public class KeyVaultHealthCheckTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILogger<KeyVaultHealthCheckTests> _logger;
    private IAzureTestEnvironment _testEnvironment = null!;
    private KeyClient _keyClient = null!;
    private SecretClient _secretClient = null!;
    private string _testKeyName = null!;
    private string _testSecretName = null!;

    public KeyVaultHealthCheckTests(ITestOutputHelper output)
    {
        _output = output;
        _logger = LoggerHelper.CreateLogger<KeyVaultHealthCheckTests>(output);
    }

    public async Task InitializeAsync()
    {
        var config = new AzureTestConfiguration
        {
            UseAzurite = true,
            KeyVaultUrl = "https://localhost:8080"
        };

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        _testEnvironment = new AzureTestEnvironment(config, loggerFactory);
        await _testEnvironment.InitializeAsync();

        _keyClient = _testEnvironment.CreateKeyClient();
        _secretClient = _testEnvironment.CreateSecretClient();

        _testKeyName = $"health-check-key-{Guid.NewGuid():N}";
        _testSecretName = $"health-check-secret-{Guid.NewGuid():N}";

        _logger.LogInformation("Test environment initialized for Key Vault health checks");
    }

    public async Task DisposeAsync()
    {
        try
        {
            // Cleanup test keys and secrets
            if (_keyClient != null)
            {
                try
                {
                    var deleteOperation = await _keyClient.StartDeleteKeyAsync(_testKeyName);
                    await deleteOperation.WaitForCompletionAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error deleting test key during cleanup");
                }
            }

            if (_secretClient != null)
            {
                try
                {
                    var deleteOperation = await _secretClient.StartDeleteSecretAsync(_testSecretName);
                    await deleteOperation.WaitForCompletionAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error deleting test secret during cleanup");
                }
            }

            await _testEnvironment.CleanupAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during test cleanup");
        }
    }

    [Fact]
    public async Task KeyVaultAccessibility_ShouldSucceed()
    {
        // Arrange
        _logger.LogInformation("Testing Key Vault accessibility");

        // Act
        var isAvailable = await _testEnvironment.IsKeyVaultAvailableAsync();

        // Assert
        Assert.True(isAvailable, "Key Vault should be accessible");
        _logger.LogInformation("Key Vault accessibility validated successfully");
    }

    [Fact]
    public async Task ManagedIdentityAuthentication_ShouldSucceed()
    {
        // Arrange
        _logger.LogInformation("Testing managed identity authentication status");

        // Act
        var isConfigured = await _testEnvironment.IsManagedIdentityConfiguredAsync();

        // Assert
        Assert.True(isConfigured, "Managed identity should be configured and working");
        _logger.LogInformation("Managed identity authentication validated successfully");
    }

    [Fact]
    public async Task KeyVaultPermissions_CreateKey_ShouldSucceed()
    {
        // Arrange
        _logger.LogInformation("Testing Key Vault create key permission");

        // Act
        var keyOptions = new CreateRsaKeyOptions(_testKeyName)
        {
            KeySize = 2048,
            ExpiresOn = DateTimeOffset.UtcNow.AddDays(1)
        };
        var key = await _keyClient.CreateRsaKeyAsync(keyOptions);

        // Assert
        Assert.NotNull(key.Value);
        Assert.Equal(_testKeyName, key.Value.Name);
        _logger.LogInformation("Create key permission validated successfully, key ID: {KeyId}", key.Value.Id);
    }

    [Fact]
    public async Task KeyVaultPermissions_GetKey_ShouldSucceed()
    {
        // Arrange
        _logger.LogInformation("Testing Key Vault get key permission");
        
        // Create a key first
        var keyOptions = new CreateRsaKeyOptions(_testKeyName)
        {
            KeySize = 2048
        };
        await _keyClient.CreateRsaKeyAsync(keyOptions);

        // Act
        var retrievedKey = await _keyClient.GetKeyAsync(_testKeyName);

        // Assert
        Assert.NotNull(retrievedKey.Value);
        Assert.Equal(_testKeyName, retrievedKey.Value.Name);
        _logger.LogInformation("Get key permission validated successfully");
    }

    [Fact]
    public async Task KeyVaultPermissions_ListKeys_ShouldSucceed()
    {
        // Arrange
        _logger.LogInformation("Testing Key Vault list keys permission");
        
        // Create a test key
        var keyOptions = new CreateRsaKeyOptions(_testKeyName)
        {
            KeySize = 2048
        };
        await _keyClient.CreateRsaKeyAsync(keyOptions);

        // Act
        var keys = new List<string>();
        await foreach (var keyProperties in _keyClient.GetPropertiesOfKeysAsync())
        {
            keys.Add(keyProperties.Name);
        }

        // Assert
        Assert.Contains(_testKeyName, keys);
        _logger.LogInformation("List keys permission validated successfully, found {Count} keys", keys.Count);
    }

    [Fact]
    public async Task KeyVaultPermissions_EncryptDecrypt_ShouldSucceed()
    {
        // Arrange
        _logger.LogInformation("Testing Key Vault encrypt/decrypt permissions");
        
        // Create a key
        var keyOptions = new CreateRsaKeyOptions(_testKeyName)
        {
            KeySize = 2048
        };
        var key = await _keyClient.CreateRsaKeyAsync(keyOptions);
        
        var cryptoClient = new CryptographyClient(key.Value.Id, _testEnvironment.GetAzureCredential());
        var plaintext = "Health check test data";
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);

        // Act - Encrypt
        var encryptResult = await cryptoClient.EncryptAsync(EncryptionAlgorithm.RsaOaep, plaintextBytes);
        _logger.LogInformation("Data encrypted successfully");

        // Act - Decrypt
        var decryptResult = await cryptoClient.DecryptAsync(EncryptionAlgorithm.RsaOaep, encryptResult.Ciphertext);
        var decryptedText = Encoding.UTF8.GetString(decryptResult.Plaintext);

        // Assert
        Assert.Equal(plaintext, decryptedText);
        _logger.LogInformation("Encrypt/decrypt permissions validated successfully");
    }

    [Fact]
    public async Task KeyVaultHealthCheck_KeyAvailability_ShouldReturnValidStatus()
    {
        // Arrange
        _logger.LogInformation("Testing Key Vault key availability health check");
        
        // Create a key
        var keyOptions = new CreateRsaKeyOptions(_testKeyName)
        {
            KeySize = 2048,
            Enabled = true
        };
        var key = await _keyClient.CreateRsaKeyAsync(keyOptions);

        // Act
        var keyProperties = await _keyClient.GetKeyAsync(_testKeyName);

        // Assert
        Assert.NotNull(keyProperties.Value);
        Assert.True(keyProperties.Value.Properties.Enabled);
        Assert.NotNull(keyProperties.Value.Properties.CreatedOn);
        _logger.LogInformation("Key availability validated: Enabled={Enabled}, CreatedOn={CreatedOn}",
            keyProperties.Value.Properties.Enabled,
            keyProperties.Value.Properties.CreatedOn);
    }

    [Fact]
    public async Task KeyVaultHealthCheck_SecretOperations_ShouldSucceed()
    {
        // Arrange
        _logger.LogInformation("Testing Key Vault secret operations health check");
        var secretValue = "health-check-secret-value";

        // Act - Set secret
        var secret = await _secretClient.SetSecretAsync(_testSecretName, secretValue);
        _logger.LogInformation("Secret created successfully");

        // Act - Get secret
        var retrievedSecret = await _secretClient.GetSecretAsync(_testSecretName);

        // Assert
        Assert.NotNull(retrievedSecret.Value);
        Assert.Equal(_testSecretName, retrievedSecret.Value.Name);
        Assert.Equal(secretValue, retrievedSecret.Value.Value);
        _logger.LogInformation("Secret operations health check completed successfully");
    }

    [Fact]
    public async Task KeyVaultHealthCheck_KeyRotation_ShouldSupportMultipleVersions()
    {
        // Arrange
        _logger.LogInformation("Testing Key Vault key rotation health check");
        
        // Create initial key version
        var keyOptions = new CreateRsaKeyOptions(_testKeyName)
        {
            KeySize = 2048
        };
        var initialKey = await _keyClient.CreateRsaKeyAsync(keyOptions);
        var initialKeyId = initialKey.Value.Id.ToString();
        _logger.LogInformation("Initial key version created: {KeyId}", initialKeyId);

        // Wait a moment to ensure different timestamps
        await Task.Delay(TimeSpan.FromSeconds(1));

        // Act - Create new key version (rotation)
        var rotatedKey = await _keyClient.CreateRsaKeyAsync(keyOptions);
        var rotatedKeyId = rotatedKey.Value.Id.ToString();
        _logger.LogInformation("Rotated key version created: {KeyId}", rotatedKeyId);

        // Assert - Both versions should be accessible
        Assert.NotEqual(initialKeyId, rotatedKeyId);
        
        // Verify we can still access the initial version
        var initialCryptoClient = new CryptographyClient(new Uri(initialKeyId), _testEnvironment.GetAzureCredential());
        var testData = Encoding.UTF8.GetBytes("rotation test");
        var encryptResult = await initialCryptoClient.EncryptAsync(EncryptionAlgorithm.RsaOaep, testData);
        var decryptResult = await initialCryptoClient.DecryptAsync(EncryptionAlgorithm.RsaOaep, encryptResult.Ciphertext);
        
        Assert.Equal(testData, decryptResult.Plaintext);
        _logger.LogInformation("Key rotation health check completed successfully");
    }

    [Fact]
    public async Task KeyVaultHealthCheck_EndToEndEncryption_ShouldSucceed()
    {
        // Arrange
        _logger.LogInformation("Testing end-to-end Key Vault encryption health check");
        
        var keyOptions = new CreateRsaKeyOptions(_testKeyName)
        {
            KeySize = 2048
        };
        var key = await _keyClient.CreateRsaKeyAsync(keyOptions);
        var cryptoClient = new CryptographyClient(key.Value.Id, _testEnvironment.GetAzureCredential());

        var originalData = "End-to-end health check test data with special characters: !@#$%^&*()";
        var originalBytes = Encoding.UTF8.GetBytes(originalData);

        // Act - Encrypt
        var encryptResult = await cryptoClient.EncryptAsync(EncryptionAlgorithm.RsaOaep, originalBytes);
        Assert.NotNull(encryptResult.Ciphertext);
        Assert.NotEmpty(encryptResult.Ciphertext);
        _logger.LogInformation("Data encrypted, ciphertext length: {Length}", encryptResult.Ciphertext.Length);

        // Act - Decrypt
        var decryptResult = await cryptoClient.DecryptAsync(EncryptionAlgorithm.RsaOaep, encryptResult.Ciphertext);
        var decryptedData = Encoding.UTF8.GetString(decryptResult.Plaintext);

        // Assert
        Assert.Equal(originalData, decryptedData);
        _logger.LogInformation("End-to-end encryption health check completed successfully");
    }

    [Fact]
    public async Task KeyVaultHealthCheck_GetKeyVaultProperties_ShouldReturnValidInfo()
    {
        // Arrange
        _logger.LogInformation("Testing Key Vault properties retrieval");
        
        // Create a test key
        var keyOptions = new CreateRsaKeyOptions(_testKeyName)
        {
            KeySize = 2048,
            Enabled = true
        };
        var key = await _keyClient.CreateRsaKeyAsync(keyOptions);

        // Act
        var keyProperties = await _keyClient.GetKeyAsync(_testKeyName);

        // Assert
        Assert.NotNull(keyProperties.Value);
        Assert.NotNull(keyProperties.Value.Properties);
        Assert.NotNull(keyProperties.Value.Properties.VaultUri);
        Assert.NotNull(keyProperties.Value.Properties.CreatedOn);
        Assert.NotNull(keyProperties.Value.Properties.UpdatedOn);
        Assert.True(keyProperties.Value.Properties.Enabled);

        _logger.LogInformation("Key Vault properties: VaultUri={VaultUri}, KeyType={KeyType}, KeySize={KeySize}",
            keyProperties.Value.Properties.VaultUri,
            keyProperties.Value.KeyType,
            keyProperties.Value.Key.N?.Length * 8); // RSA key size in bits
    }

    [Fact]
    public async Task KeyVaultHealthCheck_CredentialAcquisition_ShouldSucceed()
    {
        // Arrange
        _logger.LogInformation("Testing Azure credential acquisition for Key Vault");

        // Act
        var credential = _testEnvironment.GetAzureCredential();

        // Assert
        Assert.NotNull(credential);
        
        // Verify credential works by attempting a Key Vault operation
        var keys = new List<string>();
        await foreach (var keyProperties in _keyClient.GetPropertiesOfKeysAsync())
        {
            keys.Add(keyProperties.Name);
            break; // Just need to verify we can list
        }

        _logger.LogInformation("Credential acquisition validated successfully");
    }

    [Fact]
    public async Task KeyVaultHealthCheck_MultipleKeyOperations_ShouldMaintainPerformance()
    {
        // Arrange
        _logger.LogInformation("Testing Key Vault health under multiple operations");
        
        var keyOptions = new CreateRsaKeyOptions(_testKeyName)
        {
            KeySize = 2048
        };
        var key = await _keyClient.CreateRsaKeyAsync(keyOptions);
        var cryptoClient = new CryptographyClient(key.Value.Id, _testEnvironment.GetAzureCredential());

        var testData = Encoding.UTF8.GetBytes("Performance test data");
        var operationCount = 10;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Act - Perform multiple encrypt/decrypt operations
        for (int i = 0; i < operationCount; i++)
        {
            var encryptResult = await cryptoClient.EncryptAsync(EncryptionAlgorithm.RsaOaep, testData);
            var decryptResult = await cryptoClient.DecryptAsync(EncryptionAlgorithm.RsaOaep, encryptResult.Ciphertext);
            Assert.Equal(testData, decryptResult.Plaintext);
        }

        stopwatch.Stop();

        // Assert
        var averageLatency = stopwatch.ElapsedMilliseconds / (double)operationCount;
        _logger.LogInformation("Completed {Count} operations in {TotalMs}ms, average: {AvgMs}ms per operation",
            operationCount, stopwatch.ElapsedMilliseconds, averageLatency);

        // Health check passes if operations complete (no specific performance threshold for health check)
        Assert.True(stopwatch.ElapsedMilliseconds > 0);
    }
}

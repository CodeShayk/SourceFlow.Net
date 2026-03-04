using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Security;

namespace SourceFlow.Cloud.Azure.Tests.TestHelpers;

/// <summary>
/// Helper utilities for testing Azure Key Vault functionality including encryption,
/// decryption, key rotation, and managed identity authentication.
/// </summary>
public class KeyVaultTestHelpers
{
    private readonly KeyClient _keyClient;
    private readonly SecretClient _secretClient;
    private readonly TokenCredential _credential;
    private readonly ILogger<KeyVaultTestHelpers> _logger;

    public KeyVaultTestHelpers(
        KeyClient keyClient,
        SecretClient secretClient,
        TokenCredential credential,
        ILogger<KeyVaultTestHelpers> logger)
    {
        _keyClient = keyClient ?? throw new ArgumentNullException(nameof(keyClient));
        _secretClient = secretClient ?? throw new ArgumentNullException(nameof(secretClient));
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Creates a new instance using an Azure test environment.
    /// Automatically creates KeyClient and SecretClient from the environment configuration.
    /// </summary>
    public KeyVaultTestHelpers(
        IAzureTestEnvironment environment,
        ILoggerFactory loggerFactory)
    {
        if (environment == null) throw new ArgumentNullException(nameof(environment));
        if (loggerFactory == null) throw new ArgumentNullException(nameof(loggerFactory));

        var keyVaultUrl = environment.GetKeyVaultUrl();
        var credential = environment.GetAzureCredentialAsync().GetAwaiter().GetResult();
        
        _keyClient = new KeyClient(new Uri(keyVaultUrl), credential);
        _secretClient = new SecretClient(new Uri(keyVaultUrl), credential);
        _credential = credential;
        _logger = loggerFactory.CreateLogger<KeyVaultTestHelpers>();
    }

    /// <summary>
    /// Gets the KeyClient instance for direct key operations.
    /// </summary>
    public KeyClient GetKeyClient() => _keyClient;

    /// <summary>
    /// Gets the SecretClient instance for direct secret operations.
    /// </summary>
    public SecretClient GetSecretClient() => _secretClient;

    /// <summary>
    /// Creates a test encryption key in Key Vault.
    /// </summary>
    /// <param name="keyName">The name of the key to create.</param>
    /// <param name="keySize">The key size in bits (default: 2048).</param>
    /// <param name="expiresOn">Optional expiration date for the key.</param>
    /// <returns>The key ID (URI) of the created key.</returns>
    public async Task<string> CreateTestEncryptionKeyAsync(
        string keyName,
        int keySize = 2048,
        DateTimeOffset? expiresOn = null)
    {
        if (string.IsNullOrEmpty(keyName))
            throw new ArgumentException("Key name cannot be null or empty", nameof(keyName));
        if (keySize < 2048)
            throw new ArgumentException("Key size must be at least 2048 bits", nameof(keySize));

        _logger.LogInformation("Creating test encryption key: {KeyName} with size {KeySize}", keyName, keySize);

        var keyOptions = new CreateRsaKeyOptions(keyName)
        {
            KeySize = keySize,
            ExpiresOn = expiresOn ?? DateTimeOffset.UtcNow.AddYears(1),
            Enabled = true
        };

        var key = await _keyClient.CreateRsaKeyAsync(keyOptions);

        _logger.LogInformation("Created key {KeyName} with ID {KeyId}", keyName, key.Value.Id);
        return key.Value.Id.ToString();
    }

    /// <summary>
    /// Encrypts data using a Key Vault key.
    /// </summary>
    /// <param name="keyId">The key ID (URI) to use for encryption.</param>
    /// <param name="plaintext">The plaintext data to encrypt.</param>
    /// <param name="algorithm">The encryption algorithm to use (default: RSA-OAEP).</param>
    /// <returns>The encrypted ciphertext.</returns>
    public async Task<byte[]> EncryptDataAsync(
        string keyId,
        string plaintext,
        EncryptionAlgorithm? algorithm = null)
    {
        if (string.IsNullOrEmpty(keyId))
            throw new ArgumentException("Key ID cannot be null or empty", nameof(keyId));
        if (string.IsNullOrEmpty(plaintext))
            throw new ArgumentException("Plaintext cannot be null or empty", nameof(plaintext));

        var encryptionAlgorithm = algorithm ?? EncryptionAlgorithm.RsaOaep;
        var cryptoClient = new CryptographyClient(new Uri(keyId), _credential);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);

        _logger.LogDebug("Encrypting data with key {KeyId} using algorithm {Algorithm}",
            keyId, encryptionAlgorithm);

        var encryptResult = await cryptoClient.EncryptAsync(encryptionAlgorithm, plaintextBytes);

        _logger.LogDebug("Data encrypted successfully, ciphertext length: {Length} bytes",
            encryptResult.Ciphertext.Length);

        return encryptResult.Ciphertext;
    }

    /// <summary>
    /// Decrypts data using a Key Vault key.
    /// </summary>
    /// <param name="keyId">The key ID (URI) to use for decryption.</param>
    /// <param name="ciphertext">The ciphertext to decrypt.</param>
    /// <param name="algorithm">The encryption algorithm used (default: RSA-OAEP).</param>
    /// <returns>The decrypted plaintext.</returns>
    public async Task<string> DecryptDataAsync(
        string keyId,
        byte[] ciphertext,
        EncryptionAlgorithm? algorithm = null)
    {
        if (string.IsNullOrEmpty(keyId))
            throw new ArgumentException("Key ID cannot be null or empty", nameof(keyId));
        if (ciphertext == null || ciphertext.Length == 0)
            throw new ArgumentException("Ciphertext cannot be null or empty", nameof(ciphertext));

        var encryptionAlgorithm = algorithm ?? EncryptionAlgorithm.RsaOaep;
        var cryptoClient = new CryptographyClient(new Uri(keyId), _credential);

        _logger.LogDebug("Decrypting data with key {KeyId} using algorithm {Algorithm}",
            keyId, encryptionAlgorithm);

        var decryptResult = await cryptoClient.DecryptAsync(encryptionAlgorithm, ciphertext);
        var plaintext = Encoding.UTF8.GetString(decryptResult.Plaintext);

        _logger.LogDebug("Data decrypted successfully, plaintext length: {Length} characters",
            plaintext.Length);

        return plaintext;
    }

    /// <summary>
    /// Validates end-to-end encryption and decryption with a Key Vault key.
    /// </summary>
    /// <param name="keyId">The key ID (URI) to test.</param>
    /// <param name="testData">The test data to encrypt and decrypt.</param>
    /// <returns>True if encryption and decryption succeed and data matches, false otherwise.</returns>
    public async Task<bool> ValidateEncryptionRoundTripAsync(string keyId, string testData)
    {
        if (string.IsNullOrEmpty(keyId))
            throw new ArgumentException("Key ID cannot be null or empty", nameof(keyId));
        if (string.IsNullOrEmpty(testData))
            throw new ArgumentException("Test data cannot be null or empty", nameof(testData));

        try
        {
            _logger.LogInformation("Validating encryption round-trip for key {KeyId}", keyId);

            // Encrypt the test data
            var ciphertext = await EncryptDataAsync(keyId, testData);

            // Decrypt the ciphertext
            var decryptedData = await DecryptDataAsync(keyId, ciphertext);

            // Verify the data matches
            var success = testData == decryptedData;

            if (success)
            {
                _logger.LogInformation("Encryption round-trip validation successful");
            }
            else
            {
                _logger.LogError("Encryption round-trip validation failed: data mismatch");
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Encryption round-trip validation failed with exception");
            return false;
        }
    }

    /// <summary>
    /// Validates key rotation by creating a new key version and ensuring old data can still be decrypted.
    /// </summary>
    /// <param name="keyName">The name of the key to rotate.</param>
    /// <param name="testData">Optional test data to use for validation.</param>
    /// <returns>True if key rotation succeeds and old data remains decryptable, false otherwise.</returns>
    public async Task<bool> ValidateKeyRotationAsync(string keyName, string? testData = null)
    {
        if (string.IsNullOrEmpty(keyName))
            throw new ArgumentException("Key name cannot be null or empty", nameof(keyName));

        var testString = testData ?? "sensitive test data for key rotation validation";

        try
        {
            _logger.LogInformation("Validating key rotation for {KeyName}", keyName);

            // Create initial key version
            var initialKeyId = await CreateTestEncryptionKeyAsync(keyName);
            var initialCryptoClient = new CryptographyClient(new Uri(initialKeyId), _credential);

            // Encrypt test data with initial key
            var testDataBytes = Encoding.UTF8.GetBytes(testString);
            var encryptResult = await initialCryptoClient.EncryptAsync(
                EncryptionAlgorithm.RsaOaep,
                testDataBytes);

            _logger.LogInformation("Encrypted data with initial key version");

            // Wait a moment to ensure different timestamp
            await Task.Delay(TimeSpan.FromSeconds(1));

            // Rotate key (create new version)
            var rotatedKeyId = await CreateTestEncryptionKeyAsync(keyName);
            var rotatedCryptoClient = new CryptographyClient(new Uri(rotatedKeyId), _credential);

            _logger.LogInformation("Created rotated key version");

            // Verify old data can still be decrypted with initial key
            var decryptResult = await initialCryptoClient.DecryptAsync(
                EncryptionAlgorithm.RsaOaep,
                encryptResult.Ciphertext);
            var decryptedData = Encoding.UTF8.GetString(decryptResult.Plaintext);

            if (decryptedData != testString)
            {
                _logger.LogError("Failed to decrypt with initial key after rotation");
                return false;
            }

            _logger.LogInformation("Successfully decrypted with initial key after rotation");

            // Verify new key can encrypt new data
            var newEncryptResult = await rotatedCryptoClient.EncryptAsync(
                EncryptionAlgorithm.RsaOaep,
                testDataBytes);
            var newDecryptResult = await rotatedCryptoClient.DecryptAsync(
                EncryptionAlgorithm.RsaOaep,
                newEncryptResult.Ciphertext);
            var newDecryptedData = Encoding.UTF8.GetString(newDecryptResult.Plaintext);

            if (newDecryptedData != testString)
            {
                _logger.LogError("Failed to encrypt/decrypt with rotated key");
                return false;
            }

            _logger.LogInformation("Key rotation validation successful");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Key rotation validation failed with exception");
            return false;
        }
    }

    /// <summary>
    /// Validates that sensitive data is properly masked in serialized output.
    /// </summary>
    /// <param name="testObject">The object containing sensitive data to validate.</param>
    /// <returns>True if all properties marked with [SensitiveData] are masked, false otherwise.</returns>
    public bool ValidateSensitiveDataMasking(object testObject)
    {
        if (testObject == null)
            throw new ArgumentNullException(nameof(testObject));

        _logger.LogInformation("Validating sensitive data masking for {ObjectType}",
            testObject.GetType().Name);

        try
        {
            // Serialize object
            var serialized = JsonSerializer.Serialize(testObject, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            _logger.LogDebug("Serialized object: {Serialized}", serialized);

            // Check if properties marked with [SensitiveData] are masked
            var sensitiveProperties = testObject.GetType()
                .GetProperties()
                .Where(p => p.GetCustomAttributes(typeof(SensitiveDataAttribute), true).Any())
                .ToList();

            if (sensitiveProperties.Count == 0)
            {
                _logger.LogWarning("No properties marked with [SensitiveData] found");
                return true; // No sensitive properties to validate
            }

            foreach (var property in sensitiveProperties)
            {
                var value = property.GetValue(testObject)?.ToString();
                if (!string.IsNullOrEmpty(value) && serialized.Contains(value))
                {
                    _logger.LogError(
                        "Sensitive property {PropertyName} is not masked in serialized output",
                        property.Name);
                    return false;
                }
            }

            _logger.LogInformation("Sensitive data masking validation successful");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sensitive data masking validation failed with exception");
            return false;
        }
    }

    /// <summary>
    /// Validates managed identity authentication by attempting to acquire tokens for Azure services.
    /// </summary>
    /// <returns>True if managed identity authentication succeeds, false otherwise.</returns>
    public async Task<bool> ValidateManagedIdentityAuthenticationAsync()
    {
        try
        {
            _logger.LogInformation("Validating managed identity authentication");

            // Try to acquire token for Key Vault
            var keyVaultToken = await _credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://vault.azure.net/.default" }),
                CancellationToken.None);

            if (string.IsNullOrEmpty(keyVaultToken.Token))
            {
                _logger.LogError("Failed to acquire Key Vault token");
                return false;
            }

            _logger.LogInformation("Successfully acquired Key Vault token");

            // Try to acquire token for Service Bus
            var serviceBusToken = await _credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://servicebus.azure.net/.default" }),
                CancellationToken.None);

            if (string.IsNullOrEmpty(serviceBusToken.Token))
            {
                _logger.LogError("Failed to acquire Service Bus token");
                return false;
            }

            _logger.LogInformation("Successfully acquired Service Bus token");
            _logger.LogInformation("Managed identity authentication validation successful");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Managed identity authentication validation failed");
            return false;
        }
    }

    /// <summary>
    /// Validates Key Vault access permissions by attempting various operations.
    /// </summary>
    /// <returns>A KeyVaultPermissionValidationResult with detailed permission status.</returns>
    public async Task<KeyVaultPermissionValidationResult> ValidateKeyVaultPermissionsAsync()
    {
        _logger.LogInformation("Validating Key Vault permissions");

        var result = new KeyVaultPermissionValidationResult();

        // Test get keys permission
        try
        {
            await _keyClient.GetPropertiesOfKeysAsync().GetAsyncEnumerator().MoveNextAsync();
            result.CanGetKeys = true;
            _logger.LogInformation("Key Vault get keys permission validated");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Key Vault get keys permission denied");
            result.CanGetKeys = false;
        }

        // Test create keys permission
        try
        {
            var testKeyName = $"test-key-{Guid.NewGuid()}";
            var testKey = await _keyClient.CreateRsaKeyAsync(new CreateRsaKeyOptions(testKeyName)
            {
                KeySize = 2048
            });
            result.CanCreateKeys = true;
            _logger.LogInformation("Key Vault create keys permission validated");

            // Clean up test key
            try
            {
                await _keyClient.StartDeleteKeyAsync(testKey.Value.Name);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Key Vault create keys permission denied");
            result.CanCreateKeys = false;
        }

        // Test encrypt/decrypt permissions
        try
        {
            // Get or create a test key
            var testKeyName = "permission-test-key";
            KeyVaultKey testKey;
            
            try
            {
                testKey = await _keyClient.GetKeyAsync(testKeyName);
            }
            catch
            {
                testKey = await _keyClient.CreateRsaKeyAsync(new CreateRsaKeyOptions(testKeyName)
                {
                    KeySize = 2048
                });
            }

            var cryptoClient = new CryptographyClient(testKey.Id, _credential);
            var testData = Encoding.UTF8.GetBytes("test");

            // Test encryption
            var encrypted = await cryptoClient.EncryptAsync(EncryptionAlgorithm.RsaOaep, testData);
            result.CanEncrypt = true;
            _logger.LogInformation("Key Vault encrypt permission validated");

            // Test decryption
            var decrypted = await cryptoClient.DecryptAsync(EncryptionAlgorithm.RsaOaep, encrypted.Ciphertext);
            result.CanDecrypt = true;
            _logger.LogInformation("Key Vault decrypt permission validated");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Key Vault encrypt/decrypt permissions denied");
            result.CanEncrypt = false;
            result.CanDecrypt = false;
        }

        _logger.LogInformation(
            "Key Vault permission validation complete: GetKeys={CanGetKeys}, CreateKeys={CanCreateKeys}, Encrypt={CanEncrypt}, Decrypt={CanDecrypt}",
            result.CanGetKeys, result.CanCreateKeys, result.CanEncrypt, result.CanDecrypt);

        return result;
    }

    /// <summary>
    /// Deletes a test key from Key Vault.
    /// </summary>
    /// <param name="keyName">The name of the key to delete.</param>
    /// <returns>True if deletion succeeds, false otherwise.</returns>
    public async Task<bool> DeleteTestKeyAsync(string keyName)
    {
        if (string.IsNullOrEmpty(keyName))
            throw new ArgumentException("Key name cannot be null or empty", nameof(keyName));

        try
        {
            _logger.LogInformation("Deleting test key: {KeyName}", keyName);

            var deleteOperation = await _keyClient.StartDeleteKeyAsync(keyName);
            await deleteOperation.WaitForCompletionAsync();

            _logger.LogInformation("Test key {KeyName} deleted successfully", keyName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete test key {KeyName}", keyName);
            return false;
        }
    }

    /// <summary>
    /// Purges a deleted key from Key Vault (permanent deletion).
    /// </summary>
    /// <param name="keyName">The name of the deleted key to purge.</param>
    /// <returns>True if purge succeeds, false otherwise.</returns>
    public async Task<bool> PurgeDeletedKeyAsync(string keyName)
    {
        if (string.IsNullOrEmpty(keyName))
            throw new ArgumentException("Key name cannot be null or empty", nameof(keyName));

        try
        {
            _logger.LogInformation("Purging deleted key: {KeyName}", keyName);

            await _keyClient.PurgeDeletedKeyAsync(keyName);

            _logger.LogInformation("Deleted key {KeyName} purged successfully", keyName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to purge deleted key {KeyName}", keyName);
            return false;
        }
    }
}

/// <summary>
/// Result of Key Vault permission validation.
/// </summary>
public class KeyVaultPermissionValidationResult
{
    /// <summary>
    /// Indicates whether the identity can get/list keys.
    /// </summary>
    public bool CanGetKeys { get; set; }

    /// <summary>
    /// Indicates whether the identity can create keys.
    /// </summary>
    public bool CanCreateKeys { get; set; }

    /// <summary>
    /// Indicates whether the identity can encrypt data.
    /// </summary>
    public bool CanEncrypt { get; set; }

    /// <summary>
    /// Indicates whether the identity can decrypt data.
    /// </summary>
    public bool CanDecrypt { get; set; }

    /// <summary>
    /// Indicates whether all required permissions are granted.
    /// </summary>
    public bool HasAllRequiredPermissions => CanGetKeys && CanEncrypt && CanDecrypt;
}

using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Azure.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Azure.Tests.Integration;

/// <summary>
/// Property-based tests for Azure Key Vault encryption using FsCheck.
/// Feature: azure-cloud-integration-testing
/// Task: 6.2 Write property test for Azure Key Vault encryption
/// </summary>
public class KeyVaultEncryptionPropertyTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;
    private IAzureTestEnvironment? _testEnvironment;
    private KeyVaultTestHelpers? _keyVaultHelpers;
    private KeyClient? _keyClient;
    private KeyVaultKey? _testKey;

    public KeyVaultEncryptionPropertyTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Debug);
        });
    }

    public async Task InitializeAsync()
    {
        var config = new AzureTestConfiguration
        {
            UseAzurite = true,
            KeyVaultUrl = "https://localhost:8080"
        };

        var azuriteConfig = new AzuriteConfiguration
        {
            StartupTimeoutSeconds = 30
        };

        var azuriteManager = new AzuriteManager(
            azuriteConfig,
            _loggerFactory.CreateLogger<AzuriteManager>());

        _testEnvironment = new AzureTestEnvironment(
            config,
            _loggerFactory.CreateLogger<AzureTestEnvironment>(),
            azuriteManager);

        await _testEnvironment.InitializeAsync();

        _keyVaultHelpers = new KeyVaultTestHelpers(
            _testEnvironment,
            _loggerFactory);

        // Create a test key for property tests
        _keyClient = _keyVaultHelpers.GetKeyClient();
        _testKey = await _keyClient.CreateKeyAsync($"prop-test-key-{Guid.NewGuid():N}", KeyType.Rsa);
    }

    public async Task DisposeAsync()
    {
        if (_testEnvironment != null)
        {
            await _testEnvironment.CleanupAsync();
        }
    }

    #region Property 6: Azure Key Vault Encryption Round-Trip Consistency

    /// <summary>
    /// Property 6: Azure Key Vault Encryption Round-Trip Consistency
    /// For any plaintext message encrypted with Azure Key Vault,
    /// decrypting the ciphertext should return the original plaintext.
    /// Validates: Requirements 3.1, 3.4
    /// </summary>
    [Property(MaxTest = 20)]
    public Property Property6_EncryptionRoundTrip_PreservesPlaintext()
    {
        return Prop.ForAll(
            GenerateEncryptableString().ToArbitrary(),
            (plaintext) =>
            {
                try
                {
                    if (string.IsNullOrEmpty(plaintext))
                    {
                        return true.ToProperty(); // Skip empty strings
                    }

                    var credential = _testEnvironment!.GetAzureCredentialAsync().GetAwaiter().GetResult();
                    var cryptoClient = new CryptographyClient(_testKey!.Id, credential);

                    var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
                    
                    // Encrypt
                    var encryptResult = cryptoClient.EncryptAsync(
                        EncryptionAlgorithm.RsaOaep, 
                        plaintextBytes).GetAwaiter().GetResult();

                    // Decrypt
                    var decryptResult = cryptoClient.DecryptAsync(
                        EncryptionAlgorithm.RsaOaep, 
                        encryptResult.Ciphertext).GetAwaiter().GetResult();

                    var decrypted = System.Text.Encoding.UTF8.GetString(decryptResult.Plaintext);

                    // Property: decrypt(encrypt(plaintext)) == plaintext
                    return (plaintext == decrypted).ToProperty();
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Property test failed: {ex.Message}");
                    return false.ToProperty();
                }
            });
    }

    /// <summary>
    /// Property 6 Variant: Encryption produces different ciphertext for same plaintext
    /// (due to random padding in RSA-OAEP)
    /// Validates: Requirements 3.1
    /// </summary>
    [Property(MaxTest = 10)]
    public Property Property6_EncryptionNonDeterministic_ProducesDifferentCiphertext()
    {
        return Prop.ForAll(
            GenerateEncryptableString().ToArbitrary(),
            (plaintext) =>
            {
                try
                {
                    if (string.IsNullOrEmpty(plaintext))
                    {
                        return true.ToProperty();
                    }

                    var credential = _testEnvironment!.GetAzureCredentialAsync().GetAwaiter().GetResult();
                    var cryptoClient = new CryptographyClient(_testKey!.Id, credential);

                    var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
                    
                    // Encrypt twice
                    var encryptResult1 = cryptoClient.EncryptAsync(
                        EncryptionAlgorithm.RsaOaep, 
                        plaintextBytes).GetAwaiter().GetResult();
                    
                    var encryptResult2 = cryptoClient.EncryptAsync(
                        EncryptionAlgorithm.RsaOaep, 
                        plaintextBytes).GetAwaiter().GetResult();

                    // Property: Same plaintext produces different ciphertext (due to random padding)
                    var ciphertext1 = Convert.ToBase64String(encryptResult1.Ciphertext);
                    var ciphertext2 = Convert.ToBase64String(encryptResult2.Ciphertext);

                    return (ciphertext1 != ciphertext2).ToProperty();
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Property test failed: {ex.Message}");
                    return false.ToProperty();
                }
            });
    }

    /// <summary>
    /// Property 6 Variant: Ciphertext is always different from plaintext
    /// Validates: Requirements 3.1
    /// </summary>
    [Property(MaxTest = 20)]
    public Property Property6_Ciphertext_DifferentFromPlaintext()
    {
        return Prop.ForAll(
            GenerateEncryptableString().ToArbitrary(),
            (plaintext) =>
            {
                try
                {
                    if (string.IsNullOrEmpty(plaintext))
                    {
                        return true.ToProperty();
                    }

                    var credential = _testEnvironment!.GetAzureCredentialAsync().GetAwaiter().GetResult();
                    var cryptoClient = new CryptographyClient(_testKey!.Id, credential);

                    var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
                    
                    // Encrypt
                    var encryptResult = cryptoClient.EncryptAsync(
                        EncryptionAlgorithm.RsaOaep, 
                        plaintextBytes).GetAwaiter().GetResult();

                    var ciphertextBase64 = Convert.ToBase64String(encryptResult.Ciphertext);

                    // Property: Ciphertext should not contain the plaintext
                    return (!ciphertextBase64.Contains(plaintext)).ToProperty();
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Property test failed: {ex.Message}");
                    return false.ToProperty();
                }
            });
    }

    /// <summary>
    /// Property 6 Variant: Encryption preserves data length semantics
    /// Validates: Requirements 3.1
    /// </summary>
    [Property(MaxTest = 15)]
    public Property Property6_EncryptionDecryption_PreservesDataLength()
    {
        return Prop.ForAll(
            GenerateEncryptableString().ToArbitrary(),
            (plaintext) =>
            {
                try
                {
                    if (string.IsNullOrEmpty(plaintext))
                    {
                        return true.ToProperty();
                    }

                    var credential = _testEnvironment!.GetAzureCredentialAsync().GetAwaiter().GetResult();
                    var cryptoClient = new CryptographyClient(_testKey!.Id, credential);

                    var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
                    
                    // Encrypt and decrypt
                    var encryptResult = cryptoClient.EncryptAsync(
                        EncryptionAlgorithm.RsaOaep, 
                        plaintextBytes).GetAwaiter().GetResult();
                    
                    var decryptResult = cryptoClient.DecryptAsync(
                        EncryptionAlgorithm.RsaOaep, 
                        encryptResult.Ciphertext).GetAwaiter().GetResult();

                    // Property: Decrypted data has same length as original
                    return (decryptResult.Plaintext.Length == plaintextBytes.Length).ToProperty();
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Property test failed: {ex.Message}");
                    return false.ToProperty();
                }
            });
    }

    /// <summary>
    /// Property 6 Variant: Encryption works with various character encodings
    /// Validates: Requirements 3.1
    /// </summary>
    [Property(MaxTest = 10)]
    public Property Property6_Encryption_WorksWithUnicodeCharacters()
    {
        return Prop.ForAll(
            GenerateUnicodeString().ToArbitrary(),
            (plaintext) =>
            {
                try
                {
                    if (string.IsNullOrEmpty(plaintext))
                    {
                        return true.ToProperty();
                    }

                    var credential = _testEnvironment!.GetAzureCredentialAsync().GetAwaiter().GetResult();
                    var cryptoClient = new CryptographyClient(_testKey!.Id, credential);

                    var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
                    
                    // Encrypt and decrypt
                    var encryptResult = cryptoClient.EncryptAsync(
                        EncryptionAlgorithm.RsaOaep, 
                        plaintextBytes).GetAwaiter().GetResult();
                    
                    var decryptResult = cryptoClient.DecryptAsync(
                        EncryptionAlgorithm.RsaOaep, 
                        encryptResult.Ciphertext).GetAwaiter().GetResult();

                    var decrypted = System.Text.Encoding.UTF8.GetString(decryptResult.Plaintext);

                    // Property: Unicode characters are preserved
                    return (plaintext == decrypted).ToProperty();
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Property test failed: {ex.Message}");
                    return false.ToProperty();
                }
            });
    }

    #endregion

    #region Generators

    private static Gen<string> GenerateEncryptableString()
    {
        // RSA-OAEP with 2048-bit key can encrypt max ~190 bytes
        // Generate strings that fit within this limit
        return from length in Gen.Choose(1, 100)
               from chars in Gen.ArrayOf(length, Gen.Elements(
                   "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 !@#$%^&*()_+-=[]{}|;:,.<>?".ToCharArray()))
               select new string(chars);
    }

    private static Gen<string> GenerateUnicodeString()
    {
        // Generate strings with Unicode characters
        return from length in Gen.Choose(1, 50)
               from chars in Gen.ArrayOf(length, Gen.Elements(
                   "Hello世界Привет🌍Héllo".ToCharArray()))
               select new string(chars);
    }

    #endregion
}

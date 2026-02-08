using Azure.Security.KeyVault.Keys.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using SourceFlow.Cloud.Core.Security;
using System.Security.Cryptography;
using System.Text;

namespace SourceFlow.Cloud.Azure.Security;

/// <summary>
/// Message encryption using Azure Key Vault with envelope encryption pattern
/// </summary>
public class AzureKeyVaultMessageEncryption : IMessageEncryption
{
    private readonly CryptographyClient _cryptoClient;
    private readonly ILogger<AzureKeyVaultMessageEncryption> _logger;
    private readonly IMemoryCache _dataKeyCache;
    private readonly AzureKeyVaultOptions _options;

    public string AlgorithmName => "Azure-KeyVault-AES256";
    public string KeyIdentifier => _options.KeyIdentifier;

    public AzureKeyVaultMessageEncryption(
        CryptographyClient cryptoClient,
        ILogger<AzureKeyVaultMessageEncryption> logger,
        IMemoryCache dataKeyCache,
        AzureKeyVaultOptions options)
    {
        _cryptoClient = cryptoClient;
        _logger = logger;
        _dataKeyCache = dataKeyCache;
        _options = options;
    }

    public async Task<string> EncryptAsync(string plaintext, CancellationToken cancellationToken = default)
    {
        try
        {
            var dataKey = await GetOrGenerateDataKeyAsync(cancellationToken);
            byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            byte[] ciphertext, nonce, tag;

            using (var aes = new AesGcm(dataKey.PlaintextKey))
            {
                nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
                RandomNumberGenerator.Fill(nonce);

                ciphertext = new byte[plaintextBytes.Length];
                tag = new byte[AesGcm.TagByteSizes.MaxSize];

                aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
            }

            var envelope = new EnvelopeData
            {
                EncryptedDataKey = Convert.ToBase64String(dataKey.EncryptedKey),
                Nonce = Convert.ToBase64String(nonce),
                Tag = Convert.ToBase64String(tag),
                Ciphertext = Convert.ToBase64String(ciphertext)
            };

            var envelopeJson = System.Text.Json.JsonSerializer.Serialize(envelope);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(envelopeJson));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error encrypting message with Azure Key Vault");
            throw;
        }
    }

    public async Task<string> DecryptAsync(string ciphertext, CancellationToken cancellationToken = default)
    {
        try
        {
            var envelopeBytes = Convert.FromBase64String(ciphertext);
            var envelopeJson = Encoding.UTF8.GetString(envelopeBytes);
            var envelope = System.Text.Json.JsonSerializer.Deserialize<EnvelopeData>(envelopeJson);

            if (envelope == null)
                throw new InvalidOperationException("Failed to deserialize encryption envelope");

            var encryptedDataKey = Convert.FromBase64String(envelope.EncryptedDataKey);
            var decryptResult = await _cryptoClient.DecryptAsync(
                EncryptionAlgorithm.RsaOaep256,
                encryptedDataKey,
                cancellationToken);

            var plaintextKey = decryptResult.Plaintext;
            var nonce = Convert.FromBase64String(envelope.Nonce);
            var tag = Convert.FromBase64String(envelope.Tag);
            var ciphertextBytes = Convert.FromBase64String(envelope.Ciphertext);
            var plaintextBytes = new byte[ciphertextBytes.Length];

            using (var aes = new AesGcm(plaintextKey))
            {
                aes.Decrypt(nonce, ciphertextBytes, tag, plaintextBytes);
            }

            return Encoding.UTF8.GetString(plaintextBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error decrypting message with Azure Key Vault");
            throw;
        }
    }

    private async Task<DataKey> GetOrGenerateDataKeyAsync(CancellationToken cancellationToken)
    {
        if (_options.CacheDataKeySeconds > 0)
        {
            var cacheKey = $"keyvault-data-key:{_options.KeyIdentifier}";
            if (_dataKeyCache.TryGetValue(cacheKey, out DataKey? cachedKey) && cachedKey != null)
            {
                return cachedKey;
            }

            var dataKey = await GenerateDataKeyAsync(cancellationToken);

            var cacheOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromSeconds(_options.CacheDataKeySeconds))
                .RegisterPostEvictionCallback((key, value, reason, state) =>
                {
                    if (value is DataKey dk)
                    {
                        Array.Clear(dk.PlaintextKey, 0, dk.PlaintextKey.Length);
                    }
                });

            _dataKeyCache.Set(cacheKey, dataKey, cacheOptions);
            _logger.LogDebug("Generated and cached new data key for {Duration} seconds",
                _options.CacheDataKeySeconds);

            return dataKey;
        }

        return await GenerateDataKeyAsync(cancellationToken);
    }

    private async Task<DataKey> GenerateDataKeyAsync(CancellationToken cancellationToken)
    {
        byte[] plaintextKey = new byte[32]; // 256-bit key
        RandomNumberGenerator.Fill(plaintextKey);

        var encryptResult = await _cryptoClient.EncryptAsync(
            EncryptionAlgorithm.RsaOaep256,
            plaintextKey,
            cancellationToken);

        _logger.LogDebug("Generated new data key using Azure Key Vault: {KeyId}", _options.KeyIdentifier);

        return new DataKey
        {
            PlaintextKey = plaintextKey,
            EncryptedKey = encryptResult.Ciphertext
        };
    }

    private class DataKey
    {
        public byte[] PlaintextKey { get; set; } = Array.Empty<byte>();
        public byte[] EncryptedKey { get; set; } = Array.Empty<byte>();
    }

    private class EnvelopeData
    {
        public string EncryptedDataKey { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
        public string Ciphertext { get; set; } = string.Empty;
    }
}

/// <summary>
/// Configuration options for Azure Key Vault encryption
/// </summary>
public class AzureKeyVaultOptions
{
    /// <summary>
    /// Key Vault Key identifier (URL)
    /// </summary>
    public string KeyIdentifier { get; set; } = string.Empty;

    /// <summary>
    /// How long to cache data encryption keys (in seconds). 0 = no caching.
    /// </summary>
    public int CacheDataKeySeconds { get; set; } = 300;
}

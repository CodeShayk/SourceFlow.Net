using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using SourceFlow.Cloud.Security;
using System.Security.Cryptography;
using System.Text;

namespace SourceFlow.Cloud.AWS.Security;

/// <summary>
/// Message encryption using AWS KMS (Key Management Service) with envelope encryption pattern
/// </summary>
public class AwsKmsMessageEncryption : IMessageEncryption
{
    private readonly IAmazonKeyManagementService _kmsClient;
    private readonly ILogger<AwsKmsMessageEncryption> _logger;
    private readonly IMemoryCache _dataKeyCache;
    private readonly AwsKmsOptions _options;

    public string AlgorithmName => "AWS-KMS-AES256";
    public string KeyIdentifier => _options.MasterKeyId;

    public AwsKmsMessageEncryption(
        IAmazonKeyManagementService kmsClient,
        ILogger<AwsKmsMessageEncryption> logger,
        IMemoryCache dataKeyCache,
        AwsKmsOptions options)
    {
        _kmsClient = kmsClient;
        _logger = logger;
        _dataKeyCache = dataKeyCache;
        _options = options;
    }

    public async Task<string> EncryptAsync(string plaintext, CancellationToken cancellationToken = default)
    {
        try
        {
            // 1. Get or generate data encryption key (DEK)
            var dataKey = await GetOrGenerateDataKeyAsync(cancellationToken);

            // 2. Encrypt the plaintext using AES-256-GCM
            byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            byte[] ciphertext;
            byte[] nonce;
            byte[] tag;

            using (var aes = new AesGcm(dataKey.PlaintextKey))
            {
                // Generate random nonce (12 bytes for GCM)
                nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
                RandomNumberGenerator.Fill(nonce);

                // Prepare buffers
                ciphertext = new byte[plaintextBytes.Length];
                tag = new byte[AesGcm.TagByteSizes.MaxSize];

                // Encrypt
                aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
            }

            // 3. Create envelope: encryptedDataKey:nonce:tag:ciphertext (all base64)
            var envelope = new EnvelopeData
            {
                EncryptedDataKey = Convert.ToBase64String(dataKey.EncryptedKey),
                Nonce = Convert.ToBase64String(nonce),
                Tag = Convert.ToBase64String(tag),
                Ciphertext = Convert.ToBase64String(ciphertext)
            };

            // 4. Serialize envelope to string
            var envelopeJson = System.Text.Json.JsonSerializer.Serialize(envelope);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(envelopeJson));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error encrypting message with AWS KMS");
            throw;
        }
    }

    public async Task<string> DecryptAsync(string ciphertext, CancellationToken cancellationToken = default)
    {
        try
        {
            // 1. Deserialize envelope
            var envelopeBytes = Convert.FromBase64String(ciphertext);
            var envelopeJson = Encoding.UTF8.GetString(envelopeBytes);
            var envelope = System.Text.Json.JsonSerializer.Deserialize<EnvelopeData>(envelopeJson);

            if (envelope == null)
                throw new InvalidOperationException("Failed to deserialize encryption envelope");

            // 2. Decrypt the data encryption key using KMS
            var encryptedDataKey = Convert.FromBase64String(envelope.EncryptedDataKey);
            var decryptRequest = new DecryptRequest
            {
                CiphertextBlob = new MemoryStream(encryptedDataKey),
                KeyId = _options.MasterKeyId
            };

            var decryptResponse = await _kmsClient.DecryptAsync(decryptRequest, cancellationToken);

            // 3. Extract plaintext key bytes
            byte[] plaintextKey = new byte[decryptResponse.Plaintext.Length];
            decryptResponse.Plaintext.Read(plaintextKey, 0, plaintextKey.Length);

            // 4. Decrypt the ciphertext using AES-256-GCM
            var nonce = Convert.FromBase64String(envelope.Nonce);
            var tag = Convert.FromBase64String(envelope.Tag);
            var ciphertextBytes = Convert.FromBase64String(envelope.Ciphertext);
            var plaintextBytes = new byte[ciphertextBytes.Length];

            using (var aes = new AesGcm(plaintextKey))
            {
                aes.Decrypt(nonce, ciphertextBytes, tag, plaintextBytes);
            }

            // 5. Convert to string
            return Encoding.UTF8.GetString(plaintextBytes);
        }
        catch (Amazon.KeyManagementService.Model.InvalidCiphertextException ex)
        {
            _logger.LogError(ex, "KMS reported invalid ciphertext — message may be tampered or encrypted with wrong key.");
            throw new MessageDecryptionException(
                "The message ciphertext is invalid. The message may be corrupted or encrypted with a different key.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error decrypting message with AWS KMS");
            throw;
        }
    }

    private async Task<DataKey> GetOrGenerateDataKeyAsync(CancellationToken cancellationToken)
    {
        // Check cache first (if caching is enabled)
        if (_options.CacheDataKeySeconds > 0)
        {
            var cacheKey = $"kms-data-key:{_options.MasterKeyId}";
            if (_dataKeyCache.TryGetValue(cacheKey, out DataKey? cachedKey) && cachedKey != null)
            {
                _logger.LogTrace("Using cached data encryption key");
                return cachedKey;
            }

            // Generate new key and cache it
            var dataKey = await GenerateDataKeyAsync(cancellationToken);

            var cacheOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromSeconds(_options.CacheDataKeySeconds))
                .RegisterPostEvictionCallback((key, value, reason, state) =>
                {
                    // Clear the plaintext key from memory when evicted
                    if (value is DataKey dk)
                    {
                        Array.Clear(dk.PlaintextKey, 0, dk.PlaintextKey.Length);
                    }
                });

            _dataKeyCache.Set(cacheKey, dataKey, cacheOptions);
            _logger.LogDebug("Generated and cached new data encryption key for {Duration} seconds",
                _options.CacheDataKeySeconds);

            return dataKey;
        }

        // No caching - generate new key for each operation
        return await GenerateDataKeyAsync(cancellationToken);
    }

    private async Task<DataKey> GenerateDataKeyAsync(CancellationToken cancellationToken)
    {
        var request = new GenerateDataKeyRequest
        {
            KeyId = _options.MasterKeyId,
            KeySpec = DataKeySpec.AES_256
        };

        var response = await _kmsClient.GenerateDataKeyAsync(request, cancellationToken);

        // Extract plaintext key bytes
        byte[] plaintextKey = new byte[response.Plaintext.Length];
        response.Plaintext.Read(plaintextKey, 0, plaintextKey.Length);

        // Extract encrypted key bytes
        byte[] encryptedKey = new byte[response.CiphertextBlob.Length];
        response.CiphertextBlob.Read(encryptedKey, 0, encryptedKey.Length);

        _logger.LogDebug("Generated new data encryption key from KMS master key: {KeyId}",
            _options.MasterKeyId);

        return new DataKey
        {
            PlaintextKey = plaintextKey,
            EncryptedKey = encryptedKey
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
/// Configuration options for AWS KMS encryption
/// </summary>
public class AwsKmsOptions
{
    /// <summary>
    /// KMS Master Key ID or ARN
    /// </summary>
    public string MasterKeyId { get; set; } = string.Empty;

    /// <summary>
    /// How long to cache data encryption keys (in seconds). 0 = no caching.
    /// Recommended: 300 (5 minutes) for better performance
    /// </summary>
    public int CacheDataKeySeconds { get; set; } = 300;
}

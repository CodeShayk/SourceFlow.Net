using Google.Cloud.Kms.V1;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Security;
using System.Security.Cryptography;
using System.Text;

namespace SourceFlow.Cloud.GCP.Security;

/// <summary>
/// Message encryption using Google Cloud KMS with the envelope-encryption pattern: a random
/// data key encrypts the payload with AES-256-GCM, and Cloud KMS wraps (encrypts) the data key.
/// </summary>
/// <remarks>
/// Cloud KMS has no <c>GenerateDataKey</c> operation (unlike AWS KMS), so the data key is
/// generated locally and wrapped with the KMS <c>Encrypt</c> call.
/// </remarks>
public class GcpKmsMessageEncryption : IMessageEncryption
{
    private readonly KeyManagementServiceClient _kmsClient;
    private readonly ILogger<GcpKmsMessageEncryption> _logger;
    private readonly IMemoryCache _dataKeyCache;
    private readonly GcpKmsOptions _options;
    private readonly CryptoKeyName _keyName;

    public string AlgorithmName => "GCP-KMS-AES256-GCM";
    public string KeyIdentifier => _options.KeyName;

    public GcpKmsMessageEncryption(
        KeyManagementServiceClient kmsClient,
        ILogger<GcpKmsMessageEncryption> logger,
        IMemoryCache dataKeyCache,
        GcpKmsOptions options)
    {
        _kmsClient = kmsClient;
        _logger = logger;
        _dataKeyCache = dataKeyCache;
        _options = options;
        _keyName = CryptoKeyName.Parse(options.KeyName);
    }

    public async Task<string> EncryptAsync(string plaintext, CancellationToken cancellationToken = default)
    {
        try
        {
            var dataKey = await GetOrGenerateDataKeyAsync(cancellationToken);

            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            var nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
            RandomNumberGenerator.Fill(nonce);
            var ciphertext = new byte[plaintextBytes.Length];
            var tag = new byte[AesGcm.TagByteSizes.MaxSize];

#if NET8_0_OR_GREATER
            using (var aes = new AesGcm(dataKey.PlaintextKey, tag.Length))
#else
            using (var aes = new AesGcm(dataKey.PlaintextKey))
#endif
            {
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
            _logger.LogError(ex, "Error encrypting message with Google Cloud KMS");
            throw;
        }
    }

    public async Task<string> DecryptAsync(string ciphertext, CancellationToken cancellationToken = default)
    {
        try
        {
            var envelopeBytes = Convert.FromBase64String(ciphertext);
            var envelopeJson = Encoding.UTF8.GetString(envelopeBytes);
            var envelope = System.Text.Json.JsonSerializer.Deserialize<EnvelopeData>(envelopeJson)
                ?? throw new InvalidOperationException("Failed to deserialize encryption envelope");

            // Unwrap the data key via KMS.
            var decryptResponse = await _kmsClient.DecryptAsync(
                _keyName, ByteString.FromBase64(envelope.EncryptedDataKey), cancellationToken);
            var plaintextKey = decryptResponse.Plaintext.ToByteArray();

            var nonce = Convert.FromBase64String(envelope.Nonce);
            var tag = Convert.FromBase64String(envelope.Tag);
            var ciphertextBytes = Convert.FromBase64String(envelope.Ciphertext);
            var plaintextBytes = new byte[ciphertextBytes.Length];

#if NET8_0_OR_GREATER
            using (var aes = new AesGcm(plaintextKey, tag.Length))
#else
            using (var aes = new AesGcm(plaintextKey))
#endif
            {
                aes.Decrypt(nonce, ciphertextBytes, tag, plaintextBytes);
            }

            return Encoding.UTF8.GetString(plaintextBytes);
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "AES-GCM reported invalid ciphertext — message may be tampered or encrypted with a different key.");
            throw new MessageDecryptionException(
                "The message ciphertext is invalid. The message may be corrupted or encrypted with a different key.", ex);
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "Error decrypting data key with Google Cloud KMS");
            throw new MessageDecryptionException("Failed to unwrap the data key via Cloud KMS.", ex);
        }
        catch (MessageDecryptionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error decrypting message with Google Cloud KMS");
            throw;
        }
    }

    private async Task<DataKey> GetOrGenerateDataKeyAsync(CancellationToken cancellationToken)
    {
        if (_options.CacheDataKeySeconds > 0)
        {
            var cacheKey = $"gcp-kms-data-key:{_options.KeyName}";
            if (_dataKeyCache.TryGetValue(cacheKey, out DataKey? cachedKey) && cachedKey != null)
                return cachedKey;

            var dataKey = await GenerateDataKeyAsync(cancellationToken);

            var cacheOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromSeconds(_options.CacheDataKeySeconds))
                .RegisterPostEvictionCallback((key, value, reason, state) =>
                {
                    if (value is DataKey dk)
                        Array.Clear(dk.PlaintextKey, 0, dk.PlaintextKey.Length);
                });

            _dataKeyCache.Set(cacheKey, dataKey, cacheOptions);
            return dataKey;
        }

        return await GenerateDataKeyAsync(cancellationToken);
    }

    private async Task<DataKey> GenerateDataKeyAsync(CancellationToken cancellationToken)
    {
        // Generate a 256-bit data key locally and wrap it with Cloud KMS.
        var plaintextKey = new byte[32];
        RandomNumberGenerator.Fill(plaintextKey);

        var encryptResponse = await _kmsClient.EncryptAsync(
            _keyName, ByteString.CopyFrom(plaintextKey), cancellationToken);

        return new DataKey
        {
            PlaintextKey = plaintextKey,
            EncryptedKey = encryptResponse.Ciphertext.ToByteArray()
        };
    }

    private sealed class DataKey
    {
        public byte[] PlaintextKey { get; set; } = Array.Empty<byte>();
        public byte[] EncryptedKey { get; set; } = Array.Empty<byte>();
    }

    private sealed class EnvelopeData
    {
        public string EncryptedDataKey { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
        public string Ciphertext { get; set; } = string.Empty;
    }
}

/// <summary>Configuration options for Google Cloud KMS encryption.</summary>
public class GcpKmsOptions
{
    /// <summary>
    /// Full Cloud KMS crypto key resource name
    /// (<c>projects/{p}/locations/{l}/keyRings/{r}/cryptoKeys/{k}</c>).
    /// </summary>
    public string KeyName { get; set; } = string.Empty;

    /// <summary>How long to cache the wrapped data key (seconds). 0 = no caching.</summary>
    public int CacheDataKeySeconds { get; set; } = 300;
}

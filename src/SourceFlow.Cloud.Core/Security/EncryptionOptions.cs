namespace SourceFlow.Cloud.Core.Security;

/// <summary>
/// Configuration options for message encryption
/// </summary>
public class EncryptionOptions
{
    /// <summary>
    /// Enable message encryption
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Key identifier (KMS Key ID, Key Vault URI, etc.)
    /// </summary>
    public string? KeyIdentifier { get; set; }

    /// <summary>
    /// Encryption algorithm (AES256, RSA, etc.)
    /// </summary>
    public string Algorithm { get; set; } = "AES256";

    /// <summary>
    /// Cache decrypted data keys (for performance)
    /// </summary>
    public bool CacheDataKeys { get; set; } = true;

    /// <summary>
    /// Data key cache TTL
    /// </summary>
    public TimeSpan DataKeyCacheTTL { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum size of message to encrypt (larger messages split)
    /// </summary>
    public int MaxMessageSize { get; set; } = 256 * 1024; // 256 KB
}

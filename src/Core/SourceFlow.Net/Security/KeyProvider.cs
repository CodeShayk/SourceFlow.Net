using System.Security.Cryptography;

namespace SourceFlow.Security;

/// <summary>
/// A key management service for envelope encryption (doc 10 §3): it issues data encryption keys (DEKs) wrapped by a
/// master key it never reveals, and unwraps them again. Implemented by the cloud packages (AWS KMS, Azure Key Vault,
/// Google Cloud KMS) and by <see cref="LocalKeyProvider"/> for tests and development.
/// </summary>
public interface IKeyProvider
{
    /// <summary>The provider's name in the <c>sfenc</c> header, for example <c>aws-kms</c>.</summary>
    string Name { get; }

    /// <summary>Generates a 256-bit DEK: its plaintext and its wrapped form, with the reference of the master key that wrapped it.</summary>
    /// <exception cref="KeyProviderException">The key service failed or refused.</exception>
    ValueTask<DataKey> GenerateDataKeyAsync(CancellationToken cancellationToken);

    /// <summary>Unwraps a DEK that this provider wrapped under <paramref name="keyReference"/> (any key version).</summary>
    /// <exception cref="KeyProviderException">The key service failed, or the wrapped key is not valid for it.</exception>
    ValueTask<byte[]> UnwrapDataKeyAsync(string keyReference, ReadOnlyMemory<byte> wrapped, CancellationToken cancellationToken);
}

/// <summary>A data encryption key: the plaintext (zeroed on dispose), the wrapped form and the master key reference.</summary>
public sealed class DataKey : IDisposable
{
    /// <summary>Creates a data key. The key takes ownership of <paramref name="plaintext"/> and zeroes it on dispose.</summary>
    public DataKey(byte[] plaintext, byte[] wrapped, string keyReference)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(wrapped);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyReference);
        if (plaintext.Length != 32)
        {
            throw new ArgumentException("A data key is 256 bits.", nameof(plaintext));
        }

        if (wrapped.Length is 0 or > ushort.MaxValue)
        {
            throw new ArgumentException("A wrapped data key is 1 to 65,535 bytes.", nameof(wrapped));
        }

        Plaintext = plaintext;
        Wrapped = wrapped;
        KeyReference = keyReference;
    }

    /// <summary>The 32-byte key.</summary>
    public byte[] Plaintext { get; }

    /// <summary>The key wrapped by the master key; it travels with every message encrypted under it.</summary>
    public byte[] Wrapped { get; }

    /// <summary>The master key that wrapped it (an ARN, a Key Vault key id, a Cloud KMS key name).</summary>
    public string KeyReference { get; }

    /// <summary>Zeroes the plaintext.</summary>
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(Plaintext);
    }
}

/// <summary>A key service call failed. Transient failures (throttling, outage) are retried; others are permanent.</summary>
public sealed class KeyProviderException : Exception
{
    /// <summary>Creates the exception.</summary>
    public KeyProviderException(string message, bool isTransient, Exception? inner = null)
        : base(message, inner)
    {
        IsTransient = isTransient;
    }

    /// <summary>True when a retry can succeed (throttling, timeouts, an unavailable service).</summary>
    public bool IsTransient { get; }
}

/// <summary>
/// A protected message could not be decrypted: a tampered body or associated data, an unknown scheme or provider, or a
/// wrapped key the provider refuses. Permanent: the message is parked with reason <c>DecryptionFailed</c>, never retried.
/// </summary>
public sealed class DecryptionFailedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public DecryptionFailedException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

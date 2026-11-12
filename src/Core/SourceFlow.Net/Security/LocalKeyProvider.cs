using System.Globalization;
using System.Security.Cryptography;

namespace SourceFlow.Security;

/// <summary>
/// An in-process key provider for tests and development: master keys held in memory, DEKs wrapped with AES-256-GCM. It
/// supports rotation (<see cref="Rotate"/>): new DEKs are wrapped by the newest version, and every version still
/// unwraps. Never use it in production: the master keys live in the application's memory and die with it.
/// </summary>
public sealed class LocalKeyProvider : IKeyProvider, IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<byte[]> _versions = [];
    private readonly string _name;

    /// <summary>Creates a provider with one random master key version.</summary>
    public LocalKeyProvider(string keyName = "local-master")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        _name = keyName;
        Rotate();
    }

    /// <inheritdoc />
    public string Name => "local";

    /// <summary>How many times a DEK was generated (one key service call each).</summary>
    public int Generated { get; private set; }

    /// <summary>How many times a DEK was unwrapped (one key service call each).</summary>
    public int Unwrapped { get; private set; }

    /// <summary>When set, every call fails with this exception (a key service outage or refusal).</summary>
    public KeyProviderException? Failure { get; set; }

    /// <summary>Adds a master key version; new DEKs are wrapped by it. Returns the version number.</summary>
    public int Rotate()
    {
        lock (_gate)
        {
            _versions.Add(RandomNumberGenerator.GetBytes(32));
            return _versions.Count;
        }
    }

    /// <inheritdoc />
    public ValueTask<DataKey> GenerateDataKeyAsync(CancellationToken cancellationToken)
    {
        Fail();
        byte[] plaintext = GC.AllocateArray<byte>(32, pinned: true);
        RandomNumberGenerator.Fill(plaintext);
        byte[] master;
        int version;
        lock (_gate)
        {
            version = _versions.Count;
            master = _versions[^1];
            Generated++;
        }

        // wrapped = nonce(12) | tag(16) | ciphertext(32); the key reference names the version.
        byte[] wrapped = new byte[12 + 16 + 32];
        RandomNumberGenerator.Fill(wrapped.AsSpan(0, 12));
        using (var aes = new AesGcm(master, 16))
        {
            aes.Encrypt(wrapped.AsSpan(0, 12), plaintext, wrapped.AsSpan(28, 32), wrapped.AsSpan(12, 16));
        }

        return ValueTask.FromResult(new DataKey(plaintext, wrapped, $"{_name}/v{version.ToString(CultureInfo.InvariantCulture)}"));
    }

    /// <inheritdoc />
    public ValueTask<byte[]> UnwrapDataKeyAsync(string keyReference, ReadOnlyMemory<byte> wrapped, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keyReference);
        Fail();
        int slash = keyReference.LastIndexOf("/v", StringComparison.Ordinal);
        byte[] master;
        lock (_gate)
        {
            if (slash < 0 || keyReference[..slash] != _name
                || !int.TryParse(keyReference.AsSpan(slash + 2), NumberStyles.None, CultureInfo.InvariantCulture, out int version)
                || version < 1 || version > _versions.Count)
            {
                throw new KeyProviderException($"Unknown key '{keyReference}'.", isTransient: false);
            }

            master = _versions[version - 1];
            Unwrapped++;
        }

        if (wrapped.Length != 60)
        {
            throw new KeyProviderException("The wrapped key is malformed.", isTransient: false);
        }

        byte[] plaintext = GC.AllocateArray<byte>(32, pinned: true);
        try
        {
            using var aes = new AesGcm(master, 16);
            ReadOnlySpan<byte> span = wrapped.Span;
            aes.Decrypt(span[..12], span.Slice(28, 32), span.Slice(12, 16), plaintext);
            return ValueTask.FromResult(plaintext);
        }
        catch (CryptographicException e)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new KeyProviderException("The wrapped key does not unwrap under this master key.", isTransient: false, e);
        }
    }

    /// <summary>Zeroes the master keys.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (byte[] key in _versions)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    private void Fail()
    {
        if (Failure is { } failure)
        {
            throw failure;
        }
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SourceFlow.Messaging;

namespace SourceFlow.Security;

/// <summary>Payload protection settings (doc 10 §3).</summary>
public sealed class PayloadProtectionOptions
{
    private readonly HashSet<string> _destinations = new(StringComparer.Ordinal);

    /// <summary>True when every destination is protected.</summary>
    public bool AllDestinations { get; private set; }

    /// <summary>The protected destinations (endpoints and topics), unless <see cref="AllDestinations"/>.</summary>
    public IReadOnlySet<string> Destinations => _destinations;

    /// <summary>A DEK encrypts at most this many messages (default 10,000), well inside the AES-GCM random-nonce limit.</summary>
    public int MaxMessagesPerKey { get; set; } = 10_000;

    /// <summary>A DEK is used for at most this long (default 5 minutes).</summary>
    public TimeSpan MaxKeyAge { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How many unwrapped DEKs a receiver keeps (default 256); each saves a key service call per message.</summary>
    public int UnwrappedKeyCacheSize { get; set; } = 256;

    /// <summary>
    /// When true, a message received on a protected destination without protection is parked (<c>ProtectionRequired</c>):
    /// a sender cannot downgrade to plaintext. Off by default, so protection can be rolled out sender first.
    /// </summary>
    public bool RequireOnReceive { get; set; }

    /// <summary>Protects messages sent or published to these destinations.</summary>
    public PayloadProtectionOptions Protect(params string[] destinations)
    {
        ArgumentNullException.ThrowIfNull(destinations);
        foreach (string destination in destinations)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(destination);
            _destinations.Add(destination);
        }

        return this;
    }

    /// <summary>Protects every message this service sends or publishes.</summary>
    public PayloadProtectionOptions ProtectAll()
    {
        AllDestinations = true;
        return this;
    }

    internal void Validate()
    {
        if (MaxMessagesPerKey is < 1 or > 1_000_000)
        {
            throw new InvalidOperationException("MaxMessagesPerKey must be between 1 and 1,000,000 (AES-GCM with random nonces stays far below 2^32 messages per key).");
        }

        if (MaxKeyAge <= TimeSpan.Zero || MaxKeyAge > TimeSpan.FromDays(1))
        {
            throw new InvalidOperationException("MaxKeyAge must be positive and at most one day.");
        }

        if (UnwrappedKeyCacheSize < 1)
        {
            throw new InvalidOperationException("UnwrappedKeyCacheSize must be positive.");
        }

        if (!AllDestinations && _destinations.Count == 0)
        {
            throw new InvalidOperationException("Payload protection protects nothing: call Protect(destinations) or ProtectAll().");
        }
    }
}

/// <summary>
/// Envelope encryption of message bodies (doc 10 §3): AES-256-GCM with a 96-bit random nonce under a DEK from the
/// <see cref="IKeyProvider"/>, associated data <c>id|type|source</c>. The body becomes
/// <c>version(1) | wrapped length(2) | wrapped DEK | nonce(12) | tag(16) | ciphertext</c>, and the <c>sfenc</c> header
/// (<see cref="MessageEnvelope.Protection"/>) names the scheme, the provider and the master key.
/// </summary>
/// <remarks>
/// A DEK is reused for at most <see cref="PayloadProtectionOptions.MaxMessagesPerKey"/> messages and
/// <see cref="PayloadProtectionOptions.MaxKeyAge"/>, so a key service call is made per key, not per message. Plaintext
/// keys are held in pinned arrays and zeroed when replaced or evicted; each operation works on its own copy inside an
/// <see cref="AesGcm"/> instance, so zeroing never races an operation in flight.
/// </remarks>
public sealed class PayloadProtector : IDisposable
{
    /// <summary>The scheme in the <c>sfenc</c> header.</summary>
    public const string Scheme = "sf-aes256gcm-v1";

    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly IKeyProvider _provider;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _rotation = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CachedKey> _unwrapped = new(StringComparer.Ordinal);
    private ActiveKey? _active;
    private ActiveKey? _retired;
    private (DateTimeOffset At, Exception? Failure)? _probe;

    /// <summary>Creates the protector.</summary>
    public PayloadProtector(IKeyProvider provider, PayloadProtectionOptions options, TimeProvider? time = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The settings.</summary>
    public PayloadProtectionOptions Options { get; }

    /// <summary>True when messages to <paramref name="destination"/> are protected.</summary>
    public bool Protects(string destination)
    {
        return Options.AllDestinations || Options.Destinations.Contains(destination);
    }

    /// <summary>Encrypts the body; an envelope that is already protected is returned as is.</summary>
    /// <exception cref="KeyProviderException">A new DEK was needed and the key service failed.</exception>
    public async ValueTask<MessageEnvelope> ProtectAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Protection is not null)
        {
            return envelope;
        }

        (AesGcm aes, byte[] wrapped, string keyReference) = await LeaseKeyAsync(cancellationToken).ConfigureAwait(false);
        using (aes)
        {
            int length = 1 + 2 + wrapped.Length + NonceSize + TagSize + envelope.Body.Length;
            byte[] body = new byte[length];
            body[0] = FormatVersion;
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(1, 2), (ushort)wrapped.Length);
            wrapped.CopyTo(body.AsSpan(3));
            int offset = 3 + wrapped.Length;
            Span<byte> nonce = body.AsSpan(offset, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            aes.Encrypt(nonce, envelope.Body.Span, body.AsSpan(offset + NonceSize + TagSize), body.AsSpan(offset + NonceSize, TagSize), AssociatedData(envelope));
            return envelope with { Body = body, Protection = $"{Scheme};{_provider.Name};{keyReference}" };
        }
    }

    /// <summary>Decrypts a protected body; an unprotected envelope is returned as is.</summary>
    /// <exception cref="DecryptionFailedException">Tampered, malformed, or not this provider's (permanent).</exception>
    /// <exception cref="KeyProviderException">The key service is unavailable (transient: the message is retried).</exception>
    public async ValueTask<MessageEnvelope> UnprotectAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Protection is not { } protection)
        {
            return envelope;
        }

        string[] parts = protection.Split(';', 3);
        if (parts.Length != 3 || parts[0] != Scheme)
        {
            throw new DecryptionFailedException($"Unknown protection scheme '{(parts.Length > 0 ? parts[0] : string.Empty)}'.");
        }

        if (parts[1] != _provider.Name)
        {
            throw new DecryptionFailedException($"The message was protected with key provider '{parts[1]}', and this service uses '{_provider.Name}'.");
        }

        ReadOnlyMemory<byte> body = envelope.Body;
        if (body.Length < 3 || body.Span[0] != FormatVersion)
        {
            throw new DecryptionFailedException("The protected body is malformed.");
        }

        int wrappedLength = BinaryPrimitives.ReadUInt16BigEndian(body.Span.Slice(1, 2));
        if (body.Length < 3 + wrappedLength + NonceSize + TagSize)
        {
            throw new DecryptionFailedException("The protected body is truncated.");
        }

        ReadOnlyMemory<byte> wrapped = body.Slice(3, wrappedLength);
        int offset = 3 + wrappedLength;
        using AesGcm aes = await UnwrapAsync(parts[2], wrapped, cancellationToken).ConfigureAwait(false);
        byte[] plaintext = new byte[body.Length - offset - NonceSize - TagSize];
        try
        {
            aes.Decrypt(body.Span.Slice(offset, NonceSize), body.Span[(offset + NonceSize + TagSize)..], body.Span.Slice(offset + NonceSize, TagSize), plaintext, AssociatedData(envelope));
        }
        catch (CryptographicException e)
        {
            throw new DecryptionFailedException("The protected body or its id, type or source was altered (authentication failed).", e);
        }

        return envelope with { Body = plaintext, Protection = null };
    }

    /// <summary>
    /// Asks the key service for a data key (discarded), at most once per <paramref name="cacheFor"/>; returns the failure,
    /// or null when it answered. For the <c>sourceflow.keys</c> health check.
    /// </summary>
    internal async ValueTask<Exception?> ProbeAsync(TimeSpan cacheFor, CancellationToken cancellationToken)
    {
        (DateTimeOffset At, Exception? Failure)? last = _probe;
        if (last is { } cached && _time.GetUtcNow() - cached.At < cacheFor)
        {
            return cached.Failure;
        }

        Exception? failure = null;
        try
        {
            using DataKey key = await _provider.GenerateDataKeyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is KeyProviderException or InvalidOperationException or ArgumentException)
        {
            failure = e;
        }

        _probe = (_time.GetUtcNow(), failure);
        return failure;
    }

    /// <summary>Zeroes every cached key.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _active?.Dispose();
            _retired?.Dispose();
            foreach (CachedKey key in _unwrapped.Values)
            {
                CryptographicOperations.ZeroMemory(key.Plaintext);
            }

            _unwrapped.Clear();
        }

        _rotation.Dispose();
    }

    // id|type|source binds the ciphertext to the envelope: a body moved to another message fails authentication.
    private static byte[] AssociatedData(MessageEnvelope envelope)
    {
        return Encoding.UTF8.GetBytes($"{envelope.MessageId}|{envelope.Type}|{envelope.Source}");
    }

    private async ValueTask<(AesGcm Aes, byte[] Wrapped, string KeyReference)> LeaseKeyAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_active is { } key && key.Uses < Options.MaxMessagesPerKey && _time.GetUtcNow() - key.CreatedAt < Options.MaxKeyAge)
                {
                    key.Uses++;
                    return (new AesGcm(key.Data.Plaintext, TagSize), key.Data.Wrapped, key.Data.KeyReference);
                }
            }

            await _rotation.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                bool stale;
                lock (_gate)
                {
                    stale = _active is not { } current || current.Uses >= Options.MaxMessagesPerKey || _time.GetUtcNow() - current.CreatedAt >= Options.MaxKeyAge;
                }

                if (stale)
                {
                    DataKey fresh = await _provider.GenerateDataKeyAsync(cancellationToken).ConfigureAwait(false);
                    lock (_gate)
                    {
                        _retired?.Dispose();   // two rotations old: no operation still copies from it
                        _retired = _active;
                        _active = new ActiveKey(fresh, _time.GetUtcNow());
                    }
                }
            }
            finally
            {
                _rotation.Release();
            }
        }
    }

    private async ValueTask<AesGcm> UnwrapAsync(string keyReference, ReadOnlyMemory<byte> wrapped, CancellationToken cancellationToken)
    {
        string cacheKey = keyReference + "|" + Convert.ToBase64String(wrapped.Span);
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_unwrapped.TryGetValue(cacheKey, out CachedKey? cached) && now - cached.CachedAt < Options.MaxKeyAge * 2)
            {
                return new AesGcm(cached.Plaintext, TagSize);
            }
        }

        byte[] plaintext = GC.AllocateArray<byte>(32, pinned: true);
        try
        {
            // Keys from an SDK may live in movable memory: keep a pinned copy and zero theirs.
            byte[] unwrapped = await _provider.UnwrapDataKeyAsync(keyReference, wrapped, cancellationToken).ConfigureAwait(false);
            if (unwrapped.Length != 32)
            {
                CryptographicOperations.ZeroMemory(unwrapped);
                throw new DecryptionFailedException("The key service returned a key of the wrong size.");
            }

            unwrapped.CopyTo(plaintext, 0);
            CryptographicOperations.ZeroMemory(unwrapped);
        }
        catch (KeyProviderException e) when (!e.IsTransient)
        {
            throw new DecryptionFailedException($"The key service refused the wrapped key ({e.Message}).", e);
        }

        lock (_gate)
        {
            var aes = new AesGcm(plaintext, TagSize);
            if (_unwrapped.Remove(cacheKey, out CachedKey? old))
            {
                CryptographicOperations.ZeroMemory(old.Plaintext);
            }

            while (_unwrapped.Count >= Options.UnwrappedKeyCacheSize)
            {
                KeyValuePair<string, CachedKey> oldest = _unwrapped.MinBy(k => k.Value.CachedAt);
                CryptographicOperations.ZeroMemory(oldest.Value.Plaintext);
                _unwrapped.Remove(oldest.Key);
            }

            _unwrapped[cacheKey] = new CachedKey(plaintext, now);
            return aes;
        }
    }

    private sealed class ActiveKey(DataKey data, DateTimeOffset createdAt) : IDisposable
    {
        public DataKey Data { get; } = data;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public int Uses { get; set; }

        public void Dispose()
        {
            Data.Dispose();
        }
    }

    private sealed record CachedKey(byte[] Plaintext, DateTimeOffset CachedAt);
}

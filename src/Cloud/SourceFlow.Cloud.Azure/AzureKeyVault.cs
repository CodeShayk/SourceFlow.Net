using System.Collections.Concurrent;
using System.Security.Cryptography;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Keys.Cryptography;
using SourceFlow.Security;

namespace SourceFlow.Cloud.Azure;

/// <summary>Options of the Azure Key Vault key provider (doc 10 §3).</summary>
public sealed class AzureKeyVaultOptions
{
    /// <summary>
    /// The key: <c>https://{vault}.vault.azure.net/keys/{name}</c> (the current version wraps new DEKs) or a versioned id.
    /// Required.
    /// </summary>
    public Uri? KeyId { get; set; }

    /// <summary>The credential; defaults to <see cref="DefaultAzureCredential"/> (managed identity, workload identity, CLI).</summary>
    public TokenCredential? Credential { get; set; }

    /// <summary>The key wrap algorithm: RSA-OAEP-256 for RSA keys (default), A256KW for Managed HSM AES keys.</summary>
    public KeyWrapAlgorithm Algorithm { get; set; } = KeyWrapAlgorithm.RsaOaep256;
}

/// <summary>
/// Azure Key Vault as the <see cref="IKeyProvider"/> (doc 10 §3, WP-5.1). A DEK is generated locally and wrapped by the
/// vault key (<c>wrapKey</c>); the key reference is the versioned key id the vault reports, so a DEK wrapped before a
/// rotation still unwraps with the version that wrapped it. Throttling (429), server errors and network failures are
/// transient; access denied, a missing or disabled key and a bad wrapped key are not.
/// </summary>
public sealed class AzureKeyVaultKeyProvider : IKeyProvider
{
    private readonly Uri _keyId;
    private readonly KeyWrapAlgorithm _algorithm;
    private readonly Func<Uri, CryptographyClient> _clients;
    private readonly ConcurrentDictionary<string, CryptographyClient> _byVersion = new(StringComparer.Ordinal);

    /// <summary>Creates the provider with clients built from <paramref name="options"/>.</summary>
    public AzureKeyVaultKeyProvider(AzureKeyVaultOptions options)
        : this(Required(options).KeyId!, options.Algorithm, Clients(options.Credential ?? new DefaultAzureCredential()))
    {
    }

    /// <summary>Creates the provider over a client factory (one client per key id; tests pass fakes).</summary>
    public AzureKeyVaultKeyProvider(Uri keyId, KeyWrapAlgorithm algorithm, Func<Uri, CryptographyClient> clients)
    {
        _keyId = keyId ?? throw new ArgumentNullException(nameof(keyId));
        _algorithm = algorithm;
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
    }

    /// <inheritdoc />
    public string Name => "azure-keyvault";

    /// <inheritdoc />
    public async ValueTask<DataKey> GenerateDataKeyAsync(CancellationToken cancellationToken)
    {
        byte[] plaintext = GC.AllocateArray<byte>(32, pinned: true);
        RandomNumberGenerator.Fill(plaintext);
        try
        {
            WrapResult wrapped = await Client(_keyId.ToString()).WrapKeyAsync(_algorithm, plaintext, cancellationToken).ConfigureAwait(false);
            return new DataKey(plaintext, wrapped.EncryptedKey, wrapped.KeyId);
        }
        catch (Exception e) when (Map(e) is { } mapped)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw mapped;
        }
    }

    /// <inheritdoc />
    public async ValueTask<byte[]> UnwrapDataKeyAsync(string keyReference, ReadOnlyMemory<byte> wrapped, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyReference);
        if (!Uri.TryCreate(keyReference, UriKind.Absolute, out Uri? version) || version.Host != _keyId.Host)
        {
            throw new KeyProviderException($"'{keyReference}' is not a key of this vault.", isTransient: false);
        }

        try
        {
            UnwrapResult result = await Client(keyReference).UnwrapKeyAsync(_algorithm, wrapped.ToArray(), cancellationToken).ConfigureAwait(false);
            return result.Key;
        }
        catch (Exception e) when (Map(e) is { } mapped)
        {
            throw mapped;
        }
    }

    internal static KeyProviderException? Map(Exception error)
    {
        return error switch
        {
            KeyProviderException => null,
            OperationCanceledException when error.InnerException is not TimeoutException => null,
            RequestFailedException { Status: 408 or 429 or >= 500 } failed => new KeyProviderException($"Key Vault is unavailable ({failed.Status}): {failed.Message}", isTransient: true, failed),
            RequestFailedException { Status: 0 } failed => new KeyProviderException($"Key Vault is unreachable: {failed.Message}", isTransient: true, failed),
            RequestFailedException failed => new KeyProviderException($"Key Vault refused the key operation ({failed.Status}): {failed.Message}", isTransient: false, failed),
            AuthenticationFailedException auth => new KeyProviderException($"Could not authenticate to Key Vault: {auth.Message}", isTransient: true, auth),
            CryptographicException crypto => new KeyProviderException($"The wrapped key is not valid for this key: {crypto.Message}", isTransient: false, crypto),
            HttpRequestException or IOException or TimeoutException or OperationCanceledException => new KeyProviderException($"Key Vault is unreachable: {error.Message}", isTransient: true, error),
            _ => null,
        };
    }

    private CryptographyClient Client(string keyId)
    {
        return _byVersion.GetOrAdd(keyId, id => _clients(new Uri(id)));
    }

    private static AzureKeyVaultOptions Required(AzureKeyVaultOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.KeyId is null || !options.KeyId.IsAbsoluteUri || !options.KeyId.AbsolutePath.StartsWith("/keys/", StringComparison.Ordinal))
        {
            throw new ArgumentException("AzureKeyVaultOptions.KeyId must be a Key Vault key id: https://{vault}.vault.azure.net/keys/{name}[/{version}].", nameof(options));
        }

        return options;
    }

    private static Func<Uri, CryptographyClient> Clients(TokenCredential credential)
    {
        return id => new CryptographyClient(id, credential);
    }
}

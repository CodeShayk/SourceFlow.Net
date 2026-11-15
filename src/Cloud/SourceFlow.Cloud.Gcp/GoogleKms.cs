using System.Security.Cryptography;
using Google.Api.Gax.Grpc;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Kms.V1;
using Google.Protobuf;
using Grpc.Core;
using SourceFlow.Security;

namespace SourceFlow.Cloud.Gcp;

/// <summary>Options of the Google Cloud KMS key provider (doc 10 §3).</summary>
public sealed class GoogleKmsOptions
{
    /// <summary>
    /// The crypto key: <c>projects/{project}/locations/{location}/keyRings/{ring}/cryptoKeys/{key}</c>. Its primary version
    /// wraps new DEKs. Required.
    /// </summary>
    public string? KeyName { get; set; }

    /// <summary>The credential; defaults to Application Default Credentials.</summary>
    public GoogleCredential? Credential { get; set; }
}

/// <summary>
/// Google Cloud KMS as the <see cref="IKeyProvider"/> (doc 10 §3, WP-5.1). A DEK is generated locally and encrypted by
/// the crypto key (<c>Encrypt</c>); the ciphertext names the key version, so <c>Decrypt</c> on the crypto key unwraps
/// DEKs from before a rotation. Unavailable, deadline, resource-exhausted and internal errors are transient; permission
/// denied, not found and invalid arguments (a bad wrapped key) are not.
/// </summary>
public sealed class GoogleKmsKeyProvider : IKeyProvider
{
    private readonly KeyManagementServiceClient _kms;
    private readonly CryptoKeyName _key;

    /// <summary>Creates the provider with a client built from <paramref name="options"/>.</summary>
    public GoogleKmsKeyProvider(GoogleKmsOptions options)
        : this(Client(options), KeyOf(options))
    {
    }

    /// <summary>Creates the provider over an existing client (tests pass a fake).</summary>
    public GoogleKmsKeyProvider(KeyManagementServiceClient kms, string keyName)
    {
        _kms = kms ?? throw new ArgumentNullException(nameof(kms));
        _key = CryptoKeyName.TryParse(keyName, out CryptoKeyName? key)
            ? key
            : throw new ArgumentException("A Cloud KMS crypto key is projects/{project}/locations/{location}/keyRings/{ring}/cryptoKeys/{key}.", nameof(keyName));
    }

    /// <inheritdoc />
    public string Name => "gcp-kms";

    /// <inheritdoc />
    public async ValueTask<DataKey> GenerateDataKeyAsync(CancellationToken cancellationToken)
    {
        byte[] plaintext = GC.AllocateArray<byte>(32, pinned: true);
        RandomNumberGenerator.Fill(plaintext);
        try
        {
            EncryptResponse response = await _kms.EncryptAsync(new EncryptRequest { Name = _key.ToString(), Plaintext = ByteString.CopyFrom(plaintext) }, CallSettings.FromCancellationToken(cancellationToken)).ConfigureAwait(false);
            return new DataKey(plaintext, response.Ciphertext.ToByteArray(), _key.ToString());
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
        if (!CryptoKeyName.TryParse(keyReference, out CryptoKeyName? key))
        {
            throw new KeyProviderException($"'{keyReference}' is not a Cloud KMS crypto key.", isTransient: false);
        }

        try
        {
            DecryptResponse response = await _kms.DecryptAsync(new DecryptRequest { Name = key.ToString(), Ciphertext = ByteString.CopyFrom(wrapped.Span) }, CallSettings.FromCancellationToken(cancellationToken)).ConfigureAwait(false);
            return response.Plaintext.ToByteArray();
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
            RpcException { StatusCode: StatusCode.PermissionDenied or StatusCode.NotFound or StatusCode.InvalidArgument or StatusCode.FailedPrecondition } refused
                => new KeyProviderException($"Cloud KMS refused the key operation ({refused.StatusCode}): {refused.Status.Detail}", isTransient: false, refused),
            _ when GcpErrors.Map(error, "kms") is { } transport
                => new KeyProviderException($"Cloud KMS call failed: {error.Message}", transport.IsTransient, error),
            _ => null,
        };
    }

    private static KeyManagementServiceClient Client(GoogleKmsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = new KeyManagementServiceClientBuilder();
        if (options.Credential is { } credential)
        {
            builder.GoogleCredential = credential;
        }

        return builder.Build();
    }

    private static string KeyOf(GoogleKmsOptions options)
    {
        return options.KeyName ?? throw new ArgumentException("GoogleKmsOptions.KeyName is required.", nameof(options));
    }
}

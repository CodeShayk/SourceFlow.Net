using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using SourceFlow.Security;

namespace SourceFlow.Cloud.Aws;

/// <summary>Options of the AWS KMS key provider (doc 10 §3).</summary>
public sealed class AwsKmsOptions
{
    /// <summary>The KMS key: a key id, ARN, alias name (<c>alias/accounts-messages</c>) or alias ARN. Required.</summary>
    public string? KeyId { get; set; }

    /// <summary>The region; defaults to the SDK's resolution (environment, profile, instance metadata).</summary>
    public string? Region { get; set; }

    /// <summary>A named profile from the shared credentials file; null uses the SDK's default credential chain.</summary>
    public string? Profile { get; set; }

    /// <summary>A custom endpoint, for example LocalStack (<c>http://localhost:4566</c>).</summary>
    public Uri? ServiceUrl { get; set; }

    /// <summary>The SDK's own retries per call (0 to 10, default 3).</summary>
    public int MaxSdkRetries { get; set; } = 3;

    /// <summary>The timeout of one KMS call (default 10 s).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    internal AwsTransportOptions Connection()
    {
        var connection = new AwsTransportOptions { Region = Region, Profile = Profile, ServiceUrl = ServiceUrl, MaxSdkRetries = MaxSdkRetries, Timeout = Timeout };
        connection.Validate();
        return connection;
    }
}

/// <summary>
/// AWS KMS as the <see cref="IKeyProvider"/> (doc 10 §3, WP-5.1): <c>GenerateDataKey</c> (AES-256) for new DEKs and
/// <c>Decrypt</c> to unwrap them. The key reference is the key ARN KMS reports; the ciphertext blob names the key version,
/// so rotation is transparent. Throttling and outages are transient; access denied, a disabled key or an invalid blob are not.
/// </summary>
public sealed class AwsKmsKeyProvider : IKeyProvider, IDisposable
{
    private readonly IAmazonKeyManagementService _kms;
    private readonly string _keyId;
    private readonly bool _ownsClient;

    /// <summary>Creates the provider with a client built from <paramref name="options"/>.</summary>
    public AwsKmsKeyProvider(AwsKmsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _keyId = !string.IsNullOrWhiteSpace(options.KeyId) ? options.KeyId : throw new ArgumentException("AwsKmsOptions.KeyId is required.", nameof(options));
        AwsTransportOptions connection = options.Connection();
        AmazonKeyManagementServiceConfig config = connection.Apply(new AmazonKeyManagementServiceConfig());
        Amazon.Runtime.AWSCredentials? credentials = connection.ResolveCredentials();
        _kms = credentials is null ? new AmazonKeyManagementServiceClient(config) : new AmazonKeyManagementServiceClient(credentials, config);
        _ownsClient = true;
    }

    /// <summary>Creates the provider over an existing client (owned by the caller).</summary>
    public AwsKmsKeyProvider(IAmazonKeyManagementService kms, string keyId)
    {
        _kms = kms ?? throw new ArgumentNullException(nameof(kms));
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        _keyId = keyId;
    }

    /// <inheritdoc />
    public string Name => "aws-kms";

    /// <inheritdoc />
    public async ValueTask<DataKey> GenerateDataKeyAsync(CancellationToken cancellationToken)
    {
        GenerateDataKeyResponse response;
        try
        {
            response = await _kms.GenerateDataKeyAsync(new GenerateDataKeyRequest { KeyId = _keyId, KeySpec = DataKeySpec.AES_256 }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (Map(e) is { } mapped)
        {
            throw mapped;
        }

        byte[] plaintext = response.Plaintext.ToArray();
        response.Plaintext.SetLength(0);
        return new DataKey(plaintext, response.CiphertextBlob.ToArray(), response.KeyId);
    }

    /// <inheritdoc />
    public async ValueTask<byte[]> UnwrapDataKeyAsync(string keyReference, ReadOnlyMemory<byte> wrapped, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyReference);
        try
        {
            using var blob = new MemoryStream(wrapped.ToArray());
            DecryptResponse response = await _kms.DecryptAsync(new DecryptRequest { CiphertextBlob = blob, KeyId = keyReference }, cancellationToken).ConfigureAwait(false);
            byte[] plaintext = response.Plaintext.ToArray();
            response.Plaintext.SetLength(0);
            return plaintext;
        }
        catch (Exception e) when (Map(e) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <summary>Disposes the client when this provider created it.</summary>
    public void Dispose()
    {
        if (_ownsClient)
        {
            _kms.Dispose();
        }
    }

    // KMS-specific refusals are permanent; the rest follows the transport's transience rules (throttling, outages).
    internal static KeyProviderException? Map(Exception error)
    {
        return error switch
        {
            KeyProviderException => null,
            OperationCanceledException when error.InnerException is not TimeoutException => null,
            InvalidCiphertextException or IncorrectKeyException or DisabledException or NotFoundException or KMSInvalidStateException or InvalidKeyUsageException
                => new KeyProviderException($"AWS KMS refused the key operation: {error.Message}", isTransient: false, error),
            _ when AwsErrors.Map(error, "kms") is { } transport
                => new KeyProviderException($"AWS KMS call failed: {error.Message}", transport.IsTransient, error),
            _ => null,
        };
    }
}

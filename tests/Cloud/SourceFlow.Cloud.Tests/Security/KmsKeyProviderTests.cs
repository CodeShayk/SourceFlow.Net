using System.Security.Cryptography;
using System.Text;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Amazon.Runtime;
using Azure;
using Azure.Security.KeyVault.Keys.Cryptography;
using Google.Api.Gax.Grpc;
using Google.Cloud.Kms.V1;
using GcpDecryptRequest = Google.Cloud.Kms.V1.DecryptRequest;
using GcpDecryptResponse = Google.Cloud.Kms.V1.DecryptResponse;
using GcpEncryptRequest = Google.Cloud.Kms.V1.EncryptRequest;
using GcpEncryptResponse = Google.Cloud.Kms.V1.EncryptResponse;
using Google.Protobuf;
using Grpc.Core;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SourceFlow.Cloud.Aws;
using SourceFlow.Cloud.Azure;
using SourceFlow.Cloud.Gcp;
using SourceFlow.Cloud.Tests.Aws;
using SourceFlow.Messaging;
using SourceFlow.Security;

namespace SourceFlow.Cloud.Tests.Security;

/// <summary>
/// The KMS key providers (doc 10 §3, WP-5.1; UT-SEC with real SDK types): AWS KMS on LocalStack, and Azure Key Vault and
/// Google Cloud KMS through fakes of their SDK clients (neither has an emulator; the real services run in the nightly
/// real-cloud job). Each provider round-trips through <see cref="PayloadProtector"/> and maps its errors by transience.
/// </summary>
[Trait("Category", "Integration")]
public sealed class KmsKeyProviderTests(LocalStackServer localStack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ================================================================ AWS KMS

    [Fact]
    [Trait("Id", "UT-SEC-21")]
    [Trait("Cloud", "Aws")]
    [Trait("Requires", "LocalStack")]
    [Trait("Covers", "doc 10 3")]
    public async Task AwsKms_RoundTrips_OnLocalStack_WithOneGenerateAndOneDecryptPerKey()
    {
        await localStack.EnsureStartedAsync();
        using var kms = new AmazonKeyManagementServiceClient(new BasicAWSCredentials("test", "test"),
            new AmazonKeyManagementServiceConfig { ServiceURL = localStack.Endpoint.ToString(), AuthenticationRegion = LocalStackServer.Region });
        string keyArn = (await kms.CreateKeyAsync(new CreateKeyRequest { Description = "sourceflow-test" }, Ct)).KeyMetadata.Arn;
        string alias = "alias/sf-" + Guid.CreateVersion7().ToString("N")[..12];
        await kms.CreateAliasAsync(new CreateAliasRequest { AliasName = alias, TargetKeyId = keyArn }, Ct);
        using var provider = new AwsKmsKeyProvider(kms, alias);
        using PayloadProtector sender = Protector(provider);
        using PayloadProtector receiver = Protector(provider);

        var sealedEnvelopes = new List<MessageEnvelope>();
        for (int i = 0; i < 5; i++)
        {
            sealedEnvelopes.Add(await sender.ProtectAsync(Envelope("{\"n\":" + i + "}"), Ct));
        }

        sealedEnvelopes[0].Protection.ShouldBe($"sf-aes256gcm-v1;aws-kms;{keyArn}", "the alias resolves to the key ARN");
        for (int i = 0; i < 5; i++)
        {
            Encoding.UTF8.GetString((await receiver.UnprotectAsync(sealedEnvelopes[i], Ct)).Body.Span).ShouldBe("{\"n\":" + i + "}");
        }

        // A wrapped key presented under the wrong master key is refused by KMS (IncorrectKeyException): permanent, never
        // retried. (A corrupted blob would be refused the same way by AWS; LocalStack answers that with a 500, which the
        // provider rightly treats as transient.)
        string otherArn = (await kms.CreateKeyAsync(new CreateKeyRequest { Description = "sourceflow-test-other" }, Ct)).KeyMetadata.Arn;
        using var other = new AwsKmsKeyProvider(kms, otherArn);
        using PayloadProtector otherSender = Protector(other);
        MessageEnvelope foreign = await otherSender.ProtectAsync(Envelope("{}"), Ct);
        using PayloadProtector fresh = Protector(provider);
        Exception? refused = await Record.ExceptionAsync(async () => await fresh.UnprotectAsync(foreign with { Protection = $"sf-aes256gcm-v1;aws-kms;{keyArn}" }, Ct));
        refused.ShouldBeOfType<DecryptionFailedException>(refused?.ToString());
    }

    [Fact]
    [Trait("Id", "UT-SEC-22")]
    [Trait("Cloud", "Aws")]
    public async Task AwsKms_Errors_AreMappedByTransience()
    {
        IAmazonKeyManagementService kms = Substitute.For<IAmazonKeyManagementService>();
        var provider = new AwsKmsKeyProvider(kms, "alias/x");

        kms.GenerateDataKeyAsync(Arg.Any<GenerateDataKeyRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new AmazonKeyManagementServiceException("slow down") { ErrorCode = "ThrottlingException" });
        (await Should.ThrowAsync<KeyProviderException>(async () => await provider.GenerateDataKeyAsync(Ct))).IsTransient.ShouldBeTrue();

        kms.GenerateDataKeyAsync(Arg.Any<GenerateDataKeyRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new DisabledException("disabled"));
        (await Should.ThrowAsync<KeyProviderException>(async () => await provider.GenerateDataKeyAsync(Ct))).IsTransient.ShouldBeFalse();

        kms.DecryptAsync(Arg.Any<Amazon.KeyManagementService.Model.DecryptRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidCiphertextException("bad blob"));
        (await Should.ThrowAsync<KeyProviderException>(async () => await provider.UnwrapDataKeyAsync("arn:k", new byte[8], Ct))).IsTransient.ShouldBeFalse();

        kms.DecryptAsync(Arg.Any<Amazon.KeyManagementService.Model.DecryptRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("connection refused"));
        (await Should.ThrowAsync<KeyProviderException>(async () => await provider.UnwrapDataKeyAsync("arn:k", new byte[8], Ct))).IsTransient.ShouldBeTrue();

        Should.Throw<ArgumentException>(() => new AwsKmsKeyProvider(new AwsKmsOptions()));
    }

    // ================================================================ Azure Key Vault

    [Fact]
    [Trait("Id", "UT-SEC-23")]
    [Trait("Cloud", "Azure")]
    [Trait("Covers", "doc 10 3")]
    public async Task AzureKeyVault_RoundTrips_AndKeepsTheWrappingVersion_AcrossRotation()
    {
        var vault = new FakeVault("https://unit.vault.azure.net/keys/messages");
        var provider = new AzureKeyVaultKeyProvider(new Uri("https://unit.vault.azure.net/keys/messages"), KeyWrapAlgorithm.RsaOaep256, vault.Client);
        using PayloadProtector sender = Protector(provider, o => o.MaxMessagesPerKey = 1);
        using PayloadProtector receiver = Protector(provider);

        MessageEnvelope before = await sender.ProtectAsync(Envelope("{\"v\":1}"), Ct);
        vault.Rotate();
        MessageEnvelope after = await sender.ProtectAsync(Envelope("{\"v\":2}"), Ct);

        before.Protection.ShouldEndWith("/keys/messages/v1");
        after.Protection.ShouldEndWith("/keys/messages/v2");
        Encoding.UTF8.GetString((await receiver.UnprotectAsync(before, Ct)).Body.Span).ShouldBe("{\"v\":1}");
        Encoding.UTF8.GetString((await receiver.UnprotectAsync(after, Ct)).Body.Span).ShouldBe("{\"v\":2}");
        await Should.ThrowAsync<DecryptionFailedException>(async () => await receiver.UnprotectAsync(before with { Protection = "sf-aes256gcm-v1;azure-keyvault;https://other.vault.azure.net/keys/messages/v1" }, Ct));
        Should.Throw<ArgumentException>(() => new AzureKeyVaultKeyProvider(new AzureKeyVaultOptions { KeyId = new Uri("https://unit.vault.azure.net/secrets/x") }));
    }

    [Theory]
    [Trait("Id", "UT-SEC-24")]
    [Trait("Cloud", "Azure")]
    [InlineData(429, true)]
    [InlineData(503, true)]
    [InlineData(0, true)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(400, false)]
    public void AzureKeyVault_Errors_AreMappedByTransience(int status, bool transient)
    {
        AzureKeyVaultKeyProvider.Map(new RequestFailedException(status, "failed"))!.IsTransient.ShouldBe(transient);
    }

    // ================================================================ Google Cloud KMS

    [Fact]
    [Trait("Id", "UT-SEC-25")]
    [Trait("Cloud", "Gcp")]
    [Trait("Covers", "doc 10 3")]
    public async Task GoogleKms_RoundTrips_AndRefusalsArePermanent()
    {
        const string key = "projects/p/locations/global/keyRings/r/cryptoKeys/messages";
        var kms = new FakeKms();
        var provider = new GoogleKmsKeyProvider(kms, key);
        using PayloadProtector sender = Protector(provider);
        using PayloadProtector receiver = Protector(provider);

        MessageEnvelope sealedEnvelope = await sender.ProtectAsync(Envelope("{\"holder\":\"Ada\"}"), Ct);

        sealedEnvelope.Protection.ShouldBe($"sf-aes256gcm-v1;gcp-kms;{key}");
        Encoding.UTF8.GetString((await receiver.UnprotectAsync(sealedEnvelope, Ct)).Body.Span).ShouldBe("{\"holder\":\"Ada\"}");

        kms.Failure = new RpcException(new Status(StatusCode.PermissionDenied, "no"));
        using PayloadProtector cold = Protector(provider);
        await Should.ThrowAsync<DecryptionFailedException>(async () => await cold.UnprotectAsync(sealedEnvelope, Ct));
        kms.Failure = new RpcException(new Status(StatusCode.Unavailable, "down"));
        (await Should.ThrowAsync<KeyProviderException>(async () => await cold.UnprotectAsync(sealedEnvelope, Ct))).IsTransient.ShouldBeTrue();
        kms.Failure = new RpcException(new Status(StatusCode.ResourceExhausted, "quota"));
        (await Should.ThrowAsync<KeyProviderException>(async () => await provider.GenerateDataKeyAsync(Ct))).IsTransient.ShouldBeTrue();
        Should.Throw<ArgumentException>(() => new GoogleKmsKeyProvider(kms, "not/a/key"));
    }

    // ================================================================ helpers

    private static PayloadProtector Protector(IKeyProvider keys, Action<PayloadProtectionOptions>? configure = null)
    {
        var options = new PayloadProtectionOptions().ProtectAll();
        configure?.Invoke(options);
        return new PayloadProtector(keys, options);
    }

    private static MessageEnvelope Envelope(string json)
    {
        return new MessageEnvelope
        {
            MessageId = MessageIds.New(TimeProvider.System),
            Type = "bank.account.opened",
            TypeVersion = 1,
            Source = "accounts",
            Time = TimeProvider.System.GetUtcNow(),
            Body = Encoding.UTF8.GetBytes(json),
        };
    }

    // A vault with RSA key versions: wrapKey uses the current version, unwrapKey the version in the client's key id.
    private sealed class FakeVault(string keyName)
    {
        private readonly List<RSA> _versions = [RSA.Create(2048)];

        public string KeyName { get; } = keyName;

        public void Rotate()
        {
            _versions.Add(RSA.Create(2048));
        }

        public CryptographyClient Client(Uri id)
        {
            return new FakeClient(this, id.ToString());
        }

        private sealed class FakeClient(FakeVault vault, string id) : CryptographyClient
        {
            public override Task<WrapResult> WrapKeyAsync(KeyWrapAlgorithm algorithm, byte[] key, CancellationToken cancellationToken = default)
            {
                int version = vault._versions.Count;
                byte[] wrapped = vault._versions[version - 1].Encrypt(key, RSAEncryptionPadding.OaepSHA256);
                return Task.FromResult(global::Azure.Security.KeyVault.Keys.CryptographyModelFactory.WrapResult($"{vault.KeyName}/v{version}", wrapped, algorithm));
            }

            public override Task<UnwrapResult> UnwrapKeyAsync(KeyWrapAlgorithm algorithm, byte[] encryptedKey, CancellationToken cancellationToken = default)
            {
                int version = int.Parse(id[(id.LastIndexOf("/v", StringComparison.Ordinal) + 2)..], System.Globalization.CultureInfo.InvariantCulture);
                byte[] key = vault._versions[version - 1].Decrypt(encryptedKey, RSAEncryptionPadding.OaepSHA256);
                return Task.FromResult(global::Azure.Security.KeyVault.Keys.CryptographyModelFactory.UnwrapResult(id, key, algorithm));
            }
        }
    }

    // Cloud KMS: AES-GCM under a key the fake holds; the ciphertext carries its nonce and tag.
    private sealed class FakeKms : KeyManagementServiceClient
    {
        private readonly byte[] _master = RandomNumberGenerator.GetBytes(32);

        public RpcException? Failure { get; set; }

        public override Task<GcpEncryptResponse> EncryptAsync(GcpEncryptRequest request, CallSettings? callSettings = null)
        {
            if (Failure is { } failure)
            {
                throw failure;
            }

            byte[] plaintext = request.Plaintext.ToByteArray();
            byte[] output = new byte[12 + 16 + plaintext.Length];
            RandomNumberGenerator.Fill(output.AsSpan(0, 12));
            using (var aes = new AesGcm(_master, 16))
            {
                aes.Encrypt(output.AsSpan(0, 12), plaintext, output.AsSpan(28), output.AsSpan(12, 16));
            }

            return Task.FromResult(new GcpEncryptResponse { Name = request.Name + "/cryptoKeyVersions/1", Ciphertext = ByteString.CopyFrom(output) });
        }

        public override Task<GcpDecryptResponse> DecryptAsync(GcpDecryptRequest request, CallSettings? callSettings = null)
        {
            if (Failure is { } failure)
            {
                throw failure;
            }

            byte[] input = request.Ciphertext.ToByteArray();
            byte[] plaintext = new byte[input.Length - 28];
            using (var aes = new AesGcm(_master, 16))
            {
                aes.Decrypt(input.AsSpan(0, 12), input.AsSpan(28), input.AsSpan(12, 16), plaintext);
            }

            return Task.FromResult(new GcpDecryptResponse { Plaintext = ByteString.CopyFrom(plaintext) });
        }
    }
}

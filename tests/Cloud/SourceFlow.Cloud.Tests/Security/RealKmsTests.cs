using System.Text;
using SourceFlow.Cloud.Aws;
using SourceFlow.Cloud.Azure;
using SourceFlow.Cloud.Gcp;
using SourceFlow.Messaging;
using SourceFlow.Security;

namespace SourceFlow.Cloud.Tests.Security;

/// <summary>
/// The key providers against the real services (WP-5.1, nightly <c>real-cloud</c> job). Each runs only when its key is
/// configured: <c>SOURCEFLOW_KMS_AWS_KEY</c> (with the SDK credential chain and <c>AWS_REGION</c>),
/// <c>SOURCEFLOW_KMS_AZURE_KEY</c> (a Key Vault key id, with <c>DefaultAzureCredential</c>) and
/// <c>SOURCEFLOW_KMS_GCP_KEY</c> (a Cloud KMS crypto key name, with Application Default Credentials).
/// </summary>
[Trait("Category", "Integration")]
[Trait("Requires", "RealCloud")]
public sealed class RealKmsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Cloud", "Aws")]
    [Trait("Covers", "doc 10 3")]
    public async Task AwsKms_RoundTrip()
    {
        string key = Configured("SOURCEFLOW_KMS_AWS_KEY");
        using var provider = new AwsKmsKeyProvider(new AwsKmsOptions { KeyId = key, Region = Environment.GetEnvironmentVariable("AWS_REGION") });
        await RoundTripAsync(provider);
    }

    [Fact]
    [Trait("Cloud", "Azure")]
    [Trait("Covers", "doc 10 3")]
    public async Task AzureKeyVault_RoundTrip()
    {
        string key = Configured("SOURCEFLOW_KMS_AZURE_KEY");
        await RoundTripAsync(new AzureKeyVaultKeyProvider(new AzureKeyVaultOptions { KeyId = new Uri(key) }));
    }

    [Fact]
    [Trait("Cloud", "Gcp")]
    [Trait("Covers", "doc 10 3")]
    public async Task GoogleKms_RoundTrip()
    {
        string key = Configured("SOURCEFLOW_KMS_GCP_KEY");
        await RoundTripAsync(new GoogleKmsKeyProvider(new GoogleKmsOptions { KeyName = key }));
    }

    private static async Task RoundTripAsync(IKeyProvider provider)
    {
        var options = new PayloadProtectionOptions().ProtectAll();
        using var sender = new PayloadProtector(provider, options);
        using var receiver = new PayloadProtector(provider, new PayloadProtectionOptions().ProtectAll());
        var envelope = new MessageEnvelope
        {
            MessageId = MessageIds.New(TimeProvider.System),
            Type = "bank.account.opened",
            TypeVersion = 1,
            Source = "accounts",
            Time = TimeProvider.System.GetUtcNow(),
            Body = Encoding.UTF8.GetBytes("{\"holder\":\"Ada\"}"),
        };

        MessageEnvelope sealedEnvelope = await sender.ProtectAsync(envelope, Ct);
        MessageEnvelope opened = await receiver.UnprotectAsync(sealedEnvelope, Ct);

        opened.Body.ToArray().ShouldBe(envelope.Body.ToArray());
        byte[] tampered = sealedEnvelope.Body.ToArray();
        tampered[^1] ^= 0x01;
        await Should.ThrowAsync<DecryptionFailedException>(async () => await receiver.UnprotectAsync(sealedEnvelope with { Body = tampered }, Ct));
    }

    private static string Configured(string variable)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value))
        {
            Assert.Skip($"{variable} is not set.");
        }

        return value;
    }
}

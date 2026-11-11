using System.Text.Json;
using Bank.Accounts;
using Microsoft.Extensions.Compliance.Classification;
using Microsoft.Extensions.Compliance.Redaction;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Messaging;
using SourceFlow.Runtime;
using SourceFlow.Testing;

namespace SourceFlow.Core.Tests.Runtime;

/// <summary>UT-RED-01..06 (plan 05, WP-5.3): the SourceFlow taxonomy, redactors and payload logging (doc 10 §5).</summary>
[Trait("Category", "Unit")]
public sealed class RedactionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-RED-01")]
    [Trait("Covers", "doc 10 5")]
    public void PersonalDataAndSecret_MapToTheSourceFlowTaxonomy()
    {
        new PersonalDataAttribute().Classification.ShouldBe(SourceFlowTaxonomy.PersonalData);
        new SecretAttribute().Classification.ShouldBe(SourceFlowTaxonomy.Secret);
        SourceFlowTaxonomy.PersonalData.TaxonomyName.ShouldBe("SourceFlow");
        SourceFlowTaxonomy.PersonalData.ShouldNotBe(SourceFlowTaxonomy.Secret);
    }

    [Fact]
    [Trait("Id", "UT-RED-02")]
    [Trait("Id", "UT-RED-04")]
    [Trait("Covers", "doc 10 5")]
    public async Task Default_ErasesPersonalDataAndSecrets()
    {
        await using SourceFlowTestHost host = await StartAsync();
        IRedactorProvider redactors = host.Services.GetRequiredService<IRedactorProvider>();

        redactors.GetRedactor(new DataClassificationSet(SourceFlowTaxonomy.PersonalData)).Redact("Ada Lovelace").ShouldBeEmpty();
        redactors.GetRedactor(new DataClassificationSet(SourceFlowTaxonomy.Secret)).Redact("token-123").ShouldBeEmpty();
    }

    [Fact]
    [Trait("Id", "UT-RED-03")]
    [Trait("Covers", "doc 10 5")]
    public async Task HmacRedactor_IsCorrelatableButNotReversible()
    {
        await using SourceFlowTestHost host = await StartAsync(sf => sf.UseRedaction(r =>
        {
#pragma warning disable EXTEXP0002 // the HMAC redactor is marked experimental upstream
            r.SetHmacRedactor(o =>
            {
                o.Key = Convert.ToBase64String(new byte[64]);
                o.KeyId = 7;
            }, SourceFlowTaxonomy.PersonalData);
#pragma warning restore EXTEXP0002
        }));
        Redactor redactor = host.Services.GetRequiredService<IRedactorProvider>().GetRedactor(new DataClassificationSet(SourceFlowTaxonomy.PersonalData));

        string first = redactor.Redact("Ada Lovelace");
        string again = redactor.Redact("Ada Lovelace");
        string other = redactor.Redact("Grace Hopper");

        first.ShouldNotBeNullOrEmpty();
        first.ShouldBe(again);
        first.ShouldNotBe(other);
        first.ShouldNotContain("Ada");
    }

    [Fact]
    [Trait("Id", "UT-RED-05")]
    [Trait("Covers", "doc 08 4")]
    public async Task Payloads_AreOmittedFromLogsByDefault()
    {
        await using SourceFlowTestHost host = await StartAsync();
        PayloadRedaction payloads = host.Services.GetRequiredService<PayloadRedaction>();

        payloads.ForLog(Opened(host)).ShouldBe(PayloadRedaction.Omitted);
    }

    [Fact]
    [Trait("Id", "UT-RED-06")]
    [Trait("Covers", "doc 08 4;doc 10 5")]
    public async Task LogPayloads_LogsThePayload_WithPersonalDataRedacted()
    {
        await using SourceFlowTestHost host = await StartAsync(sf => sf.Configure(o => o.LogPayloads = true));
        PayloadRedaction payloads = host.Services.GetRequiredService<PayloadRedaction>();

        JsonElement logged = JsonDocument.Parse(payloads.ForLog(Opened(host))).RootElement;

        logged.GetProperty("holder").GetString().ShouldBeEmpty();               // [PersonalData] → erased
        logged.GetProperty("initialDeposit").GetDecimal().ShouldBe(25m);          // other members unchanged
        logged.GetProperty("accountId").GetString().ShouldNotBeNullOrEmpty();
    }

    private static MessageEnvelope Opened(SourceFlowTestHost host)
    {
        return host.Runtime.CreateEnvelope(new AccountOpened(AccountId.New(), "Ada Lovelace", 25m, DateTimeOffset.UnixEpoch));
    }

    private static Task<SourceFlowTestHost> StartAsync(Action<SourceFlow.Hosting.SourceFlowBuilder>? configure = null)
    {
        return SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            configure?.Invoke(sf);
        });
    }
}

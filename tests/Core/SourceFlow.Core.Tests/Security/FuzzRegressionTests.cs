using System.Text;
using System.Text.Json;
using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Runtime;
using SourceFlow.Security;

namespace SourceFlow.Core.Tests.Security;

/// <summary>
/// UT-FUZZ-01..04 (WP-5.5): the inputs fuzzing found that broke a parser's contract. Each parser rejects malformed input
/// only with the exception it documents, so a receiver that catches it can't be stopped by a crafted message.
/// </summary>
[Trait("Category", "Unit")]
public sealed class FuzzRegressionTests
{
    private static readonly IReadOnlyDictionary<string, string> Valid = new Dictionary<string, string>
    {
        ["specversion"] = "1.0",
        ["id"] = "0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b",
        ["source"] = "urn:sourceflow:accounts",
        ["type"] = "bank.account.opened.v1",
        ["time"] = "2026-01-01T00:00:00+00:00",
    };

    [Theory]
    [Trait("Id", "UT-FUZZ-01")]
    [Trait("Covers", "doc 10 2;doc 10 7")]
    [InlineData("sfheaders", "{\"h\u001e\":\"v\"}")]   // a control character in a JSON string (the fuzzer's first find)
    [InlineData("sfheaders", "[1,2]")]                // not an object
    [InlineData("sfheaders", "{\"h\":1}")]            // a non-string header value
    [InlineData("sfheaders", "{\"h\":")]              // truncated
    [InlineData("sfheaders", "{\"h\":\"a\",\"h\":\"b\"}")]   // a duplicated header (security review)
    [InlineData("sfext", "{\"sfactor\":")]            // the overflow attribute, truncated
    [InlineData("sfstreamversion", "99999999999999999999")]   // overflows a long
    public void FromAttributes_RejectsMalformedAttributes_OnlyWithFormatException(string attribute, string value)
    {
        var attributes = new Dictionary<string, string>(Valid) { [attribute] = value };

        Should.Throw<FormatException>(() => CloudEvents.FromAttributes(attributes, "{}"u8.ToArray()));
    }

    [Theory]
    [Trait("Id", "UT-FUZZ-02")]
    [Trait("Covers", "doc 10 7")]
    [InlineData("{\"specversion\":\"1.0\",")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{\"specversion\":\"1.0\",\"id\":\"x\",\"source\":\"s\",\"type\":\"t.v1\",\"time\":\"2026-01-01T00:00:00Z\",\"data_base64\":\"!!\"}")]
    [InlineData("{\"specversion\",\"1.0\"}")]
    [InlineData("{\"specversion\":\"1.0\",\"id\":\"0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b\",\"source\":\"s\",\"type\":\"a.v1\",\"type\":\"b.v1\",\"time\":\"2026-01-01T00:00:00Z\"}")]   // a duplicated attribute (security review)
    public void FromStructured_RejectsMalformedDocuments_OnlyWithFormatException(string document)
    {
        Should.Throw<FormatException>(() => CloudEvents.FromStructured(Encoding.UTF8.GetBytes(document)));
    }

    [Theory]
    [Trait("Id", "UT-FUZZ-03")]
    [Trait("Covers", "doc 10 2;GEN-19")]
    [InlineData("{\"accountId\":\"0]197e8a9b-0c1d2e3f4a5b\",\"reason\":\"done\"}")]   // the fuzzer's find: FormatException escaped
    [InlineData("{\"accountId\":42,\"reason\":\"done\"}")]
    [InlineData("{\"accountId\":{},\"reason\":\"done\"}")]
    [InlineData("{\"accountId\":null,\"reason\":\"done\"}")]
    public void GeneratedIdConverters_RejectMalformedIds_WithJsonException(string json)
    {
        using ServiceProvider services = Services();
        SourceFlowRuntime runtime = services.GetRequiredService<SourceFlowRuntime>();
        MessageTypeInfo type = runtime.Model.Registry.Get(typeof(CloseAccount));

        Should.Throw<JsonException>(() => runtime.Serializer.Deserialize(Encoding.UTF8.GetBytes(json), type));

        // Well-formed ids, in either Guid format, still read.
        var close = (CloseAccount)runtime.Serializer.Deserialize("{\"accountId\":\"0193a1b2c3d47e5f8a9b0c1d2e3f4a5b\",\"reason\":\"done\"}"u8.ToArray(), type);
        close.AccountId.ShouldBe(AccountId.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b"));
    }

    [Fact]
    [Trait("Id", "UT-FUZZ-04")]
    [Trait("Covers", "doc 10 4")]
    public async Task PersonalData_InvalidUtf8_IsJsonException()
    {
        using ServiceProvider services = Services();
        PersonalDataProtector protector = services.GetRequiredService<PersonalDataProtector>();
        byte[] invalid = [.. "{\"a"u8, 0x80, .. "\":{\"$pd\":\"v1:x\"}}"u8];   // the fuzzer's find: InvalidOperationException escaped

        await Should.ThrowAsync<JsonException>(async () => await protector.UnprotectAsync(invalid, CancellationToken.None));

        // ...and a lone-surrogate escape in valid UTF-8 (the second find).
        byte[] surrogate = """{"a\ud800":{"$pd":"v1:x"}}"""u8.ToArray();   // the JSON escape \ud800, as text
        await Should.ThrowAsync<JsonException>(async () => await protector.UnprotectAsync(surrogate, CancellationToken.None));
    }

    private static ServiceProvider Services()
    {
        var keys = new LocalKeyProvider();
        var services = new ServiceCollection();
        services.AddSingleton(keys);
        services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "fuzz-regressions";
            sf.AddModule<AccountsModule>();
            sf.Security(s => s.UseKeyProvider(keys).EncryptPersonalData());
            sf.UseInMemoryStorage();
        });
        return services.BuildServiceProvider();
    }
}

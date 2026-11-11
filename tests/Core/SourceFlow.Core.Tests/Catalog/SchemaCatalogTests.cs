using System.Text.Json.Nodes;
using Bank.Accounts;
using Bank.Contracts;
using Bank.Transfers;
using SourceFlow.Catalog;
using SourceFlow.Runtime;
using SourceFlow.Storage;
using SourceFlow.Storage.InMemory;
using SourceFlow.Testing;

namespace SourceFlow.Core.Tests.Catalog;

/// <summary>UT-CAT-01..18 (plan 05 §3.8, WP-1.12): the schema catalog, registration, compatibility and AsyncAPI export.</summary>
[Trait("Category", "Unit")]
public sealed class SchemaCatalogTests
{
    private static readonly RuntimeModel Model = new SourceFlowModelBuilder()
        .AddModule<ContractsModule>().AddModule<AccountsModule>().AddModule<TransfersModule>().Build();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ================================================================ generation

    [Fact]
    [Trait("Id", "UT-CAT-01")]
    [Trait("Covers", "doc 14 2.1")]
    public void MessageSchema_IsJsonSchema2020_WithSourceFlowExtensions()
    {
        SchemaEntry opened = Catalog().Entries.Single(e => e.Type == "bank.account.opened");
        JsonObject schema = opened.Schema;

        schema["$schema"]!.GetValue<string>().ShouldBe("https://json-schema.org/draft/2020-12/schema");
        schema["$id"]!.GetValue<string>().ShouldBe("urn:sourceflow:schema:bank.account.opened:1");
        schema["title"]!.GetValue<string>().ShouldBe("AccountOpened");
        schema["type"]!.GetValue<string>().ShouldBe("object");
        schema["x-sourceflow"]!["kind"]!.GetValue<string>().ShouldBe("event");
        JsonObject properties = schema["properties"]!.AsObject();
        properties["accountId"]!["format"]!.GetValue<string>().ShouldBe("uuid");
        properties["holder"]!["x-sourceflow-personal-data"]!.GetValue<bool>().ShouldBeTrue();
        properties["initialDeposit"]!.ToJsonString().ShouldBe("{\"type\":\"number\",\"format\":\"decimal\"}");
        properties["openedAt"]!["format"]!.GetValue<string>().ShouldBe("date-time");
        schema["required"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe(["accountId", "holder", "initialDeposit", "openedAt"]);
    }

    [Fact]
    [Trait("Id", "UT-CAT-02")]
    [Trait("Covers", "doc 14 2.2")]
    public void Catalog_CoversEveryKind_ReadModelsAndProcessStates()
    {
        SchemaCatalog catalog = Catalog();

        catalog.TryGet("account-summary", 1, out SchemaEntry? summary).ShouldBeTrue();
        summary!.Kind.ShouldBe(SchemaKind.ReadModel);
        summary.Schema["properties"]!["status"]!.ToJsonString().ShouldBe("{\"type\":\"integer\",\"x-sourceflow-enum\":\"AccountStatus\"}");
        catalog.TryGet("money-transfer", 1, out SchemaEntry? process).ShouldBeTrue();
        process!.Kind.ShouldBe(SchemaKind.ProcessState);
        catalog.TryGet("bank.account.withdraw.rejected", 1, out SchemaEntry? reply).ShouldBeTrue();
        reply!.Kind.ShouldBe(SchemaKind.Reply);
        reply.Schema["properties"]!["command"]!["type"]!.GetValue<string>().ShouldBe("object");
        catalog.Entries.Select(e => e.Kind).Distinct().Order().ShouldBe(
            [SchemaKind.Command, SchemaKind.Event, SchemaKind.IntegrationEvent, SchemaKind.Timeout, SchemaKind.Reply, SchemaKind.ReadModel, SchemaKind.ProcessState]);
    }

    [Fact]
    [Trait("Id", "UT-CAT-03")]
    [Trait("Covers", "doc 14 2.2")]
    public void Fingerprints_AreStable_AndCanonicalTextSortsKeys()
    {
        SchemaCatalog first = Catalog();
        SchemaCatalog second = Catalog();

        second.Entries.Select(e => e.Fingerprint).ShouldBe(first.Entries.Select(e => e.Fingerprint));
        first.Entries.ShouldAllBe(e => e.Fingerprint.StartsWith("sha256:", StringComparison.Ordinal) && e.Fingerprint.Length == 71);
        JsonSchemaGenerator.Canonical(new JsonObject { ["b"] = 1, ["a"] = new JsonObject { ["d"] = true, ["c"] = "x" } })
            .ShouldBe("{\"a\":{\"c\":\"x\",\"d\":true},\"b\":1}");
    }

    // ================================================================ registration

    [Fact]
    [Trait("Id", "UT-CAT-04")]
    [Trait("Covers", "doc 14 2.2")]
    public async Task Registration_IsIdempotent()
    {
        var store = new InMemoryStore();
        SchemaCatalog catalog = Catalog();

        (await store.RegisterAsync("bank", catalog.Entries, Ct)).ShouldBeEmpty();
        (await store.RegisterAsync("bank", catalog.Entries, Ct)).ShouldBeEmpty();
        (await ((ISchemaStore)store).ListAsync(Ct)).Count.ShouldBe(catalog.Entries.Count);
    }

    [Fact]
    [Trait("Id", "UT-CAT-05")]
    [Trait("Covers", "doc 14 2.2")]
    public async Task ChangedPublishedVersion_FailsStartup()
    {
        var store = new InMemoryStore();
        await store.RegisterAsync("bank", [new SchemaEntry("bank.account.opened", 1, SchemaKind.Event, new JsonObject { ["type"] = "object" })], Ct);

        SchemaConflictException e = await Should.ThrowAsync<SchemaConflictException>(async () =>
            await SourceFlowTestHost.StartAsync(sf =>
            {
                sf.ServiceName = "bank";
                sf.AddModule<AccountsModule>();
            }, store: store));

        SchemaConflict conflict = e.Conflicts.ShouldHaveSingleItem();
        conflict.Type.ShouldBe("bank.account.opened");
        conflict.Version.ShouldBe(1);
        conflict.CurrentFingerprint.ShouldNotBe(conflict.RegisteredFingerprint);
    }

    [Fact]
    [Trait("Id", "UT-CAT-06")]
    [Trait("Covers", "doc 14 2.2")]
    public async Task WarnMode_StartsDespiteAConflict_AndNewSchemasAreRegistered()
    {
        var store = new InMemoryStore();
        await store.RegisterAsync("bank", [new SchemaEntry("bank.account.opened", 1, SchemaKind.Event, new JsonObject { ["type"] = "object" })], Ct);

        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "bank";
            sf.AddModule<AccountsModule>();
            sf.Configure(o => o.SchemaRegistration = SchemaRegistrationMode.Warn);
        }, store: store);

        (await ((ISchemaStore)store).ListAsync(Ct)).ShouldContain(e => e.Type == "bank.account.deposited");
    }

    [Fact]
    [Trait("Id", "UT-CAT-07")]
    [Trait("Covers", "doc 14 2.2")]
    public void LockFile_RoundTrips()
    {
        SchemaCatalog catalog = Catalog();

        SchemaCatalog read = SchemaCatalog.FromLockFile(catalog.ToLockFile());

        read.Entries.Select(e => (e.Type, e.Version, e.Kind, e.Fingerprint)).ShouldBe(catalog.Entries.Select(e => (e.Type, e.Version, e.Kind, e.Fingerprint)));
        SchemaCompatibility.Check(read, catalog).ShouldBeEmpty();
    }

    // ================================================================ compatibility (doc 14 §2.2)

    [Fact]
    [Trait("Id", "UT-CAT-08")]
    public void SFC01_AddingAnOptionalField_IsAllowed()
    {
        Finding(Event("a:string!"), Event("a:string!", "b:string?")).ShouldBe(("SFC01", CompatibilitySeverity.Allowed, "b"));
    }

    [Fact]
    [Trait("Id", "UT-CAT-09")]
    public void SFC02_AddingARequiredField_IsBreaking()
    {
        Finding(Event("a:string!"), Event("a:string!", "b:string!")).ShouldBe(("SFC02", CompatibilitySeverity.Breaking, "b"));
    }

    [Fact]
    [Trait("Id", "UT-CAT-10")]
    public void SFC03_RemovingAField_IsBreaking()
    {
        Finding(Event("a:string!", "b:string?"), Event("a:string!")).ShouldBe(("SFC03", CompatibilitySeverity.Breaking, "b"));
    }

    [Fact]
    [Trait("Id", "UT-CAT-11")]
    public void SFC04_RetypingAField_IsBreaking()
    {
        Finding(Event("a:string!"), Event("a:integer!")).ShouldBe(("SFC04", CompatibilitySeverity.Breaking, "a"));
    }

    [Fact]
    [Trait("Id", "UT-CAT-12")]
    public void SFC05_OptionalBecomingRequired_IsBreaking_TheReverseIsAllowed()
    {
        Finding(Event("a:string?"), Event("a:string!")).ShouldBe(("SFC05", CompatibilitySeverity.Breaking, "a"));
        SchemaCompatibility.Check(Catalog(Event("a:string!")), Catalog(Event("a:string?"))).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Id", "UT-CAT-13")]
    public void SFC06_ANewVersionRenamingWithRenamedFrom_IsAllowed()
    {
        SchemaEntry v1 = Event("holder:string!").WithUpcast(2);
        SchemaEntry v2 = Event(2, "name:string!@1=holder");

        IReadOnlyList<CompatibilityFinding> findings = SchemaCompatibility.Check(Catalog(v1), Catalog(Event("holder:string!").WithUpcast(2), v2));

        findings.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            f => f.Rule.ShouldBe("SFC06"),
            f => f.Severity.ShouldBe(CompatibilitySeverity.Allowed),
            f => f.Field.ShouldBe("name"));
    }

    [Fact]
    [Trait("Id", "UT-CAT-14")]
    public void SFC07_ANewVersionDroppingAFieldWithoutRenameOrUpcaster_IsBreaking()
    {
        SchemaEntry v1 = Event("holder:string!", "note:string?");
        IReadOnlyList<CompatibilityFinding> findings = SchemaCompatibility.Check(Catalog(v1), Catalog(v1, Event(2, "holder:string!")));

        findings.ShouldContain(f => f.Rule == "SFC07" && f.Field == "note" && f.Severity == CompatibilitySeverity.Breaking);
    }

    [Fact]
    [Trait("Id", "UT-CAT-15")]
    public void SFC08_ANewEventVersionWithoutAnUpcaster_IsBreaking_WithOneIsAllowed()
    {
        SchemaEntry v1 = Event("a:string!");
        SchemaEntry v2 = Event(2, "a:string!", "b:string?");

        SchemaCompatibility.Check(Catalog(v1), Catalog(v1, v2)).ShouldContain(f => f.Rule == "SFC08" && f.Severity == CompatibilitySeverity.Breaking);
        SchemaCompatibility.Check(Catalog(v1), Catalog(Event("a:string!").WithUpcast(2), v2)).ShouldNotContain(f => f.Rule == "SFC08");
    }

    [Fact]
    [Trait("Id", "UT-CAT-16")]
    public void SFC09_RemovingAPublishedVersion_IsBreaking_ExceptReadModelGenerations()
    {
        SchemaCompatibility.Check(Catalog(Event("a:string!")), Catalog()).ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            f => f.Rule.ShouldBe("SFC09"), f => f.Severity.ShouldBe(CompatibilitySeverity.Breaking));
        var readModel = new SchemaEntry("summary", 1, SchemaKind.ReadModel, Object(("a", "string", true, null)));
        SchemaCompatibility.Check(Catalog(readModel), Catalog()).ShouldHaveSingleItem().Severity.ShouldBe(CompatibilitySeverity.Allowed);
    }

    [Fact]
    [Trait("Id", "UT-CAT-17")]
    public void SFC10_FlagChanges_AreAllowed_AndReported()
    {
        JsonObject before = Object(("a", "string", true, null));
        JsonObject after = Object(("a", "string", true, null));
        after["properties"]!["a"]!["x-sourceflow-queryable"] = true;

        Finding(new SchemaEntry("t.e", 1, SchemaKind.Event, before), new SchemaEntry("t.e", 1, SchemaKind.Event, after))
            .ShouldBe(("SFC10", CompatibilitySeverity.Allowed, "a"));
    }

    // ================================================================ export

    [Fact]
    [Trait("Id", "UT-CAT-18")]
    [Trait("Covers", "doc 14 2.2")]
    public async Task AsyncApiExport_HasTheEndpoint_PublishedTopics_PayloadsAndCloudEventsHeaders()
    {
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<ContractsModule>();
            sf.AddModule<AccountsModule>();
            sf.Routing(r => r.Publish<AccountOpened>().As(AccountContracts.ToV1).To("accounts.events"));
        });

        string text = AsyncApiExporter.Export(host.Runtime.Model, host.Runtime.Routing, SchemaCatalog.Build(host.Runtime.Model), "accounts", "3.0.0");
        JsonNode document = JsonNode.Parse(text)!;

        document["asyncapi"]!.GetValue<string>().ShouldBe("3.0.0");
        document["info"]!["title"]!.GetValue<string>().ShouldBe("accounts");
        document["channels"]!["accounts"]!["messages"]!.AsObject().Select(m => m.Key).ShouldContain("bank.account.open_v1");
        document["channels"]!["accounts.events"]!["messages"]!.AsObject().Select(m => m.Key).ShouldBe(["accounts.account-opened_v1"]);
        document["operations"]!["publish-accounts.events"]!["action"]!.GetValue<string>().ShouldBe("send");
        JsonNode message = document["components"]!["messages"]!["accounts.account-opened_v1"]!;
        message["payload"]!["x-sourceflow"]!["kind"]!.GetValue<string>().ShouldBe("integration-event");
        message["headers"]!["$ref"]!.GetValue<string>().ShouldBe("#/components/schemas/CloudEventsHeaders");
        document["components"]!["schemas"]!["CloudEventsHeaders"]!["required"]!.AsArray().Count.ShouldBe(4);
    }

    // ================================================================ helpers

    private static SchemaCatalog Catalog()
    {
        return SchemaCatalog.Build(Model);
    }

    private static SchemaCatalog Catalog(params SchemaEntry[] entries)
    {
        return new SchemaCatalog(entries);
    }

    private static (string Rule, CompatibilitySeverity Severity, string? Field) Finding(SchemaEntry before, SchemaEntry after)
    {
        CompatibilityFinding finding = SchemaCompatibility.Check(Catalog(before), Catalog(after)).ShouldHaveSingleItem();
        return (finding.Rule, finding.Severity, finding.Field);
    }

    // Fields as "name:type!" (required) or "name:type?" (optional), optionally "@version=oldName" for [RenamedFrom].
    private static SchemaEntry Event(params string[] fields)
    {
        return Event(1, fields);
    }

    private static SchemaEntry Event(int version, params string[] fields)
    {
        var parsed = fields.Select(f =>
        {
            string[] rename = f.Split('@');
            string[] parts = rename[0].Split(':');
            bool required = parts[1].EndsWith('!');
            return (parts[0], parts[1].TrimEnd('!', '?'), required, rename.Length > 1 ? rename[1] : null);
        }).ToArray();
        JsonObject schema = Object(parsed);
        schema["x-sourceflow"] = new JsonObject { ["type"] = "t.e", ["version"] = version, ["kind"] = "event" };
        return new SchemaEntry("t.e", version, SchemaKind.Event, schema);
    }

    private static JsonObject Object(params (string Name, string Type, bool Required, string? RenamedFrom)[] fields)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach ((string name, string type, bool isRequired, string? renamedFrom) in fields)
        {
            var field = new JsonObject { ["type"] = type };
            if (renamedFrom is not null)
            {
                string[] parts = renamedFrom.Split('=');
                field["x-sourceflow-renamed-from"] = new JsonObject { [parts[0]] = parts[1] };
            }

            properties[name] = field;
            if (isRequired)
            {
                required.Add((JsonNode?)JsonValue.Create(name));
            }
        }

        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
    }
}

/// <summary>Marks a test event version as upcasting to the next version (x-sourceflow.upcastsTo).</summary>
internal static class UpcastExtensions
{
    public static SchemaEntry WithUpcast(this SchemaEntry entry, int to)
    {
        JsonObject schema = entry.Schema.DeepClone().AsObject();
        schema["x-sourceflow"]!["upcastsTo"] = to;
        return new SchemaEntry(entry.Type, entry.Version, entry.Kind, schema);
    }
}

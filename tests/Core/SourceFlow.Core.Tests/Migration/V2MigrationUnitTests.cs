using System.Data;
using System.Text;
using System.Text.Json.Nodes;
using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Migration;

namespace SourceFlow.Core.Tests.Migration;

/// <summary>
/// UT-MIG-01..06 (WP-5.4): the deterministic id map, v2 type names, row conversions, the v2 message allow-list and the
/// migration's configuration. The database steps are MIG-01..09 (storage tests), the AWS bridge MIG-10..12.
/// </summary>
[Trait("Category", "Unit")]
public sealed class V2MigrationUnitTests
{
    [Fact]
    [Trait("Id", "UT-MIG-01")]
    [Trait("Covers", "doc 12 3")]
    public void LegacyIds_AreDeterministicVersion5Uuids_ScopedOnRequest()
    {
        Guid first = V2Ids.FromLegacy(999);

        first.ShouldBe(V2Ids.FromLegacy(999));
        MessageIds.VersionOf(first).ShouldBe(5);
        first.ShouldBe(MessageIds.CreateVersion5(V2Ids.Namespace, "999"));
        V2Ids.FromLegacy(1000).ShouldNotBe(first);
        V2Ids.FromLegacy(999, "BankAccount").ShouldNotBe(first);
        V2Ids.FromLegacy(999, "BankAccount").ShouldBe(MessageIds.CreateVersion5(V2Ids.Namespace, "BankAccount/999"));
        V2Ids.FromLegacy<AccountId>(999).Value.ShouldBe(first);
        V2Ids.FromLegacy<AccountId>(0).IsDefault.ShouldBeFalse();   // even v2's id 0 maps to a valid id
    }

    [Theory]
    [Trait("Id", "UT-MIG-02")]
    [Trait("Covers", "doc 07 8")]
    [InlineData("MyApp.Commands.CreateAccount, MyApp", "MyApp.Commands.CreateAccount, MyApp")]
    [InlineData("MyApp.Commands.CreateAccount, MyApp, Version=1.2.0.0, Culture=neutral, PublicKeyToken=null", "MyApp.Commands.CreateAccount, MyApp")]
    [InlineData("  MyApp.Commands.CreateAccount ,MyApp,Version=3.0.0.0 ", "MyApp.Commands.CreateAccount, MyApp")]
    [InlineData("MyApp.Events.Updated`1[[MyApp.Account, MyApp, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]], MyApp, Version=1.0.0.0, Culture=neutral, PublicKeyToken=abc123",
        "MyApp.Events.Updated`1[[MyApp.Account, MyApp]], MyApp")]
    public void V2TypeNames_IgnoreVersionCultureAndKey(string name, string normalized)
    {
        V2TypeNames.Normalize(name).ShouldBe(normalized);
    }

    [Fact]
    [Trait("Id", "UT-MIG-03")]
    [Trait("Covers", "doc 12 3")]
    public void V2Rows_ReadTheColumnTypesV2WroteOnEveryProvider()
    {
        using var table = new DataTable();
        foreach ((string name, Type type) in new[]
        {
            ("Id", typeof(long)), ("SqliteBool", typeof(long)), ("TextBool", typeof(string)), ("NativeBool", typeof(bool)),
            ("Real", typeof(double)), ("Numeric", typeof(decimal)), ("TextDate", typeof(string)), ("Date", typeof(DateTime)),
            ("Name", typeof(string)), ("Missing", typeof(string)), ("Blob", typeof(byte[])),
        })
        {
            table.Columns.Add(name, type);
        }

        table.Rows.Add(42L, 1L, "true", false, 10.25d, 99.95m, "2025-01-10T09:00:00", new DateTime(2025, 1, 11, 8, 0, 0, DateTimeKind.Unspecified), "Ada", DBNull.Value, new byte[] { 1, 2 });
        using DataTableReader reader = table.CreateDataReader();
        reader.Read().ShouldBeTrue();
        var row = new V2Row(reader);

        row.Int32("id").ShouldBe(42);                   // column names are case-insensitive, as in v2's EF model
        row.Int64("Id").ShouldBe(42L);
        row.Boolean("SqliteBool").ShouldBeTrue();
        row.Boolean("TextBool").ShouldBeTrue();
        row.Boolean("NativeBool").ShouldBeFalse();
        row.Decimal("Real").ShouldBe(10.25m);
        row.Decimal("Numeric").ShouldBe(99.95m);
        row.Double("Numeric").ShouldBe(99.95d);
        row.DateTime("TextDate").ShouldBe(new DateTime(2025, 1, 10, 9, 0, 0, DateTimeKind.Utc));
        row.DateTime("TextDate").Kind.ShouldBe(DateTimeKind.Utc);
        row.DateTime("Date").Kind.ShouldBe(DateTimeKind.Utc);
        row.String("Name").ShouldBe("Ada");
        row.String("Id").ShouldBe("42");
        row.String("Missing").ShouldBeNull();
        row.IsNull("Missing").ShouldBeTrue();
        row.Int32("Missing").ShouldBe(0);
        row.Bytes("Blob").ShouldBe(new byte[] { 1, 2 });
        row.Has("Balance").ShouldBeFalse();
        Should.Throw<KeyNotFoundException>(() => row.Decimal("Balance")).Message.ShouldContain("Balance");
    }

    [Fact]
    [Trait("Id", "UT-MIG-04")]
    [Trait("Covers", "doc 07 8")]
    public void MessageMap_TranslatesMappedTypes_WithDeterministicIds_AndMarksUnmappedOnes()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var map = new V2MessageMap()
            .Type("MyApp.Commands.CreateAccount, MyApp", "bank.account.open", 1, v2 => new JsonObject
            {
                ["accountId"] = V2Ids.FromLegacy(v2.EntityId ?? 0).ToString(),
                ["holder"] = v2.Payload!["accountName"]!.GetValue<string>(),
                ["initialDeposit"] = v2.Payload!["initialAmount"]!.GetValue<decimal>(),
            })
            .Type("MyApp.Events.AccountUpdated, MyApp", "legacy.account.updated", 2);
        var command = new V2Message
        {
            V2Type = "MyApp.Commands.CreateAccount, MyApp, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
            MessageId = "sqs-1",
            Body = """{"metadata":{"sequenceNo":1},"name":"CreateAccount","payload":{"id":7,"initialAmount":100,"accountName":"Ada"},"entity":{"id":7,"isNew":false}}""",
            EntityId = 7,
            SequenceNo = 1,
        };

        MessageEnvelope envelope = map.Translate(command, time);
        envelope.Type.ShouldBe("bank.account.open");
        envelope.TypeVersion.ShouldBe(1);
        envelope.MessageId.ShouldBe(V2MessageMap.MessageIdOf("sqs-1"));
        envelope.MessageId.ShouldBe(map.Translate(command, time).MessageId);   // a redelivery is a duplicate
        MessageIds.VersionOf(envelope.MessageId.Value).ShouldBe(5);
        envelope.Source.ShouldBe(V2MessageMap.Source);
        envelope.Time.ShouldBe(time.GetUtcNow());
        envelope.Headers[V2MessageMap.V2TypeHeader].ShouldBe(command.V2Type);
        envelope.Headers[V2MessageMap.V2MessageIdHeader].ShouldBe("sqs-1");
        envelope.Headers.ContainsKey(V2MessageMap.UnmappedHeader).ShouldBeFalse();
        JsonNode body = JsonNode.Parse(envelope.Body.Span)!;
        body["accountId"]!.GetValue<string>().ShouldBe(V2Ids.FromLegacy(7).ToString());
        body["holder"]!.GetValue<string>().ShouldBe("Ada");

        // Without a converter the v2 payload is the body.
        MessageEnvelope updated = map.Translate(new V2Message { V2Type = "MyApp.Events.AccountUpdated, MyApp", MessageId = "sns-1", Body = """{"name":"AccountUpdated","payload":{"id":7,"balance":5}}""", IsEvent = true }, time);
        updated.TypeVersion.ShouldBe(2);
        Encoding.UTF8.GetString(updated.Body.Span).ShouldBe("""{"id":7,"balance":5}""");

        // An unmapped type keeps its v2 name and body, marked for parking.
        MessageEnvelope unmapped = map.Translate(new V2Message { V2Type = "MyApp.Commands.Unknown, MyApp, Version=1.0.0.0", MessageId = "sqs-2", Body = "not json" }, time);
        unmapped.Type.ShouldBe("MyApp.Commands.Unknown, MyApp");
        unmapped.Headers[V2MessageMap.UnmappedHeader].ShouldBe("true");
        Encoding.UTF8.GetString(unmapped.Body.Span).ShouldBe("not json");

        map.TryGet("MyApp.Commands.CreateAccount, MyApp, Version=9.9.9.9", out V2MessageMapping? mapping).ShouldBeTrue();
        mapping!.Type.ShouldBe("bank.account.open");
        Should.Throw<ArgumentException>(() => map.Type("MyApp.Commands.CreateAccount, MyApp, Version=2.0.0.0", "x", 1));
        Should.Throw<ArgumentOutOfRangeException>(() => map.Type("A, B", "x", 0));
    }

    [Fact]
    [Trait("Id", "UT-MIG-05")]
    [Trait("Covers", "doc 12 3")]
    public async Task Migration_NeedsItsSource_AndTheServiceForImportAndVerify()
    {
        Should.Throw<ArgumentException>(() => new ServiceCollection().AddSourceFlow(sf => sf.MigrateFromV2(_ => { }))).Message.ShouldContain("From(");

        using var source = new UnusedDataSource();
        var builder = new V2MigrationBuilder().From(new V2Source(source, V2Dialect.PostgreSql)).Import(new AccountImport());
        Should.Throw<ArgumentException>(() => builder.Import(new AccountImport())).Message.ShouldContain("already has an import");

        var migration = V2Migration.ForSource(new V2Source(source, V2Dialect.SqlServer), m => m.Import(new AccountImport()));
        migration.ImportedTables.ShouldBe(["Account"]);
        (await Should.ThrowAsync<InvalidOperationException>(async () => await migration.ImportAsync())).Message.ShouldContain("inside the service");
        (await Should.ThrowAsync<InvalidOperationException>(async () => await migration.VerifyAsync())).Message.ShouldContain("inside the service");
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await migration.ImportAsync(new V2ImportOptions { BatchSize = 0 }));
    }

    [Fact]
    [Trait("Id", "UT-MIG-06")]
    [Trait("Covers", "doc 12 3")]
    public void Imports_OwnEveryCommandByDefault()
    {
        IV2Import<V2Account, BankAccount, AccountId> import = new AccountImport();

        import.OwnsCommand("Anything, Anywhere").ShouldBeTrue();
        import.MapId(new V2Account(5)).ShouldBe(V2Ids.FromLegacy<AccountId>(5));
    }

    private sealed record V2Account(int Id) : IV2Entity<V2Account>
    {
        public static string TableName => "Account";

        public static V2Account Read(V2Row row)
        {
            return new(row.Int32("Id"));
        }
    }

    private sealed class AccountImport : IV2Import<V2Account, BankAccount, AccountId>
    {
        public AccountId MapId(V2Account entity)
        {
            return V2Ids.FromLegacy<AccountId>(entity.Id);
        }

        public IEnumerable<IEvent> ToEvents(V2Account entity, V2ImportContext context)
        {
            yield return new AccountOpened(MapId(entity), "x", 1m, context.ImportedAt);
        }

        public IEnumerable<string> Compare(V2Account entity, BankAccount state)
        {
            return [];
        }
    }

    private sealed class UnusedDataSource : System.Data.Common.DbDataSource
    {
        public override string ConnectionString => string.Empty;

        protected override System.Data.Common.DbConnection CreateDbConnection()
        {
            throw new InvalidOperationException("No database in unit tests.");
        }
    }
}

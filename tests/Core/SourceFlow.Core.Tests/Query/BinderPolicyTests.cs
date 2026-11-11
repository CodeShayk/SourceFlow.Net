using System.Collections.Frozen;
using SourceFlow.Query;
using SourceFlow.Core.Tests.Runtime;
using SourceFlow.Runtime;

namespace SourceFlow.Core.Tests.Query;

/// <summary>UT-QL-21..40, 79..92: binder and policy (doc 14 §4.4, §5, §7.3, §8).</summary>
[Trait("Category", "Unit")]
public sealed class BinderPolicyTests
{
    private static readonly IReadOnlyDictionary<int, string> NoRenames = FrozenDictionary<int, string>.Empty;

    internal static QueryCatalog Catalog { get; } = BuildCatalog();

    private static QueryField F(string name, QueryType type, bool queryable = false, bool personal = false, IReadOnlyDictionary<int, string>? renamed = null, QueryField? element = null, IReadOnlyList<QueryField>? fields = null)
    {
        return new QueryField(name, type, queryable, personal, renamed ?? NoRenames, fields ?? [], element);
    }

    private static QueryCatalog BuildCatalog()
    {
        var opened = new QueryEventType("bank.account.opened",
        [
            new QueryEventVersion(1, typeof(object), [F("accountId", QueryType.String, queryable: true), F("holder", QueryType.String, personal: true), F("initialDeposit", QueryType.Decimal), F("branch", QueryType.String)]),
            new QueryEventVersion(2, typeof(object),
            [
                F("accountId", QueryType.String, queryable: true),
                F("givenName", QueryType.String, personal: true, renamed: new Dictionary<int, string> { [1] = "holder" }),
                F("branchCode", QueryType.String, renamed: new Dictionary<int, string> { [1] = "branch" }),
                F("initialDeposit", QueryType.Decimal),
            ]),
        ]);
        var deposited = new QueryEventType("bank.account.deposited",
        [
            new QueryEventVersion(1, typeof(object),
            [
                F("accountId", QueryType.String, queryable: true),
                F("amount", QueryType.Decimal),
                F("items", QueryType.Array, element: F("[]", QueryType.Object, fields: [F("sku", QueryType.String)])),
            ]),
        ]);
        var withdrawn = new QueryEventType("bank.account.withdrawn", [new QueryEventVersion(1, typeof(object), [F("accountId", QueryType.String, queryable: true), F("amount", QueryType.Integer)])]);
        var noted = new QueryEventType("bank.account.noted", [new QueryEventVersion(1, typeof(object), [F("amount", QueryType.String)])]);
        var summary = new QueryReadModel("account-summary", "account-summary@1", typeof(object),
        [
            F("id", QueryType.String), F("holder", QueryType.String, personal: true), F("balance", QueryType.Decimal, queryable: true),
            F("status", QueryType.String), F("customerId", QueryType.String, queryable: true), F("tags", QueryType.Array, element: F("[]", QueryType.String)),
        ]);
        var profile = new QueryReadModel("customer-profile", "customer-profile@1", typeof(object),
            [F("id", QueryType.String), F("segment", QueryType.String), F("fullName", QueryType.String, personal: true)]);
        var account = new QueryAggregate("bank-account", typeof(object), [F("balance", QueryType.Decimal), F("holder", QueryType.String, personal: true)]);
        return new QueryCatalog([opened, deposited, withdrawn, noted], [summary, profile], [account]);
    }

    internal static BoundQuery Bound(string text, QueryLanguage language = QueryLanguage.Sfql)
    {
        ParseResult parsed = SfqlParser.Parse(text, language);
        parsed.Diagnostics.ShouldBeEmpty(string.Join("; ", parsed.Diagnostics));
        BindResult result = SfqlBinder.Bind(parsed.Query!, text, Catalog);
        result.Diagnostics.ShouldBeEmpty(string.Join("; ", result.Diagnostics));
        return result.Query!;
    }

    private static QueryDiagnostic BindError(string text, QueryLanguage language = QueryLanguage.Sfql)
    {
        ParseResult parsed = SfqlParser.Parse(text, language);
        parsed.Diagnostics.ShouldBeEmpty(string.Join("; ", parsed.Diagnostics));
        return SfqlBinder.Bind(parsed.Query!, text, Catalog).Diagnostics.ShouldHaveSingleItem();
    }

    private static IReadOnlyList<QueryDiagnostic> Policy(string text, QueryPermissions? permissions = null, Dictionary<string, object?>? parameters = null, QueryLanguage language = QueryLanguage.Sfql)
    {
        return SfqlPolicy.Check(Bound(text, language), text, permissions ?? QueryPermissions.Service, parameters);
    }

    // ---------------------------------------------------------------- binding

    [Fact]
    [Trait("Id", "UT-QL-21")]
    public void EventAttributes_AndDefaults()
    {
        BoundQuery query = Bound("from events | where sfcorrelationid = @c");

        query.Outputs.Select(o => o.Name).ShouldBe(["position", "stream", "version", "type", "typeversion", "time", "data"]);
        query.Parameters["c"].ShouldBe(QueryType.String);
        query.Sources.ShouldHaveSingleItem().Kind.ShouldBe(SourceKind.Events);
        query.OrderBy.ShouldHaveSingleItem();   // G-6 tiebreak: position
    }

    [Fact]
    [Trait("Id", "UT-QL-22")]
    public void OpaqueData_IsSfq0201_ExceptExistsAndWholeData()
    {
        BindError("from events | where data.amount > 1").Code.ShouldBe(QueryCodes.OpaqueData);
        Bound("from events | where exists data.amount and sfcorrelationid = @c | project data");
    }

    [Fact]
    [Trait("Id", "UT-QL-23")]
    [Trait("Covers", "ADR-023")]
    public void UnknownNames_AndForeignTypes()
    {
        BindError("from events of \"orders.order.placed\"").Code.ShouldBe(QueryCodes.NotOwned);
        BindError("from readmodel \"nope\"").Code.ShouldBe(QueryCodes.UnknownName);
        BindError("from events of \"bank.account.deposited\" | where data.nope = 1").Code.ShouldBe(QueryCodes.UnknownName);
        BindError("from readmodel \"account-summary\" | where nope = 1").Code.ShouldBe(QueryCodes.UnknownName);
        BindError("from readmodel \"account-summary\" | where x.balance = 1").Code.ShouldBe(QueryCodes.UnknownName);
    }

    [Fact]
    [Trait("Id", "UT-QL-24")]
    public void FieldsAcrossTypes_MissingInSome_IncompatibleTypesRejected()
    {
        BoundQuery query = Bound("from events of \"bank.account.deposited\", \"bank.account.opened\" | where data.amount > 1 and time >= ago(1d)");

        BoundColumn amount = query.Column(((BinaryExpr)((BinaryExpr)query.Ast.Where!).Left).Left);
        amount.VersionPaths![("bank.account.deposited", 1)].ShouldBe(["amount"]);
        amount.VersionPaths[("bank.account.opened", 1)].ShouldBeNull();   // missing → evaluates as missing (§5.5)
        BindError("from events of \"bank.account.deposited\", \"bank.account.noted\" | where data.amount > 1").Code.ShouldBe(QueryCodes.UnknownName);
        BoundQuery mixed = Bound("from events of \"bank.account.deposited\", \"bank.account.withdrawn\" | where data.amount > 1 and time >= ago(1d)");
        mixed.Column(((BinaryExpr)((BinaryExpr)mixed.Ast.Where!).Left).Left).Type.ShouldBe(QueryType.Decimal);   // decimal and integer unify
    }

    [Fact]
    [Trait("Id", "UT-QL-25")]
    public void RenamedFrom_RewritesThePathPerVersion()
    {
        BoundQuery query = Bound("from events of \"bank.account.opened\" | where data.branchCode = 'X1' and time >= ago(1d)");

        BoundColumn branch = query.Column(((BinaryExpr)((BinaryExpr)query.Ast.Where!).Left).Left);
        branch.VersionPaths![("bank.account.opened", 1)].ShouldBe(["branch"]);
        branch.VersionPaths[("bank.account.opened", 2)].ShouldBe(["branchCode"]);
        Bound("from events of \"bank.account.opened\" version 1 | where data.branch = 'X1' and time >= ago(1d)")
            .Sources[0].Version.ShouldBe(1);
    }

    [Fact]
    [Trait("Id", "UT-QL-26")]
    public void NestedPathsAndArrays()
    {
        BoundQuery query = Bound("from events of \"bank.account.deposited\" | project sku = data.items[0].sku");

        query.Outputs.ShouldHaveSingleItem().Type.ShouldBe(QueryType.String);
        query.Column(query.Outputs[0].Expression).VersionPaths![("bank.account.deposited", 1)].ShouldBe(["items", 0, "sku"]);
    }

    [Fact]
    [Trait("Id", "UT-QL-27")]
    public void TypeMismatches_AreSfq0203()
    {
        BindError("from readmodel \"account-summary\" | where balance = 'lots'").Code.ShouldBe(QueryCodes.TypeMismatch);
        BindError("from readmodel \"account-summary\" | where status > 5").Code.ShouldBe(QueryCodes.TypeMismatch);
        BindError("from readmodel \"account-summary\" | where balance").Code.ShouldBe(QueryCodes.TypeMismatch);
        BindError("from readmodel \"account-summary\" | where balance like 'x%'").Code.ShouldBe(QueryCodes.TypeMismatch);
        BindError("from readmodel \"account-summary\" | where lower(status) = 1").Code.ShouldBe(QueryCodes.TypeMismatch);
        BindError("from readmodel \"account-summary\" | project n = ago(5)").Code.ShouldBe(QueryCodes.TypeMismatch);
    }

    [Fact]
    [Trait("Id", "UT-QL-28")]
    public void Parameters_AreTypedFromTheirUse()
    {
        BoundQuery query = Bound("from readmodel \"account-summary\" | where balance < @below and status in (@a, @b) | take @n");

        query.Parameters["below"].ShouldBe(QueryType.Decimal);
        query.Parameters["a"].ShouldBe(QueryType.String);
        query.Parameters["n"].ShouldBe(QueryType.Integer);
    }

    [Fact]
    [Trait("Id", "UT-QL-29")]
    public void OutputNames_AreUnique_AndOrderByMayNameAnOutput()
    {
        BoundQuery query = Bound("from readmodel \"account-summary\" | project balance, b = balance, balance | order by b desc");

        query.Outputs.Select(o => o.Name).ShouldBe(["balance", "b", "balance_2"]);
        query.Column(query.OrderBy[0].Expression).Kind.ShouldBe(ColumnKind.Output);
        query.OrderBy.Count.ShouldBe(2);   // + id tiebreak (G-6)
    }

    [Fact]
    [Trait("Id", "UT-QL-30")]
    public void TieBreak_FollowsGroupingAndDistinct()
    {
        Bound("from readmodel \"account-summary\" | summarize n = count() by status").OrderBy.Select(s => AstPrinter.Expr(s.Expression)).ShouldBe(["status"]);
        Bound("from readmodel \"account-summary\" | distinct status, customerId").OrderBy.Select(s => AstPrinter.Expr(s.Expression)).ShouldBe(["status", "customerId"]);
        Bound("from readmodel \"account-summary\" | order by balance").OrderBy.Select(s => AstPrinter.Expr(s.Expression)).ShouldBe(["balance", "id"]);
    }

    [Fact]
    [Trait("Id", "UT-QL-31")]
    public void Grouping_G11_And_AggregatesOutOfPlace()
    {
        BindError("from readmodel \"account-summary\" | summarize n = count() by status | project status, balance").Code.ShouldBe(QueryCodes.NotGrouped);
        BindError("SELECT status, balance, COUNT(*) FROM readmodel.\"account-summary\" GROUP BY status", QueryLanguage.Sql).Code.ShouldBe(QueryCodes.NotGrouped);
        BindError("SELECT * FROM readmodel.\"account-summary\" GROUP BY status", QueryLanguage.Sql).Code.ShouldBe(QueryCodes.NotGrouped);
        BindError("from readmodel \"account-summary\" | where count() > 1").Code.ShouldBe(QueryCodes.NotGrouped);
        BindError("SELECT sum(count(*)) FROM readmodel.\"account-summary\"", QueryLanguage.Sql).Code.ShouldBe(QueryCodes.NotGrouped);
        Bound("SELECT status, lower(status) AS s, COUNT(*) + 1 AS n FROM readmodel.\"account-summary\" GROUP BY status HAVING COUNT(*) > 1", QueryLanguage.Sql);
    }

    [Fact]
    [Trait("Id", "UT-QL-79")]
    [Trait("Covers", "ADR-026")]
    public void Joins_QualifyAmbiguityAndBindPerSource()
    {
        BoundQuery query = Bound("""
            from readmodel "account-summary" as a
            | join readmodel "customer-profile" as c on c.id = a.customerId
            | project a.id, c.segment
            """);

        query.Sources.Select(s => s.Name).ShouldBe(["a", "c"]);
        query.Column(query.Outputs[1].Expression).Source.ShouldBe(1);
        BindError("from readmodel \"account-summary\" as a | join readmodel \"customer-profile\" as c on c.id = a.customerId | project id").Code.ShouldBe(QueryCodes.Ambiguous);
        BindError("from readmodel \"account-summary\" as a | join readmodel \"customer-profile\" as a on a.id = a.customerId").Code.ShouldBe(QueryCodes.Ambiguous);
    }

    [Fact]
    [Trait("Id", "UT-QL-88")]
    public void Subqueries_BindWithCorrelationByEqualityOnly()
    {
        Bound("""
            SELECT a.id FROM readmodel."account-summary" a
            WHERE a.customerId IN (SELECT c.id FROM readmodel."customer-profile" c WHERE c.segment = 'retail')
              AND EXISTS (SELECT p.id FROM readmodel."customer-profile" p WHERE p.id = a.customerId)
            """, QueryLanguage.Sql);
        BindError("""
            SELECT a.id FROM readmodel."account-summary" a
            WHERE EXISTS (SELECT p.id FROM readmodel."customer-profile" p WHERE p.segment > a.status)
            """, QueryLanguage.Sql).Code.ShouldBe(QueryCodes.Unsupported);
    }

    [Fact]
    [Trait("Id", "UT-QL-32")]
    public void Options_AreTyped_UnknownRejected()
    {
        BoundQuery query = Bound("from readmodel \"account-summary\" options (consistency = at_least(@t), timeout = 2s, on_timeout = 'stale')");

        query.Options.Consistency.ShouldBe(QueryConsistency.AtLeast);
        query.Options.StaleOnTimeout.ShouldBeTrue();
        query.Parameters["t"].ShouldBe(QueryType.String);
        BindError("from readmodel \"account-summary\" options (frobnicate = 1)").Code.ShouldBe(QueryCodes.UnknownName);
    }

    [Fact]
    [Trait("Id", "UT-QL-33")]
    public void Catalog_IsBuiltFromTheRuntimeModel_AndOnlyHoldsThisServicesTypes()
    {
        var builder = new SourceFlowModelBuilder();
        CoreTestModule.Register(builder);
        QueryCatalog catalog = QueryCatalog.Build(builder.Build());

        QueryEventType incremented = catalog.EventTypes.Single(e => e.Name == "test.counter.incremented");
        incremented.Latest.Fields.Select(f => (f.Name, f.Type)).ShouldBe([("counterId", QueryType.String), ("count", QueryType.Integer)]);
        catalog.TryGetAggregate("test-counter", out QueryAggregate? counter).ShouldBeTrue();
        counter!.Fields.ShouldContain(f => f.Name == "count" && f.Type == QueryType.Integer);
        catalog.EventTypes.ShouldNotContain(e => e.Name == "test.counter.increment");   // commands are not in the event log
        catalog.Fingerprint.Length.ShouldBe(64);
    }

    // ---------------------------------------------------------------- policy

    [Fact]
    [Trait("Id", "UT-QL-34")]
    [Trait("Covers", "ADR-026")]
    public void CostGuard_UnboundedEventScans_AreSfq0401()
    {
        Policy("from events").ShouldHaveSingleItem().Code.ShouldBe(QueryCodes.UnboundedScan);
        Policy("from events of \"bank.account.deposited\"").ShouldHaveSingleItem().Code.ShouldBe(QueryCodes.UnboundedScan);
        Policy("from events of \"bank.account.deposited\" | where time >= ago(90d)").ShouldHaveSingleItem().Code.ShouldBe(QueryCodes.UnboundedScan);
        Policy("SELECT * FROM events", language: QueryLanguage.Sql).ShouldHaveSingleItem().Code.ShouldBe(QueryCodes.UnboundedScan);
    }

    [Theory]
    [Trait("Id", "UT-QL-35")]
    [InlineData("from events of \"bank.account.deposited\" | where time >= ago(7d)")]
    [InlineData("from events | where stream = @s")]
    [InlineData("from events | where sfcorrelationid = @cid | order by position")]
    [InlineData("from events | where id in (@a, @b)")]
    [InlineData("from events | where has_tag('account:1')")]
    [InlineData("from events | where position between @p1 and @p2")]
    [InlineData("from events of \"bank.account.deposited\" | where data.accountId = @id")]
    [InlineData("from stream @s")]
    [InlineData("from readmodel \"account-summary\"")]
    public void CostGuard_BoundedQueries_Pass(string text)
    {
        Policy(text).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Id", "UT-QL-36")]
    public void CostGuard_TimeRangesFromParameterValues()
    {
        string text = "from events of \"bank.account.deposited\" | where time between @from and @to";
        var month = new Dictionary<string, object?> { ["from"] = "2026-01-01T00:00:00Z", ["to"] = "2026-01-31T00:00:00Z" };
        var year = new Dictionary<string, object?> { ["from"] = "2025-01-01T00:00:00Z", ["to"] = "2026-01-01T00:00:00Z" };

        Policy(text, parameters: month).ShouldBeEmpty();
        Policy(text, parameters: year).ShouldHaveSingleItem().Code.ShouldBe(QueryCodes.UnboundedScan);
        Policy(text).ShouldBeEmpty();   // validation without values: both bounds present
    }

    [Fact]
    [Trait("Id", "UT-QL-37")]
    public void CostGuard_NonQueryableFilterWithoutBound_IsSfq0402_AndAllowScanNeedsPermission()
    {
        Policy("from events of \"bank.account.deposited\" | where data.amount > 5").ShouldHaveSingleItem().Code.ShouldBe(QueryCodes.NotQueryable);
        Policy("from events options (allow_scan = true)").ShouldBeEmpty();
        Policy("from events options (allow_scan = true)", QueryPermissions.Of(QueryPermission.Events)).ShouldHaveSingleItem().Code.ShouldBe(QueryCodes.PermissionDenied);
    }

    [Fact]
    [Trait("Id", "UT-QL-38")]
    [Trait("Covers", "ADR-026")]
    public void PersonalData_CantFilterSortGroupOrJoin_ButCanBeProjected()
    {
        Policy("from readmodel \"account-summary\" | where holder = 'Ada'").ShouldHaveSingleItem().Code.ShouldBe(QueryCodes.PersonalData);
        Policy("from readmodel \"account-summary\" | order by holder").ShouldHaveSingleItem().Code.ShouldBe(QueryCodes.PersonalData);
        Policy("from readmodel \"account-summary\" | summarize n = count() by holder").ShouldHaveSingleItem().Code.ShouldBe(QueryCodes.PersonalData);
        Policy("from events of \"bank.account.opened\" | where data.givenName = 'Ada' and time >= ago(1d)").ShouldHaveSingleItem().Code.ShouldBe(QueryCodes.PersonalData);
        Policy("from readmodel \"account-summary\" as a | join readmodel \"customer-profile\" as c on c.fullName = a.holder").ShouldHaveSingleItem().Code.ShouldBe(QueryCodes.PersonalData);
        Policy("from readmodel \"account-summary\" | project id, holder").ShouldBeEmpty();
        Policy("from readmodel \"account-summary\" | summarize n = count(holder)").ShouldBeEmpty();
        Bound("from readmodel \"account-summary\" | project id, holder").Outputs[1].PersonalData.ShouldBeTrue();
    }

    [Fact]
    [Trait("Id", "UT-QL-39")]
    public void Permissions_PerSourceKind()
    {
        QueryPermissions readModelsOnly = QueryPermissions.Of(QueryPermission.ReadModels);

        Policy("from readmodel \"account-summary\"", readModelsOnly).ShouldBeEmpty();
        QueryDiagnostic denied = Policy("from stream @s", readModelsOnly).ShouldHaveSingleItem();
        denied.Code.ShouldBe(QueryCodes.PermissionDenied);
        denied.Message.ShouldContain(QueryPermission.Events);
        Policy("from readmodel \"account-summary\" options (tenants = 'all')", readModelsOnly).ShouldHaveSingleItem().Code.ShouldBe(QueryCodes.PermissionDenied);
    }

    [Fact]
    [Trait("Id", "UT-QL-80")]
    [Trait("Covers", "ADR-026")]
    public void Joins_IndexedKeysAndBoundedEventJoins()
    {
        Policy("from readmodel \"account-summary\" as a | join readmodel \"customer-profile\" as c on c.segment = a.status")
            .ShouldHaveSingleItem().Code.ShouldBe(QueryCodes.JoinNotIndexed);
        Policy("from readmodel \"account-summary\" as a | join readmodel \"customer-profile\" as c on c.id = a.customerId").ShouldBeEmpty();
        Policy("""
            from events of "bank.account.deposited" as e | where e.data.amount >= @min and e.time >= ago(7d)
            | join kind=left readmodel "account-summary" as s on s.id = e.aggregatekey
            | project stream = e.stream, amount = e.data.amount, status = s.status
            """).ShouldBeEmpty();
        Policy("""
            from events of "bank.account.deposited" as d | where d.time >= ago(1d)
            | join events of "bank.account.withdrawn" as w on w.stream = d.stream
            """).ShouldHaveSingleItem().Code.ShouldBe(QueryCodes.UnboundedEventJoin);
        Policy("from readmodel \"account-summary\" as a | join events as e on e.aggregatekey = a.id").ShouldBeEmpty();
    }

    [Fact]
    [Trait("Id", "UT-QL-89")]
    public void Offset_IsReadModelOnly_AndLimitsAreCapped()
    {
        Policy("from readmodel \"account-summary\" | skip 10 | take 10").ShouldBeEmpty();
        Policy("from stream @s | skip 10").ShouldHaveSingleItem().Code.ShouldBe(QueryCodes.Offset);
        Policy("from readmodel \"account-summary\" | skip 20000").ShouldHaveSingleItem().Code.ShouldBe(QueryCodes.Offset);
        Policy("from readmodel \"account-summary\" | take 5000").ShouldHaveSingleItem().Code.ShouldBe(QueryCodes.Limit);
        Policy("from readmodel \"account-summary\" | take @n", parameters: new Dictionary<string, object?> { ["n"] = 50_000 }).ShouldHaveSingleItem().Code.ShouldBe(QueryCodes.Limit);
    }
}

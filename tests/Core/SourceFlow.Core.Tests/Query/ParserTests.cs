using SourceFlow.Query;

namespace SourceFlow.Core.Tests.Query;

/// <summary>UT-QL-01..20, 61..78: the SFQL front end (doc 14 §4, §9.2, §9.3).</summary>
[Trait("Category", "Unit")]
public sealed class ParserTests
{
    private static QueryAst Pipeline(string text)
    {
        ParseResult result = SfqlParser.Parse(text);
        result.Diagnostics.ShouldBeEmpty(string.Join("; ", result.Diagnostics));
        return result.Query!;
    }

    private static QueryAst Sql(string text)
    {
        ParseResult result = SfqlParser.Parse(text, QueryLanguage.Sql);
        result.Diagnostics.ShouldBeEmpty(string.Join("; ", result.Diagnostics));
        return result.Query!;
    }

    private static QueryDiagnostic Rejected(string text, QueryLanguage language = QueryLanguage.Sfql, QueryLimits? limits = null)
    {
        ParseResult result = SfqlParser.Parse(text, language, limits);
        result.Query.ShouldBeNull();
        return result.Diagnostics.ShouldHaveSingleItem();
    }

    // ---------------------------------------------------------------- pipeline productions

    [Fact]
    [Trait("Id", "UT-QL-01")]
    [Trait("Covers", "ADR-023")]
    public void ExampleA_Pipeline_ParsesIntoTheNormalisedTree()
    {
        QueryAst query = Pipeline("""
            from events of "bank.account.deposited"
            | where data.amount >= @min and time >= ago(7d)
            | project stream, time, amount = data.amount
            | order by time desc
            | take 100
            """);

        query.ToString().ShouldBe(
            "from events of 'bank.account.deposited' where ((data.amount >= @min) and (time >= ago(duration(7.00:00:00)))) "
            + "select stream, time, data.amount as amount order by time desc limit 100");
        query.Language.ShouldBe(QueryLanguage.Sfql);
    }

    [Fact]
    [Trait("Id", "UT-QL-02")]
    public void EverySource_Parses()
    {
        Pipeline("from events").Source.ShouldBeOfType<EventsSource>().Types.ShouldBeEmpty();
        EventsSource narrowed = Pipeline("from events of \"a.b\", 'c.d' version 2 upcast as e").Source.ShouldBeOfType<EventsSource>();
        narrowed.Types.Select(t => t.Value).ShouldBe(["a.b", "c.d"]);
        narrowed.Upcast.ShouldBeTrue();
        narrowed.Alias.ShouldBe("e");
        Pipeline("from stream @s").Source.ShouldBeOfType<StreamSource>();
        Pipeline("from readmodel \"account-summary\"").Source.ShouldBeOfType<ReadModelSource>().Collection.Value.ShouldBe("account-summary");
        AggregateSource aggregate = Pipeline("from aggregate \"bank-account\" id @id as of time datetime('2026-01-31T23:59:59Z')").Source.ShouldBeOfType<AggregateSource>();
        aggregate.AsOf.ShouldBe(AsOfKind.Time);
        aggregate.Bound.ShouldBeOfType<LiteralExpr>().Value.ShouldBe(new DateTimeOffset(2026, 1, 31, 23, 59, 59, TimeSpan.Zero));
        Pipeline("from aggregate \"bank-account\" id @id as a").Source.Alias.ShouldBe("a");
        Pipeline("from schema of \"bank.account.opened\"").Source.ShouldBeOfType<SchemaSource>().Type!.Value.ShouldBe("bank.account.opened");
    }

    [Fact]
    [Trait("Id", "UT-QL-03")]
    public void Literals_Parameters_DurationsDecimalsAndTimestamps()
    {
        Expr Where(string expr) => Pipeline("from readmodel \"r\" | where " + expr).Where!;

        Where("x = 12.50m").ShouldBeOfType<BinaryExpr>().Right.ShouldBe(new LiteralExpr(LiteralKind.Decimal, 12.50m, default), new LiteralComparer());
        Where("x = 12.5").ShouldBeOfType<BinaryExpr>().Right.ShouldBeOfType<LiteralExpr>().Kind.ShouldBe(LiteralKind.Decimal);
        Where("x = -3").ShouldBeOfType<BinaryExpr>().Right.ShouldBeOfType<LiteralExpr>().Value.ShouldBe(-3L);
        Where("x = 30m").ShouldBeOfType<BinaryExpr>().Right.ShouldBeOfType<LiteralExpr>().Value.ShouldBe(TimeSpan.FromMinutes(30));
        Where("x = 250ms").ShouldBeOfType<BinaryExpr>().Right.ShouldBeOfType<LiteralExpr>().Value.ShouldBe(TimeSpan.FromMilliseconds(250));
        Where("x = 'it''s'").ShouldBeOfType<BinaryExpr>().Right.ShouldBeOfType<LiteralExpr>().Value.ShouldBe("it's");
        Where("x = \"double\"").ShouldBeOfType<BinaryExpr>().Right.ShouldBeOfType<LiteralExpr>().Value.ShouldBe("double");
        Where("x = true and y = null").ToString().ShouldNotBeEmpty();
        Where("x = @p").ShouldBeOfType<BinaryExpr>().Right.ShouldBeOfType<ParameterExpr>().Name.ShouldBe("p");
    }

    [Fact]
    [Trait("Id", "UT-QL-04")]
    public void Precedence_IsOrXorAndNotPredicateAdditiveMultiplicative()
    {
        AstPrinter.Expr(Pipeline("from readmodel \"r\" | where a = 1 or b = 2 and not c = 3").Where!)
            .ShouldBe("((a = 1) or ((b = 2) and (not (c = 3))))");
        AstPrinter.Expr(Pipeline("from readmodel \"r\" | where a xor b and c").Where!).ShouldBe("(a xor (b and c))");
        AstPrinter.Expr(Pipeline("from readmodel \"r\" | where x + 2 * 3 > -y % 4").Where!).ShouldBe("((x + (2 * 3)) > ((-y) % 4))");
        AstPrinter.Expr(Pipeline("from readmodel \"r\" | where (a or b) and c").Where!).ShouldBe("((a or b) and c)");
    }

    [Fact]
    [Trait("Id", "UT-QL-05")]
    public void Predicates_InBetweenLikeIsNullExists()
    {
        AstPrinter.Expr(Pipeline("from readmodel \"r\" | where s in ('a', 'b') and n not between 1 and 5 and t not like 'x%' and z is not null and exists data.k").Where!)
            .ShouldBe("(((((s in ('a', 'b')) and (n not between 1 and 5)) and (t not like 'x%')) and (z is not null)) and (exists data.k))");
    }

    [Fact]
    [Trait("Id", "UT-QL-06")]
    public void CaseCastFunctionsAndPaths()
    {
        QueryAst query = Pipeline("""
            from events of "o.placed" | project c = case when data.total > 100 then 'big' else 'small' end,
              n = cast(data.qty as int), sku = data.items[0].sku, l = lower(trim(data.name)), b = bin(time, 1d)
            """);

        query.Select!.Select(i => AstPrinter.Expr(i.Expression!)).ShouldBe(
        [
            "case when (data.total > 100) then 'big' else 'small' end",
            "cast(data.qty as integer)",
            "data.items[0].sku",
            "lower(trim(data.name))",
            "bin(time, duration(1.00:00:00))",
        ]);
    }

    [Fact]
    [Trait("Id", "UT-QL-07")]
    public void Summarize_GroupsAggregatesAndHavingSubstitution()
    {
        QueryAst query = Pipeline("""
            from events of "d", "w" | where time between @from and @to
            | summarize total = sum(data.amount), n = count() by type, day = bin(time, 1d)
            | where n > 5 | order by day
            """);

        query.GroupBy.Select(AstPrinter.Expr).ShouldBe(["type", "bin(time, duration(1.00:00:00))"]);
        query.Select!.Select(i => i.Alias).ShouldBe([null, "day", "total", "n"]);
        AstPrinter.Expr(query.Having!).ShouldBe("(count() > 5)");
        AstPrinter.Expr(query.OrderBy[0].Expression).ShouldBe("day");
    }

    [Fact]
    [Trait("Id", "UT-QL-08")]
    public void CountForms_AllNormaliseTheSameWay()
    {
        string Item(string text, QueryLanguage language) => AstPrinter.Expr(SfqlParser.Parse(text, language).Query!.Select![0].Expression!);

        Item("from readmodel \"r\" | summarize count_distinct(x)", QueryLanguage.Sfql).ShouldBe("count(distinct x)");
        Item("from readmodel \"r\" | summarize count(distinct x)", QueryLanguage.Sfql).ShouldBe("count(distinct x)");
        Item("SELECT COUNT(DISTINCT x) FROM readmodel.r", QueryLanguage.Sql).ShouldBe("count(distinct x)");
        Item("SELECT COUNT(*) FROM readmodel.r", QueryLanguage.Sql).ShouldBe("count()");
        Item("from readmodel \"r\" | summarize count()", QueryLanguage.Sfql).ShouldBe("count()");
    }

    [Fact]
    [Trait("Id", "UT-QL-09")]
    public void JoinsLookupDistinctOrderSkipTakeAndOptions()
    {
        QueryAst query = Pipeline("""
            from readmodel "account-summary" as a
            | lookup readmodel "customer-profile" on key = a.customerId as c
            | join kind=left events of "bank.account.deposited" as e on e.aggregatekey = a.id and e.stream = a.stream
            | project a.id, c.segment | distinct | order by a.id asc nulls last | skip 10 | take 5
            options (consistency = at_least(@token), timeout = 2s, on_timeout = 'stale', allow_scan = true)
            """);

        query.Joins.Count.ShouldBe(2);
        query.Joins[0].Kind.ShouldBe(JoinKind.Left);
        AstPrinter.Expr(query.Joins[0].On[0].Left).ShouldBe("c.id");
        AstPrinter.Expr(query.Joins[0].On[0].Right).ShouldBe("a.customerId");
        query.Joins[1].On.Count.ShouldBe(2);
        query.Distinct.ShouldBeTrue();
        query.OrderBy[0].Nulls.ShouldBe(NullsOrder.Last);
        query.Offset.ShouldBeOfType<LiteralExpr>().Value.ShouldBe(10L);
        query.Options.Select(o => o.Name).ShouldBe(["consistency", "timeout", "on_timeout", "allow_scan"]);
        AstPrinter.Expr(query.Options[0].Value).ShouldBe("at_least(@token)");
    }

    [Fact]
    [Trait("Id", "UT-QL-10")]
    public void LookupIsSugarForALeftJoinOnId()
    {
        Pipeline("from readmodel \"a\" as a | lookup readmodel \"c\" on key = a.cid as c").ToString()
            .ShouldBe(Pipeline("from readmodel \"a\" as a | join kind=left readmodel \"c\" as c on c.id = a.cid").ToString());
    }

    [Fact]
    [Trait("Id", "UT-QL-11")]
    public void QualifiersResolveToDeclaredAliasesOnly()
    {
        QueryAst query = Pipeline("from events of \"t\" as e | where e.data.amount > 1 and data.amount < 9");

        var where = (BinaryExpr)query.Where!;
        var qualified = (ColumnExpr)((BinaryExpr)where.Left).Left;
        qualified.Qualifier.ShouldBe("e");
        qualified.PathText.ShouldBe("data.amount");
        ((ColumnExpr)((BinaryExpr)where.Right).Left).Qualifier.ShouldBeNull();
    }

    [Fact]
    [Trait("Id", "UT-QL-12")]
    public void StagesOutOfOrder_AreSyntaxErrorsWithPositions()
    {
        QueryDiagnostic diagnostic = Rejected("from readmodel \"r\"\n| take 5\n| where x = 1");

        diagnostic.Code.ShouldBe(QueryCodes.Syntax);
        diagnostic.Line.ShouldBe(3);
        diagnostic.Column.ShouldBe(3);
        diagnostic.Message.ShouldContain("can't come after 'take'");
    }

    [Theory]
    [Trait("Id", "UT-QL-13")]
    [InlineData("from readmodel", 1, 15)]
    [InlineData("from readmodel \"r\" | where", 1, 27)]
    [InlineData("from readmodel \"r\" | where x = (1", 1, 34)]
    [InlineData("from readmodel \"r\" | frobnicate", 1, 22)]
    [InlineData("from readmodel \"r\" | where x = 1 extra", 1, 34)]
    public void SyntaxErrors_ReportTheirPosition(string text, int line, int column)
    {
        QueryDiagnostic diagnostic = Rejected(text);

        diagnostic.Code.ShouldBe(QueryCodes.Syntax);
        (diagnostic.Line, diagnostic.Column).ShouldBe((line, column));
    }

    // ---------------------------------------------------------------- SQL syntax and equivalence

    [Fact]
    [Trait("Id", "UT-QL-69")]
    [Trait("Covers", "ADR-026")]
    public void ExampleA_SqlAndPipeline_ProduceTheSameTree()
    {
        Sql("""
            SELECT stream, time, data.amount AS amount
            FROM events."bank.account.deposited"
            WHERE data.amount >= @min AND time >= ago(7d)
            ORDER BY time DESC
            LIMIT 100
            """).ToString().ShouldBe(Pipeline("""
            from events of "bank.account.deposited"
            | where data.amount >= @min and time >= ago(7d)
            | project stream, time, amount = data.amount
            | order by time desc
            | take 100
            """).ToString());
    }

    [Fact]
    [Trait("Id", "UT-QL-70")]
    [Trait("Covers", "ADR-026")]
    public void ExampleG_JoinGroupHaving_SqlAndPipeline_ProduceTheSameTree()
    {
        QueryAst sql = Sql("""
            SELECT c.segment, COUNT(*) AS accounts, SUM(a.balance) AS total
            FROM readmodel."account-summary" a
            JOIN readmodel."customer-profile" c ON c.id = a.customerId
            WHERE a.status = 'Open'
            GROUP BY c.segment
            HAVING SUM(a.balance) > @minTotal
            ORDER BY total DESC
            LIMIT 20
            """);
        QueryAst pipeline = Pipeline("""
            from readmodel "account-summary" as a
            | join readmodel "customer-profile" as c on c.id = a.customerId
            | where a.status = 'Open'
            | summarize accounts = count(), total = sum(a.balance) by c.segment
            | where total > @minTotal
            | order by total desc
            | take 20
            """);

        sql.ToString().ShouldBe(pipeline.ToString());
        sql.Language.ShouldBe(QueryLanguage.Sql);
    }

    [Fact]
    [Trait("Id", "UT-QL-71")]
    public void SqlProductions_StarAliasesJoinsSubqueriesNullsAndOffset()
    {
        QueryAst query = Sql("""
            SELECT DISTINCT a.*, a.balance b2, "quoted" FROM readmodel."account-summary" AS a
            LEFT OUTER JOIN stream(@s) s ON s.stream = a.stream
            WHERE a.id IN (SELECT x.accountId FROM readmodel.flags x WHERE x.on = true)
              AND NOT EXISTS (SELECT y.id FROM readmodel.blocked y WHERE y.id = a.id)
            ORDER BY a.balance DESC NULLS FIRST
            LIMIT @n OFFSET 20
            """);

        query.Distinct.ShouldBeTrue();
        query.Select![0].StarQualifier.ShouldBe("a");
        query.Select[1].Alias.ShouldBe("b2");
        query.Joins.ShouldHaveSingleItem().Kind.ShouldBe(JoinKind.Left);
        var where = (BinaryExpr)query.Where!;
        where.Left.ShouldBeOfType<InSubqueryExpr>().Subquery.Source.Alias.ShouldBe("x");
        where.Right.ShouldBeOfType<ExistsSubqueryExpr>().Negated.ShouldBeTrue();
        query.OrderBy[0].Nulls.ShouldBe(NullsOrder.First);
        query.Offset.ShouldBeOfType<LiteralExpr>().Value.ShouldBe(20L);
    }

    [Fact]
    [Trait("Id", "UT-QL-72")]
    public void SqlQuotes_DoubleIsIdentifier_SingleIsString()
    {
        QueryAst query = Sql("SELECT \"select\" FROM readmodel.\"a-b\" WHERE \"from\" = 'x'");

        query.Select![0].Expression.ShouldBeOfType<ColumnExpr>().Head.ShouldBe("select");
        query.Source.ShouldBeOfType<ReadModelSource>().Collection.Value.ShouldBe("a-b");
        ((BinaryExpr)query.Where!).Right.ShouldBeOfType<LiteralExpr>().Value.ShouldBe("x");
    }

    // ---------------------------------------------------------------- doc 14 Example I and the allow-list (I-1, I-2)

    [Theory]
    [Trait("Id", "UT-QL-73")]
    [Trait("Covers", "ADR-026")]
    [InlineData("SELECT * FROM readmodel.\"account-summary\"; DROP TABLE x", "Statement separators")]
    [InlineData("SELECT * FROM readmodel.\"account-summary\" WHERE status = 'Open' -- ' OR 1=1", "Comments")]
    [InlineData("SELECT * FROM readmodel.r /* hidden */", "Comments")]
    [InlineData("UPDATE readmodel.\"account-summary\" SET balance = 0", "'UPDATE'")]
    [InlineData("DELETE FROM readmodel.r", "'DELETE'")]
    [InlineData("INSERT INTO readmodel.r VALUES (1)", "'INSERT'")]
    [InlineData("DROP TABLE events", "'DROP'")]
    [InlineData("EXEC sp_who", "'EXEC'")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x", "WITH")]
    [InlineData("SELECT a FROM readmodel.r UNION SELECT b FROM readmodel.s", "UNION")]
    [InlineData("SELECT a INTO t FROM readmodel.r", "INTO")]
    [InlineData("SELECT * FROM readmodel.r RIGHT JOIN readmodel.s ON s.id = r.id", "RIGHT joins")]
    [InlineData("SELECT * FROM readmodel.r CROSS JOIN readmodel.s", "CROSS joins")]
    [InlineData("SELECT row_number() OVER () FROM readmodel.r", "allow-list")]
    [InlineData("SELECT count(*) OVER () FROM readmodel.r", "Window functions")]
    [InlineData("SELECT pg_sleep(10) FROM readmodel.r", "allow-list")]
    [InlineData("SELECT (SELECT 1) FROM readmodel.r", "Scalar subqueries")]
    [InlineData("SELECT * FROM (SELECT * FROM readmodel.r) x", "Derived tables")]
    [InlineData("SELECT * FROM readmodel.r JOIN readmodel.s ON s.id > r.id", "equalities between columns")]
    public void SqlConstructsOutsideTheReadOnlyGrammar_AreSfq0102(string text, string named)
    {
        QueryDiagnostic diagnostic = Rejected(text, QueryLanguage.Sql);

        diagnostic.Code.ShouldBe(QueryCodes.Unsupported);
        diagnostic.Message.ShouldContain(named);
    }

    [Theory]
    [Trait("Id", "UT-QL-74")]
    [InlineData("from readmodel \"r\" | where x in (select a from readmodel.s)")]
    [InlineData("delete from readmodel \"r\"")]
    [InlineData("from readmodel \"r\" | where x = 1; drop table events")]
    public void PipelineConstructsOutsideTheGrammar_AreSfq0102(string text)
    {
        Rejected(text).Code.ShouldBe(QueryCodes.Unsupported);
    }

    [Theory]
    [Trait("Id", "UT-QL-75")]
    [Trait("Covers", "ADR-026")]
    [InlineData("SELECT usename FROM pg_catalog.pg_user")]
    [InlineData("SELECT * FROM information_schema.tables")]
    [InlineData("SELECT * FROM outbox")]
    [InlineData("SELECT * FROM sourceflow.data_keys")]
    public void SourcesOutsideTheCatalog_AreUnreachable(string text)
    {
        Rejected(text, QueryLanguage.Sql).Code.ShouldBe(QueryCodes.UnknownName);
    }

    [Fact]
    [Trait("Id", "UT-QL-76")]
    [Trait("Covers", "ADR-026")]
    public void Diagnostics_NeverEchoTheQueryText()
    {
        string secret = "TOP-SECRET-" + new string('x', 20);
        ParseResult result = SfqlParser.Parse($"SELECT * FROM readmodel.r WHERE a = '{secret}' -- trailing", QueryLanguage.Sql);
        ParseResult unknown = SfqlParser.Parse($"SELECT {secret}_fn(1) FROM readmodel.r", QueryLanguage.Sql);

        result.Diagnostics.ShouldHaveSingleItem().Message.ShouldNotContain(secret);
        unknown.Diagnostics.ShouldHaveSingleItem().Message.Length.ShouldBeLessThan(200);
    }

    [Fact]
    [Trait("Id", "UT-QL-77")]
    public void Limits_JoinsSubqueriesInListsParametersAndDepth()
    {
        var limits = new QueryLimits { MaxJoins = 1, MaxInItems = 2, MaxParameters = 2, MaxDepth = 4, MaxSubqueries = 1 };

        Rejected("SELECT * FROM readmodel.a a JOIN readmodel.b b ON b.id = a.id JOIN readmodel.c c ON c.id = a.id", QueryLanguage.Sql, limits).Code.ShouldBe(QueryCodes.TooManyJoins);
        Rejected("from readmodel \"r\" | where x in (1, 2, 3)", limits: limits).Code.ShouldBe(QueryCodes.Limit);
        Rejected("from readmodel \"r\" | where x = @a or x = @b or x = @c", limits: limits).Code.ShouldBe(QueryCodes.Limit);
        Rejected("from readmodel \"r\" | where ((((((x = 1))))))", limits: limits).Code.ShouldBe(QueryCodes.Limit);
        Rejected("SELECT * FROM readmodel.r WHERE a IN (SELECT b FROM readmodel.s) AND c IN (SELECT d FROM readmodel.t)", QueryLanguage.Sql, limits).Code
            .ShouldBe(QueryCodes.TooManyJoins);
        Rejected("SELECT * FROM readmodel.r WHERE a IN (SELECT b FROM readmodel.s WHERE c IN (SELECT d FROM readmodel.t))", QueryLanguage.Sql).Code
            .ShouldBe(QueryCodes.TooManyJoins);
    }

    // ---------------------------------------------------------------- request guard (§9.2)

    [Theory]
    [Trait("Id", "UT-QL-61")]
    [Trait("Covers", "ADR-026")]
    [InlineData("from readmodel \"r\" | where x = 1\u0000")]
    [InlineData("from readmodel \"r\" | where x = '‮abc'")]
    [InlineData("from readmodel \"r\" | where x = '⁦abc'")]
    [InlineData("from readmodel \"r\" | where x = 1\u0007")]
    [InlineData("from​ readmodel \"r\"")]
    [InlineData("SЕLECT * FROM readmodel.r")]
    public void ForbiddenCharacters_AreSfq0104(string text)
    {
        Rejected(text, text.StartsWith('S') ? QueryLanguage.Sql : QueryLanguage.Sfql).Code.ShouldBe(QueryCodes.ForbiddenCharacters);
    }

    [Fact]
    [Trait("Id", "UT-QL-61")]
    public void LoneSurrogate_IsInvalidText()
    {
        string text = "from readmodel \"r\" | where x = '" + (char)0xD800 + "'";

        Rejected(text).Code.ShouldBe(QueryCodes.ForbiddenCharacters);
    }

    [Fact]
    [Trait("Id", "UT-QL-62")]
    public void ZeroWidthInsideAStringLiteral_IsData()
    {
        ((BinaryExpr)Pipeline("from readmodel \"r\" | where x = 'a​b'").Where!).Right.ShouldBeOfType<LiteralExpr>().Value.ShouldBe("a​b");
    }

    [Fact]
    [Trait("Id", "UT-QL-63")]
    public void Text_IsNfcNormalised()
    {
        string decomposed = "from readmodel \"r\" | where name = 'Zoë'";

        ((BinaryExpr)Pipeline(decomposed).Where!).Right.ShouldBeOfType<LiteralExpr>().Value.ShouldBe("Zoë");
    }

    [Fact]
    [Trait("Id", "UT-QL-64")]
    public void TextAndTokenLimits_AreSfq0103()
    {
        Rejected(new string(' ', 70_000) + "from readmodel \"r\"").Code.ShouldBe(QueryCodes.Limit);
        Rejected("from readmodel \"r\" | where a = 1 and b = 2", limits: new QueryLimits { MaxTokens = 5 }).Code.ShouldBe(QueryCodes.Limit);
    }

    [Fact]
    [Trait("Id", "UT-QL-65")]
    public void WrongSyntaxForTheLanguage_GivesAHint()
    {
        Rejected("SELECT * FROM readmodel.r").Message.ShouldContain("language 'sql'");
        Rejected("from readmodel \"r\"", QueryLanguage.Sql).Message.ShouldContain("language 'sfql'");
    }

    private sealed class LiteralComparer : IEqualityComparer<Expr>
    {
        public bool Equals(Expr? x, Expr? y)
        {
            return x is LiteralExpr a && y is LiteralExpr b && a.Kind == b.Kind && Equals(a.Value, b.Value);
        }

        public int GetHashCode(Expr obj)
        {
            return 0;
        }
    }
}

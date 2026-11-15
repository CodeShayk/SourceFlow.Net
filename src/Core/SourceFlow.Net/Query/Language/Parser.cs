using System.Globalization;

namespace SourceFlow.Query;

/// <summary>
/// Parses SFQL in either syntax (doc 14 §4) into the normalised <see cref="QueryAst"/>. The grammar is an allow-list:
/// anything that writes, changes schema, separates statements or carries comments has no production (rules I-1, I-2).
/// The request guard runs first (§9.2). Parsing stops at the first error, reported with its position.
/// </summary>
public static class SfqlParser
{
    /// <summary>Parses a query.</summary>
    /// <param name="text">The query text.</param>
    /// <param name="language">The syntax.</param>
    /// <param name="limits">Request limits; <see cref="QueryLimits.Default"/> when null.</param>
    public static ParseResult Parse(string text, QueryLanguage language = QueryLanguage.Sfql, QueryLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        limits = (limits ?? QueryLimits.Default).Capped();
        var diagnostics = new DiagnosticBag(text);
        string? guarded = RequestGuard.Check(text, limits, diagnostics);
        if (guarded is null)
        {
            return new ParseResult(null, diagnostics.Items);
        }

        List<Token>? tokens = new Lexer(guarded, language, limits, diagnostics).Tokenize();
        if (tokens is null)
        {
            return new ParseResult(null, diagnostics.Items);
        }

        try
        {
            QueryAst query = new QueryParser(tokens, language, limits, diagnostics).Parse();
            return new ParseResult(query, diagnostics.Items);
        }
        catch (QueryParser.ParseFailedException)
        {
            return new ParseResult(null, diagnostics.Items);
        }
    }
}

internal sealed class QueryParser(List<Token> tokens, QueryLanguage language, QueryLimits limits, DiagnosticBag diagnostics)
{
    private static readonly HashSet<string> Scalars = new(StringComparer.Ordinal)
    {
        "now", "ago", "bin", "coalesce", "lower", "upper", "trim", "length", "concat", "concat_ws", "substring", "left", "right",
        "abs", "round", "floor", "ceiling", "has_tag", "int", "bool", "string",
    };

    private static readonly HashSet<string> Aggregates = new(StringComparer.Ordinal) { "count", "count_distinct", "sum", "avg", "min", "max" };

    private static readonly HashSet<string> CastTypes = new(StringComparer.Ordinal) { "string", "integer", "int", "decimal", "boolean", "bool", "timestamp", "uuid" };

    private static readonly HashSet<string> Statements = new(StringComparer.OrdinalIgnoreCase)
    {
        "insert", "update", "delete", "merge", "create", "drop", "alter", "truncate", "exec", "execute", "call", "set", "grant",
        "revoke", "begin", "commit", "rollback", "copy", "declare", "values", "do", "replace", "upsert", "vacuum", "analyze", "lock",
    };

    // Words that end a select item or table alias in SQL (so they are never taken as an implicit alias).
    private static readonly HashSet<string> SqlClauseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "from", "where", "group", "having", "order", "limit", "offset", "join", "inner", "left", "right", "full", "cross", "natural",
        "on", "union", "intersect", "except", "into", "options", "as", "outer", "and", "or", "fetch", "window", "for",
    };

    private readonly HashSet<string> _parameters = new(StringComparer.Ordinal);
    private int _index;
    private int _depth;
    private int _subqueries;
    private int _subqueryDepth;

    private Token Current => tokens[_index];

    public QueryAst Parse()
    {
        foreach (Token token in tokens)
        {
            if (token.Kind == TokenKind.Comment)
            {
                Fail(QueryCodes.Unsupported, "Comments are not supported in queries.", token.Span);
            }

            if (token.IsSymbol(";"))
            {
                Fail(QueryCodes.Unsupported, "Statement separators (;) are not supported: send exactly one query.", token.Span);
            }
        }

        QueryAst query = language == QueryLanguage.Sql ? ParseSql() : ParsePipeline();
        if (Current.Kind != TokenKind.EndOfInput)
        {
            if (Current.Is("union") || Current.Is("intersect") || Current.Is("except"))
            {
                Fail(QueryCodes.Unsupported, $"{Current.Text.ToUpperInvariant()} is not supported; run separate queries.", Current.Span);
            }

            Fail(QueryCodes.Syntax, $"Unexpected {Describe(Current)} after the end of the query.", Current.Span);
        }

        return Qualify(query, []);
    }

    // ================================================================ pipeline syntax

    private QueryAst ParsePipeline()
    {
        Token start = Current;
        if (start.Kind == TokenKind.Identifier && Statements.Contains(start.Text))
        {
            Fail(QueryCodes.Unsupported, $"Statement type '{start.Text.ToUpperInvariant()}' is not supported: the query API is read-only.", start.Span);
        }

        if (start.Is("select"))
        {
            Fail(QueryCodes.Syntax, "This is SQL syntax: send it with language 'sql', or start a pipeline query with 'from'.", start.Span);
        }

        ExpectKeyword("from", "A pipeline query starts with 'from'.");
        SourceRef source = ParsePipelineSource(allowAlias: true);

        var joins = new List<JoinClause>();
        var where = new List<Expr>();
        var having = new List<Expr>();
        IReadOnlyList<SelectItem>? select = null;
        bool distinct = false;
        List<SelectItem>? summarizeItems = null;
        var groupBy = new List<Expr>();
        var orderBy = new List<SortItem>();
        Expr? limit = null;
        Expr? offset = null;
        int phase = 1;
        string last = "from";

        while (AcceptSymbol("|"))
        {
            Token stage = Current;
            string name = stage.Kind == TokenKind.Identifier ? stage.Text.ToLowerInvariant() : string.Empty;
            switch (name)
            {
                case "where":
                    _index++;
                    if (phase <= 1)
                    {
                        where.Add(ParseExpr());
                    }
                    else if (phase == 2 || phase == 3)
                    {
                        having.Add(ParseExpr());
                        phase = 3;
                    }
                    else
                    {
                        OutOfOrder(stage, last);
                    }

                    break;
                case "join":
                case "lookup":
                    if (phase > 1)
                    {
                        OutOfOrder(stage, last);
                    }

                    _index++;
                    joins.Add(name == "join" ? ParsePipelineJoin(stage) : ParseLookup(stage));
                    break;
                case "summarize":
                    if (phase > 1)
                    {
                        OutOfOrder(stage, last);
                    }

                    _index++;
                    (summarizeItems, groupBy) = ParseSummarize();
                    phase = 2;
                    break;
                case "project":
                    if (phase > 3 || select is not null)
                    {
                        OutOfOrder(stage, last);
                    }

                    _index++;
                    select = ParseItems(allowStar: false);
                    phase = 4;
                    break;
                case "distinct":
                    if (phase > 4 || distinct)
                    {
                        OutOfOrder(stage, last);
                    }

                    _index++;
                    distinct = true;
                    if (select is null && !AtStageEnd())
                    {
                        select = ParseItems(allowStar: false);
                    }

                    phase = 4;
                    break;
                case "order":
                    if (phase > 4)
                    {
                        OutOfOrder(stage, last);
                    }

                    _index++;
                    ExpectKeyword("by", "Expected 'by' after 'order'.");
                    orderBy.AddRange(ParseSorts());
                    phase = 5;
                    break;
                case "skip":
                    if (phase > 5)
                    {
                        OutOfOrder(stage, last);
                    }

                    _index++;
                    offset = ParseValue("skip");
                    phase = 6;
                    break;
                case "take":
                    if (phase > 6)
                    {
                        OutOfOrder(stage, last);
                    }

                    _index++;
                    limit = ParseValue("take");
                    phase = 7;
                    break;
                default:
                    Fail(stage.Kind == TokenKind.Identifier && Statements.Contains(stage.Text) ? QueryCodes.Unsupported : QueryCodes.Syntax,
                        "Expected a stage: where, join, lookup, summarize, project, distinct, order by, skip or take.", stage.Span);
                    break;
            }

            last = name;
        }

        IReadOnlyList<QueryOption> options = Current.Is("options") ? ParseOptions() : [];

        // Normalise summarize (doc 14 G-1, G-2): names it defines are replaced by their expressions in a following
        // where (HAVING) or project, so the tree equals the SQL form.
        Expr? havingExpr = Combine(having);
        if (summarizeItems is not null)
        {
            var named = new Dictionary<string, Expr>(StringComparer.Ordinal);
            foreach (SelectItem item in summarizeItems)
            {
                named[item.Alias ?? OutputName(item.Expression!)] = item.Expression!;
            }

            havingExpr = havingExpr is null ? null : Substitute(havingExpr, named);
            select = select is null
                ? summarizeItems
                : [.. select.Select(item => SubstituteItem(item, named))];
        }

        return new QueryAst(source, joins, Combine(where), select, distinct, groupBy, havingExpr, orderBy, limit, offset, options,
            QueryLanguage.Sfql, start.Span.Cover(tokens[Math.Max(0, _index - 1)].Span));
    }

    private void OutOfOrder(Token stage, string after)
    {
        Fail(QueryCodes.Syntax,
            $"Stage '{stage.Text.ToLowerInvariant()}' can't come after '{after}'. Stages go in the order: join and where, summarize, where, project, distinct, order by, skip, take.",
            stage.Span);
    }

    private bool AtStageEnd()
    {
        return Current.IsSymbol("|") || Current.Kind == TokenKind.EndOfInput || Current.Is("options");
    }

    private SourceRef ParsePipelineSource(bool allowAlias)
    {
        Token start = Current;
        SourceRef source;
        if (AcceptKeyword("events"))
        {
            var types = new List<LiteralExpr>();
            Expr? version = null;
            if (AcceptKeyword("of"))
            {
                do
                {
                    types.Add(ExpectString("Expected an event type name in quotes."));
                }
                while (AcceptSymbol(","));

                if (AcceptKeyword("version"))
                {
                    version = ParseValue("version");
                }
            }

            bool upcast = AcceptKeyword("upcast");
            source = new EventsSource(types, version, upcast, null, start.Span.Cover(Previous.Span));
        }
        else if (AcceptKeyword("stream"))
        {
            source = new StreamSource(ParseValue("stream"), null, start.Span.Cover(Previous.Span));
        }
        else if (AcceptKeyword("readmodel"))
        {
            source = new ReadModelSource(ExpectString("Expected a read-model collection name in quotes."), null, start.Span.Cover(Previous.Span));
        }
        else if (AcceptKeyword("aggregate"))
        {
            LiteralExpr name = ExpectString("Expected an aggregate name in quotes.");
            ExpectKeyword("id", "Expected 'id' after the aggregate name.");
            Expr id = ParseValue("id");
            AsOfKind kind = AsOfKind.Latest;
            Expr? bound = null;
            if (Current.Is("as") && tokens[_index + 1].Is("of"))
            {
                _index += 2;
                if (AcceptKeyword("version"))
                {
                    kind = AsOfKind.Version;
                }
                else if (AcceptKeyword("time"))
                {
                    kind = AsOfKind.Time;
                }
                else
                {
                    Fail(QueryCodes.Syntax, "Expected 'version' or 'time' after 'as of'.", Current.Span);
                }

                bound = ParseExpr();
            }

            source = new AggregateSource(name, id, kind, bound, null, start.Span.Cover(Previous.Span));
        }
        else if (AcceptKeyword("schema"))
        {
            LiteralExpr? type = AcceptKeyword("of") ? ExpectString("Expected a type name in quotes.") : null;
            source = new SchemaSource(type, null, start.Span.Cover(Previous.Span));
        }
        else
        {
            Fail(QueryCodes.UnknownName, "Unknown source; this service's sources are events, stream, readmodel, aggregate and schema (I-4).", Current.Span);
            return null!;
        }

        if (allowAlias && AcceptKeyword("as"))
        {
            source = source with { Alias = ExpectIdentifier("Expected an alias after 'as'.") };
        }

        return source;
    }

    private JoinClause ParsePipelineJoin(Token start)
    {
        JoinKind kind = JoinKind.Inner;
        if (AcceptKeyword("kind"))
        {
            ExpectSymbol("=", "Expected '=' after 'kind'.");
            if (AcceptKeyword("left"))
            {
                kind = JoinKind.Left;
            }
            else if (!AcceptKeyword("inner"))
            {
                Fail(QueryCodes.Unsupported, "Only inner and left joins are supported; rewrite a right join as a left join.", Current.Span);
            }
        }

        if (!(Current.Is("readmodel") || Current.Is("events") || Current.Is("stream")))
        {
            Fail(QueryCodes.Syntax, "A join source is readmodel, events or stream.", Current.Span);
        }

        SourceRef source = ParsePipelineSource(allowAlias: false);
        ExpectKeyword("as", "A joined source needs an alias: 'as <name>'.");
        source = source with { Alias = ExpectIdentifier("Expected an alias after 'as'.") };
        ExpectKeyword("on", "Expected 'on' and the join condition.");
        IReadOnlyList<JoinEquality> on = ParseJoinCondition();
        return new JoinClause(kind, source, on, start.Span.Cover(Previous.Span));
    }

    private JoinClause ParseLookup(Token start)
    {
        Token readModel = Current;
        ExpectKeyword("readmodel", "A lookup reads a read model: 'lookup readmodel \"<collection>\" on key = <column> as <alias>'.");
        LiteralExpr collection = ExpectString("Expected a read-model collection name in quotes.");
        ExpectKeyword("on", "Expected 'on key = <column>'.");
        ExpectKeyword("key", "Expected 'key' after 'on'.");
        ExpectSymbol("=", "Expected '=' after 'key'.");
        Expr key = ParseAdditive();
        if (key is not ColumnExpr keyColumn)
        {
            Fail(QueryCodes.Syntax, "A lookup key is a column.", key.Span);
            return null!;
        }

        ExpectKeyword("as", "A lookup needs an alias: 'as <name>'.");
        string alias = ExpectIdentifier("Expected an alias after 'as'.");
        var source = new ReadModelSource(collection, alias, readModel.Span.Cover(collection.Span));

        // Sugar for: join kind=left readmodel "…" as alias on alias.id = key (G-4).
        var id = new ColumnExpr(alias, ["id"], collection.Span);
        return new JoinClause(JoinKind.Left, source, [new JoinEquality(id, keyColumn)], start.Span.Cover(Previous.Span));
    }

    private (List<SelectItem> Items, List<Expr> GroupBy) ParseSummarize()
    {
        var aggregates = new List<SelectItem>();
        do
        {
            Token start = Current;
            string? alias = null;
            if (Current.Kind == TokenKind.Identifier && tokens[_index + 1].IsSymbol("="))
            {
                alias = Current.Text;
                _index += 2;
            }

            Expr expr = ParseExpr();
            if (expr is not CallExpr call || !Aggregates.Contains(call.Name))
            {
                Fail(QueryCodes.Syntax, "summarize takes aggregates: count, count_distinct, sum, avg, min, max.", expr.Span);
            }

            aggregates.Add(new SelectItem(expr, alias, null, start.Span.Cover(expr.Span)));
        }
        while (AcceptSymbol(","));

        var keys = new List<SelectItem>();
        if (AcceptKeyword("by"))
        {
            keys.AddRange(ParseItems(allowStar: false));
            if (keys.Count > limits.MaxGroupingKeys)
            {
                Fail(QueryCodes.Limit, $"A query can group by at most {limits.MaxGroupingKeys} keys.", keys[^1].Span);
            }
        }

        // Output order: grouping keys, then aggregates (the SQL form names them in the same order).
        return ([.. keys, .. aggregates], [.. keys.Select(k => k.Expression!)]);
    }

    // ================================================================ SQL syntax

    private QueryAst ParseSql()
    {
        Token start = Current;
        if (start.Kind == TokenKind.Identifier && Statements.Contains(start.Text))
        {
            Fail(QueryCodes.Unsupported, $"Statement type '{start.Text.ToUpperInvariant()}' is not supported: the query API is read-only.", start.Span);
        }

        if (start.Is("with"))
        {
            Fail(QueryCodes.Unsupported, "Common table expressions (WITH) are not supported.", start.Span);
        }

        if (start.Is("from"))
        {
            Fail(QueryCodes.Syntax, "This is pipeline syntax: send it with language 'sfql', or start a SQL query with SELECT.", start.Span);
        }

        ExpectKeyword("select", "A SQL query starts with SELECT.");
        bool distinct = AcceptKeyword("distinct");
        if (Current.Is("all"))
        {
            _index++;
        }

        IReadOnlyList<SelectItem> select = ParseItems(allowStar: true);
        if (Current.Is("into"))
        {
            Fail(QueryCodes.Unsupported, "SELECT … INTO is not supported: the query API is read-only.", Current.Span);
        }

        ExpectKeyword("from", "Expected FROM.");
        SourceRef source = ParseTableRef();
        var joins = new List<JoinClause>();
        while (true)
        {
            Token joinStart = Current;
            JoinKind? kind = null;
            if (Current.Is("right") || Current.Is("full") || Current.Is("cross") || Current.Is("natural"))
            {
                Fail(QueryCodes.Unsupported, $"{Current.Text.ToUpperInvariant()} joins are not supported; use INNER or LEFT (rewrite RIGHT as LEFT).", Current.Span);
            }

            if (AcceptKeyword("inner"))
            {
                kind = JoinKind.Inner;
            }
            else if (AcceptKeyword("left"))
            {
                AcceptKeyword("outer");
                kind = JoinKind.Left;
            }

            if (!AcceptKeyword("join"))
            {
                if (kind is not null)
                {
                    Fail(QueryCodes.Syntax, "Expected JOIN.", Current.Span);
                }

                if (Current.IsSymbol(","))
                {
                    Fail(QueryCodes.Unsupported, "Comma joins are not supported; use JOIN … ON.", Current.Span);
                }

                break;
            }

            SourceRef joined = ParseTableRef();
            ExpectKeyword("on", "Expected ON and the join condition.");
            IReadOnlyList<JoinEquality> on = ParseJoinCondition();
            joins.Add(new JoinClause(kind ?? JoinKind.Inner, joined, on, joinStart.Span.Cover(Previous.Span)));
            if (joins.Count > limits.MaxJoins)
            {
                Fail(QueryCodes.TooManyJoins, $"A query can have at most {limits.MaxJoins} joins.", joins[^1].Span);
            }
        }

        Expr? where = AcceptKeyword("where") ? ParseExpr() : null;
        var groupBy = new List<Expr>();
        Expr? having = null;
        if (AcceptKeyword("group"))
        {
            ExpectKeyword("by", "Expected BY after GROUP.");
            do
            {
                groupBy.Add(ParseExpr());
            }
            while (AcceptSymbol(","));

            if (groupBy.Count > limits.MaxGroupingKeys)
            {
                Fail(QueryCodes.Limit, $"A query can group by at most {limits.MaxGroupingKeys} keys.", groupBy[^1].Span);
            }

            if (AcceptKeyword("having"))
            {
                having = ParseExpr();
            }
        }
        else if (Current.Is("having"))
        {
            Fail(QueryCodes.Syntax, "HAVING needs GROUP BY.", Current.Span);
        }

        var orderBy = new List<SortItem>();
        if (AcceptKeyword("order"))
        {
            ExpectKeyword("by", "Expected BY after ORDER.");
            orderBy.AddRange(ParseSorts());
        }

        Expr? limit = null;
        Expr? offset = null;
        if (AcceptKeyword("limit"))
        {
            limit = ParseValue("LIMIT");
            if (AcceptKeyword("offset"))
            {
                offset = ParseValue("OFFSET");
            }
        }
        else if (Current.Is("offset") || Current.Is("fetch"))
        {
            Fail(QueryCodes.Syntax, "Use LIMIT n [OFFSET m].", Current.Span);
        }

        if (joins.Count > limits.MaxJoins)
        {
            Fail(QueryCodes.TooManyJoins, $"A query can have at most {limits.MaxJoins} joins.", joins[^1].Span);
        }

        IReadOnlyList<QueryOption> options = Current.Is("options") ? ParseOptions() : [];
        return new QueryAst(source, joins, where, select, distinct, groupBy, having, orderBy, limit, offset, options,
            QueryLanguage.Sql, start.Span.Cover(Previous.Span));
    }

    private SourceRef ParseTableRef()
    {
        Token start = Current;
        SourceRef source;
        if (AcceptKeyword("readmodel"))
        {
            ExpectSymbol(".", "Expected '.' and the collection: readmodel.\"<collection>\".");
            source = new ReadModelSource(ExpectName("Expected a read-model collection name."), null, start.Span.Cover(Previous.Span));
        }
        else if (AcceptKeyword("events"))
        {
            var types = new List<LiteralExpr>();
            if (AcceptSymbol("."))
            {
                types.Add(ExpectName("Expected an event type name: events.\"<type>\"."));
            }

            source = new EventsSource(types, null, false, null, start.Span.Cover(Previous.Span));
        }
        else if (AcceptKeyword("stream"))
        {
            ExpectSymbol("(", "Expected '(' after stream.");
            Expr id = ParseValue("stream");
            ExpectSymbol(")", "Expected ')'.");
            source = new StreamSource(id, null, start.Span.Cover(Previous.Span));
        }
        else
        {
            if (Current.IsSymbol("("))
            {
                Fail(QueryCodes.Unsupported, "Derived tables (subqueries in FROM) are not supported.", Current.Span);
            }

            // pg_catalog, information_schema, sys.*, internal tables and other services are unreachable (I-4).
            int end = _index;
            while (tokens[end].Kind is TokenKind.Identifier or TokenKind.QuotedIdentifier || tokens[end].IsSymbol("."))
            {
                end++;
            }

            TextSpan span = start.Span.Cover(tokens[Math.Max(_index, end - 1)].Span);
            Fail(QueryCodes.UnknownName,
                "Unknown source; this service's sources are readmodel.\"<collection>\", events[.\"<type>\"] and stream(@id) (I-4).", span);
            return null!;
        }

        if (AcceptKeyword("as"))
        {
            source = source with { Alias = ExpectAlias() };
        }
        else if (Current.Kind is TokenKind.Identifier or TokenKind.QuotedIdentifier && !SqlClauseWords.Contains(Current.Text) && !Current.Is("options"))
        {
            source = source with { Alias = ExpectAlias() };
        }

        return source;
    }

    private QueryAst ParseSubquery(Token open)
    {
        _subqueries++;
        if (_subqueries > limits.MaxSubqueries || _subqueryDepth >= 1)
        {
            Fail(QueryCodes.TooManyJoins, $"Subqueries are limited to {limits.MaxSubqueries} per query and depth 1 (G-12).", Current.Span);
        }

        _subqueryDepth++;
        Token start = Current;
        ExpectKeyword("select", "Expected SELECT.");
        Token itemStart = Current;
        Expr expr = ParseExpr();
        var item = new SelectItem(expr, null, null, itemStart.Span.Cover(expr.Span));
        ExpectKeyword("from", "Expected FROM.");
        SourceRef source = ParseTableRef();
        Expr? where = AcceptKeyword("where") ? ParseExpr() : null;
        if (!Current.IsSymbol(")"))
        {
            Fail(QueryCodes.Unsupported, "Subqueries can't contain joins, grouping, ordering or limits (G-12).", Current.Span);
        }

        _subqueryDepth--;
        return new QueryAst(source, [], where, [item], false, [], null, [], null, null, [], QueryLanguage.Sql, start.Span.Cover(Previous.Span));
    }

    // ================================================================ shared clauses

    private List<SelectItem> ParseItems(bool allowStar)
    {
        var items = new List<SelectItem>();
        do
        {
            Token start = Current;
            if (allowStar && AcceptSymbol("*"))
            {
                items.Add(new SelectItem(null, null, string.Empty, start.Span));
                continue;
            }

            if (allowStar && Current.Kind is TokenKind.Identifier or TokenKind.QuotedIdentifier && tokens[_index + 1].IsSymbol(".") && tokens[_index + 2].IsSymbol("*"))
            {
                string qualifier = Current.Text;
                _index += 3;
                items.Add(new SelectItem(null, null, qualifier, start.Span.Cover(Previous.Span)));
                continue;
            }

            string? alias = null;
            if (language == QueryLanguage.Sfql && Current.Kind == TokenKind.Identifier && tokens[_index + 1].IsSymbol("="))
            {
                alias = Current.Text;
                _index += 2;
            }

            Expr expr = ParseExpr();
            if (language == QueryLanguage.Sql)
            {
                if (AcceptKeyword("as"))
                {
                    alias = ExpectAlias();
                }
                else if (Current.Kind is TokenKind.Identifier or TokenKind.QuotedIdentifier && !SqlClauseWords.Contains(Current.Text))
                {
                    alias = ExpectAlias();
                }
            }

            items.Add(new SelectItem(expr, alias, null, start.Span.Cover(Previous.Span)));
        }
        while (AcceptSymbol(","));

        if (items.Count > limits.MaxColumns)
        {
            Fail(QueryCodes.Limit, $"A query can select at most {limits.MaxColumns} columns.", items[^1].Span);
        }

        return items;
    }

    private List<SortItem> ParseSorts()
    {
        var sorts = new List<SortItem>();
        do
        {
            Token start = Current;
            Expr expr = ParseExpr();
            bool descending = false;
            if (AcceptKeyword("desc"))
            {
                descending = true;
            }
            else
            {
                AcceptKeyword("asc");
            }

            NullsOrder nulls = NullsOrder.Default;
            if (AcceptKeyword("nulls"))
            {
                if (AcceptKeyword("first"))
                {
                    nulls = NullsOrder.First;
                }
                else if (AcceptKeyword("last"))
                {
                    nulls = NullsOrder.Last;
                }
                else
                {
                    Fail(QueryCodes.Syntax, "Expected FIRST or LAST after NULLS.", Current.Span);
                }
            }

            sorts.Add(new SortItem(expr, descending, nulls, start.Span.Cover(Previous.Span)));
        }
        while (AcceptSymbol(","));

        return sorts;
    }

    private IReadOnlyList<JoinEquality> ParseJoinCondition()
    {
        var equalities = new List<JoinEquality>();
        do
        {
            Token start = Current;
            Expr left = ParseAdditive();
            if (!AcceptSymbol("="))
            {
                Fail(QueryCodes.Unsupported, "Join conditions are equalities between columns, joined with AND (G-7).", Current.Span);
            }

            Expr right = ParseAdditive();
            if (left is not ColumnExpr l || right is not ColumnExpr r)
            {
                Fail(QueryCodes.Unsupported, "Join conditions are equalities between columns, joined with AND (G-7).", start.Span.Cover(Previous.Span));
                return null!;
            }

            equalities.Add(new JoinEquality(l, r));
        }
        while (AcceptKeyword("and"));

        if (Current.Is("or"))
        {
            Fail(QueryCodes.Unsupported, "Join conditions are equalities between columns, joined with AND (G-7).", Current.Span);
        }

        return equalities;
    }

    private IReadOnlyList<QueryOption> ParseOptions()
    {
        Token start = Current;
        ExpectKeyword("options", "Expected options.");
        ExpectSymbol("(", "Expected '(' after options.");
        var options = new List<QueryOption>();
        do
        {
            Token nameToken = Current;
            string name = ExpectIdentifier("Expected an option name.").ToLowerInvariant();
            ExpectSymbol("=", "Expected '=' after the option name.");
            Expr value;
            if (Current.Kind == TokenKind.Identifier && tokens[_index + 1].IsSymbol("("))
            {
                Token fn = Current;
                _index += 2;
                var args = new List<Expr>();
                if (!Current.IsSymbol(")"))
                {
                    args.Add(ParseValue(fn.Text));
                }

                ExpectSymbol(")", "Expected ')'.");
                value = new CallExpr(fn.Text.ToLowerInvariant(), args, false, fn.Span.Cover(Previous.Span));
            }
            else if (Current.Kind == TokenKind.Identifier && !Current.Is("true") && !Current.Is("false"))
            {
                // A bare word such as latest or horizon.
                value = new LiteralExpr(LiteralKind.String, Current.Text.ToLowerInvariant(), Current.Span);
                _index++;
            }
            else
            {
                value = ParseValue(name);
            }

            options.Add(new QueryOption(name, value, nameToken.Span.Cover(Previous.Span)));
        }
        while (AcceptSymbol(","));

        ExpectSymbol(")", "Expected ')' after the options.");
        _ = start;
        return options;
    }

    // A literal or a parameter (take, skip, limit, offset, version, ids).
    private Expr ParseValue(string context)
    {
        Token token = Current;
        if (token.Kind == TokenKind.Parameter || token.Kind is TokenKind.Integer or TokenKind.Decimal or TokenKind.String or TokenKind.Duration
            || token.Is("true") || token.Is("false") || token.Is("datetime") || (token.IsSymbol("-") && tokens[_index + 1].Kind is TokenKind.Integer or TokenKind.Decimal))
        {
            return ParseUnary();
        }

        Fail(QueryCodes.Syntax, $"Expected a literal or a parameter for {context}.", token.Span);
        return null!;
    }

    // ================================================================ expressions

    private Expr ParseExpr()
    {
        if (++_depth > limits.MaxDepth)
        {
            Fail(QueryCodes.Limit, $"The expression is nested deeper than {limits.MaxDepth} levels.", Current.Span);
        }

        try
        {
            return ParseOr();
        }
        finally
        {
            _depth--;
        }
    }

    private Expr ParseOr()
    {
        Expr left = ParseXor();
        while (AcceptKeyword("or"))
        {
            Expr right = ParseXor();
            left = new BinaryExpr(BinaryOperator.Or, left, right, left.Span.Cover(right.Span));
        }

        return left;
    }

    private Expr ParseXor()
    {
        Expr left = ParseAnd();
        while (AcceptKeyword("xor"))
        {
            Expr right = ParseAnd();
            left = new BinaryExpr(BinaryOperator.Xor, left, right, left.Span.Cover(right.Span));
        }

        return left;
    }

    private Expr ParseAnd()
    {
        Expr left = ParseNot();
        while (AcceptKeyword("and"))
        {
            Expr right = ParseNot();
            left = new BinaryExpr(BinaryOperator.And, left, right, left.Span.Cover(right.Span));
        }

        return left;
    }

    private Expr ParseNot()
    {
        Token start = Current;
        if (AcceptKeyword("not"))
        {
            if (++_depth > limits.MaxDepth)
            {
                Fail(QueryCodes.Limit, $"The expression is nested deeper than {limits.MaxDepth} levels.", start.Span);
            }

            Expr operand = ParseNot();
            _depth--;
            return operand is ExistsSubqueryExpr exists
                ? exists with { Negated = !exists.Negated, Span = start.Span.Cover(exists.Span) }
                : new UnaryExpr(UnaryOperator.Not, operand, start.Span.Cover(operand.Span));
        }

        return ParsePredicate();
    }

    private Expr ParsePredicate()
    {
        Expr left = ParseAdditive();
        Token op = Current;
        BinaryOperator? comparison = op.Kind == TokenKind.Symbol
            ? op.Text switch
            {
                "=" => BinaryOperator.Equal,
                "!=" or "<>" => BinaryOperator.NotEqual,
                "<" => BinaryOperator.Less,
                "<=" => BinaryOperator.LessOrEqual,
                ">" => BinaryOperator.Greater,
                ">=" => BinaryOperator.GreaterOrEqual,
                _ => null,
            }
            : null;
        if (comparison is { } binary)
        {
            _index++;
            Expr right = ParseAdditive();
            return new BinaryExpr(binary, left, right, left.Span.Cover(right.Span));
        }

        bool negated = false;
        if (Current.Is("not") && (tokens[_index + 1].Is("like") || tokens[_index + 1].Is("in") || tokens[_index + 1].Is("between")))
        {
            negated = true;
            _index++;
        }

        if (AcceptKeyword("like"))
        {
            Expr pattern = ParseAdditive();
            return new LikeExpr(left, pattern, negated, left.Span.Cover(pattern.Span));
        }

        if (AcceptKeyword("in"))
        {
            Token open = Current;
            ExpectSymbol("(", "Expected '(' after IN.");
            if (Current.Is("select"))
            {
                if (language != QueryLanguage.Sql)
                {
                    Fail(QueryCodes.Unsupported, "Subqueries are only available in the SQL syntax; use join.", Current.Span);
                }

                QueryAst subquery = ParseSubquery(open);
                ExpectSymbol(")", "Expected ')'.");
                return new InSubqueryExpr(left, subquery, negated, left.Span.Cover(Previous.Span));
            }

            var items = new List<Expr>();
            do
            {
                items.Add(ParseAdditive());
                if (items.Count > limits.MaxInItems)
                {
                    Fail(QueryCodes.Limit, $"An IN list can have at most {limits.MaxInItems} items.", items[^1].Span);
                }
            }
            while (AcceptSymbol(","));

            ExpectSymbol(")", "Expected ')' after the IN list.");
            return new InListExpr(left, items, negated, left.Span.Cover(Previous.Span));
        }

        if (AcceptKeyword("between"))
        {
            Expr low = ParseAdditive();
            ExpectKeyword("and", "Expected AND in BETWEEN.");
            Expr high = ParseAdditive();
            return new BetweenExpr(left, low, high, negated, left.Span.Cover(high.Span));
        }

        if (negated)
        {
            Fail(QueryCodes.Syntax, "Expected LIKE, IN or BETWEEN after NOT.", Current.Span);
        }

        if (AcceptKeyword("is"))
        {
            bool not = AcceptKeyword("not");
            ExpectKeyword("null", "Expected NULL after IS.");
            return new IsNullExpr(left, not, left.Span.Cover(Previous.Span));
        }

        return left;
    }

    private Expr ParseAdditive()
    {
        Expr left = ParseMultiplicative();
        while (Current.IsSymbol("+") || Current.IsSymbol("-"))
        {
            BinaryOperator op = Current.Text == "+" ? BinaryOperator.Add : BinaryOperator.Subtract;
            _index++;
            Expr right = ParseMultiplicative();
            left = new BinaryExpr(op, left, right, left.Span.Cover(right.Span));
        }

        return left;
    }

    private Expr ParseMultiplicative()
    {
        Expr left = ParseUnary();
        while (Current.IsSymbol("*") || Current.IsSymbol("/") || Current.IsSymbol("%"))
        {
            BinaryOperator op = Current.Text switch { "*" => BinaryOperator.Multiply, "/" => BinaryOperator.Divide, _ => BinaryOperator.Modulo };
            _index++;
            Expr right = ParseUnary();
            left = new BinaryExpr(op, left, right, left.Span.Cover(right.Span));
        }

        return left;
    }

    private Expr ParseUnary()
    {
        Token start = Current;
        if (AcceptSymbol("-"))
        {
            Expr operand = ParseUnary();
            return operand is LiteralExpr { Kind: LiteralKind.Integer or LiteralKind.Decimal } literal
                ? literal with { Value = literal.Value is long l ? -l : -(decimal)literal.Value!, Span = start.Span.Cover(literal.Span) }
                : new UnaryExpr(UnaryOperator.Negate, operand, start.Span.Cover(operand.Span));
        }

        return ParsePrimary();
    }

    private Expr ParsePrimary()
    {
        Token token = Current;
        switch (token.Kind)
        {
            case TokenKind.Integer:
                _index++;
                if (!long.TryParse(token.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long integer))
                {
                    Fail(QueryCodes.Syntax, "The number is too large.", token.Span);
                }

                return new LiteralExpr(LiteralKind.Integer, integer, token.Span);
            case TokenKind.Decimal:
                _index++;
                if (!decimal.TryParse(token.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal number))
                {
                    Fail(QueryCodes.Syntax, "The number is out of range.", token.Span);
                }

                return new LiteralExpr(LiteralKind.Decimal, number, token.Span);
            case TokenKind.Duration:
                _index++;
                return new LiteralExpr(LiteralKind.Duration, Lexer.ParseDuration(token.Text), token.Span);
            case TokenKind.String:
                _index++;
                return new LiteralExpr(LiteralKind.String, token.Text, token.Span);
            case TokenKind.Parameter:
                _index++;
                _parameters.Add(token.Text);
                if (_parameters.Count > limits.MaxParameters)
                {
                    Fail(QueryCodes.Limit, $"A query can use at most {limits.MaxParameters} parameters.", token.Span);
                }

                return new ParameterExpr(token.Text, token.Span);
            case TokenKind.Symbol when token.Text == "(":
            {
                _index++;
                if (Current.Is("select"))
                {
                    Fail(QueryCodes.Unsupported, "Scalar subqueries are not supported; use IN (SELECT …) or EXISTS (SELECT …).", Current.Span);
                }

                Expr inner = ParseExpr();
                ExpectSymbol(")", "Expected ')'.");
                return inner;
            }

            case TokenKind.QuotedIdentifier:
                return ParseColumn();
            case TokenKind.Identifier:
                return ParseIdentifierExpression(token);
            default:
                Fail(QueryCodes.Syntax, $"Expected an expression but found {Describe(token)}.", token.Span);
                return null!;
        }
    }

    private Expr ParseIdentifierExpression(Token token)
    {
        string word = token.Text.ToLowerInvariant();
        switch (word)
        {
            case "true" or "false":
                _index++;
                return new LiteralExpr(LiteralKind.Boolean, word == "true", token.Span);
            case "null":
                _index++;
                return new LiteralExpr(LiteralKind.Null, null, token.Span);
            case "case":
                return ParseCase();
            case "cast" when tokens[_index + 1].IsSymbol("("):
                return ParseCast();
            case "datetime" when tokens[_index + 1].IsSymbol("("):
            {
                _index += 2;
                LiteralExpr text = ExpectString("datetime takes an ISO 8601 timestamp in quotes.");
                ExpectSymbol(")", "Expected ')'.");
                if (!DateTimeOffset.TryParse((string)text.Value!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset value))
                {
                    Fail(QueryCodes.Syntax, "datetime takes an ISO 8601 timestamp.", text.Span);
                }

                return new LiteralExpr(LiteralKind.DateTime, value, token.Span.Cover(Previous.Span));
            }

            case "exists":
            {
                _index++;
                if (Current.IsSymbol("(") && tokens[_index + 1].Is("select"))
                {
                    if (language != QueryLanguage.Sql)
                    {
                        Fail(QueryCodes.Unsupported, "Subqueries are only available in the SQL syntax; use join.", Current.Span);
                    }

                    Token open = Current;
                    _index++;
                    QueryAst subquery = ParseSubquery(open);
                    ExpectSymbol(")", "Expected ')'.");
                    return new ExistsSubqueryExpr(subquery, false, token.Span.Cover(Previous.Span));
                }

                ColumnExpr column = ParseColumn();
                return new ExistsExpr(column, token.Span.Cover(column.Span));
            }

            case "select":
                Fail(QueryCodes.Unsupported, "Scalar subqueries are not supported; use IN (SELECT …) or EXISTS (SELECT …).", token.Span);
                return null!;
        }

        if (tokens[_index + 1].IsSymbol("("))
        {
            return ParseCall(token);
        }

        if (Statements.Contains(word) && language == QueryLanguage.Sql)
        {
            Fail(QueryCodes.Unsupported, $"Statement type '{word.ToUpperInvariant()}' is not supported: the query API is read-only.", token.Span);
        }

        return ParseColumn();
    }

    private Expr ParseCall(Token name)
    {
        string function = name.Text.ToLowerInvariant();
        if (!Scalars.Contains(function) && !Aggregates.Contains(function))
        {
            Fail(QueryCodes.Unsupported, $"Function {DiagnosticBag.Token(function)} is not in the allow-list (I-5).", name.Span);
        }

        _index += 2;
        bool distinct = false;
        var arguments = new List<Expr>();
        if (function == "count" && AcceptSymbol("*"))
        {
            // COUNT(*) = count(): count rows.
        }
        else if (!Current.IsSymbol(")"))
        {
            if (AcceptKeyword("distinct"))
            {
                if (!Aggregates.Contains(function))
                {
                    Fail(QueryCodes.Syntax, "DISTINCT is only allowed inside an aggregate.", Previous.Span);
                }

                distinct = true;
            }

            do
            {
                arguments.Add(ParseExpr());
            }
            while (AcceptSymbol(","));
        }

        ExpectSymbol(")", "Expected ')' to close the function call.");
        if (Current.Is("over"))
        {
            Fail(QueryCodes.Unsupported, "Window functions (OVER) are not supported.", Current.Span);
        }

        if (function == "count_distinct")
        {
            function = "count";
            distinct = true;
        }

        if (Aggregates.Contains(function) && function != "count" && arguments.Count != 1)
        {
            Fail(QueryCodes.Syntax, $"{function} takes one argument.", name.Span.Cover(Previous.Span));
        }

        if (function == "count" && distinct && arguments.Count != 1)
        {
            Fail(QueryCodes.Syntax, "count(distinct …) takes one argument.", name.Span.Cover(Previous.Span));
        }

        return new CallExpr(function, arguments, distinct, name.Span.Cover(Previous.Span));
    }

    private Expr ParseCase()
    {
        Token start = Current;
        _index++;
        if (!Current.Is("when"))
        {
            Fail(QueryCodes.Unsupported, "Only searched CASE is supported: CASE WHEN <condition> THEN … END.", Current.Span);
        }

        var branches = new List<CaseBranch>();
        while (AcceptKeyword("when"))
        {
            Expr when = ParseExpr();
            ExpectKeyword("then", "Expected THEN.");
            branches.Add(new CaseBranch(when, ParseExpr()));
        }

        Expr? otherwise = AcceptKeyword("else") ? ParseExpr() : null;
        ExpectKeyword("end", "Expected END to close CASE.");
        return new CaseExpr(branches, otherwise, start.Span.Cover(Previous.Span));
    }

    private Expr ParseCast()
    {
        Token start = Current;
        _index += 2;
        Expr value = ParseExpr();
        ExpectKeyword("as", "Expected AS in cast.");
        Token type = Current;
        string name = ExpectIdentifier("Expected a type: string, integer, decimal, boolean, timestamp or uuid.").ToLowerInvariant();
        if (!CastTypes.Contains(name))
        {
            Fail(QueryCodes.Unsupported, "Cast types are string, integer, decimal, boolean, timestamp and uuid.", type.Span);
        }

        ExpectSymbol(")", "Expected ')'.");
        return new CastExpr(value, name switch { "int" => "integer", "bool" => "boolean", _ => name }, start.Span.Cover(Previous.Span));
    }

    private ColumnExpr ParseColumn()
    {
        Token start = Current;
        var path = new List<object> { ExpectName("Expected a column name.").Value! };
        while (true)
        {
            if (Current.IsSymbol(".") && tokens[_index + 1].Kind is TokenKind.Identifier or TokenKind.QuotedIdentifier)
            {
                _index++;
                path.Add(ExpectName("Expected a field name.").Value!);
            }
            else if (Current.IsSymbol("[") && tokens[_index + 1].Kind == TokenKind.Integer && tokens[_index + 2].IsSymbol("]"))
            {
                path.Add(int.Parse(tokens[_index + 1].Text, NumberStyles.None, CultureInfo.InvariantCulture));
                _index += 3;
            }
            else
            {
                break;
            }
        }

        return new ColumnExpr(null, path, start.Span.Cover(Previous.Span));
    }

    // ================================================================ normalisation

    // Resolves qualifiers: a path whose first segment is a declared alias becomes alias + rest (aliases are only known
    // after FROM is parsed, so this runs on the finished tree). Subqueries see their own aliases and the outer ones.
    private static QueryAst Qualify(QueryAst query, HashSet<string> outer)
    {
        var aliases = new HashSet<string>(outer, StringComparer.Ordinal);
        if (query.Source.Alias is { } a)
        {
            aliases.Add(a);
        }

        foreach (JoinClause join in query.Joins)
        {
            if (join.Source.Alias is { } j)
            {
                aliases.Add(j);
            }
        }

        Expr Q(Expr e)
        {
            return ExprRewriter.Rewrite(e, node => node switch
            {
                ColumnExpr { Qualifier: null, Path.Count: >= 2 } c when c.Path[0] is string head && aliases.Contains(head)
                    => c with { Qualifier = head, Path = [.. c.Path.Skip(1)] },
                InSubqueryExpr s => s with { Subquery = Qualify(s.Subquery, aliases) },
                ExistsSubqueryExpr s => s with { Subquery = Qualify(s.Subquery, aliases) },
                _ => null,
            });
        }

        ColumnExpr QC(ColumnExpr c)
        {
            return (ColumnExpr)Q(c);
        }

        return query with
        {
            Joins = [.. query.Joins.Select(j => j with { On = [.. j.On.Select(e => new JoinEquality(QC(e.Left), QC(e.Right)))] })],
            Where = query.Where is null ? null : Q(query.Where),
            Select = query.Select?.Select(i => i.Expression is null ? i : i with { Expression = Q(i.Expression) }).ToArray(),
            GroupBy = [.. query.GroupBy.Select(Q)],
            Having = query.Having is null ? null : Q(query.Having),
            OrderBy = [.. query.OrderBy.Select(s => s with { Expression = Q(s.Expression) })],
        };
    }

    private static Expr Substitute(Expr expr, Dictionary<string, Expr> named)
    {
        return ExprRewriter.Rewrite(expr, node => node is ColumnExpr { Qualifier: null, Path.Count: 1 } c && c.Path[0] is string name && named.TryGetValue(name, out Expr? replacement)
            ? replacement
            : null);
    }

    private static SelectItem SubstituteItem(SelectItem item, Dictionary<string, Expr> named)
    {
        if (item.Expression is ColumnExpr { Qualifier: null, Path.Count: 1 } c && c.Path[0] is string name && named.ContainsKey(name))
        {
            return item with { Expression = named[name], Alias = item.Alias ?? name };
        }

        return item.Expression is null ? item : item with { Expression = Substitute(item.Expression, named) };
    }

    private static Expr? Combine(List<Expr> parts)
    {
        Expr? result = null;
        foreach (Expr part in parts)
        {
            result = result is null ? part : new BinaryExpr(BinaryOperator.And, result, part, result.Span.Cover(part.Span));
        }

        return result;
    }

    /// <summary>The output name of an unaliased expression: the last path segment of a column, or the function name.</summary>
    internal static string OutputName(Expr expr)
    {
        return expr switch
        {
            ColumnExpr c => c.Path.LastOrDefault(s => s is string) as string ?? "column",
            CallExpr call => call.Name,
            _ => "column",
        };
    }

    // ================================================================ token helpers

    private Token Previous => tokens[Math.Max(0, _index - 1)];

    private bool AcceptKeyword(string keyword)
    {
        if (Current.Is(keyword))
        {
            _index++;
            return true;
        }

        return false;
    }

    private bool AcceptSymbol(string symbol)
    {
        if (Current.IsSymbol(symbol))
        {
            _index++;
            return true;
        }

        return false;
    }

    private void ExpectKeyword(string keyword, string message)
    {
        if (!AcceptKeyword(keyword))
        {
            Fail(QueryCodes.Syntax, message, Current.Span);
        }
    }

    private void ExpectSymbol(string symbol, string message)
    {
        if (!AcceptSymbol(symbol))
        {
            Fail(QueryCodes.Syntax, message, Current.Span);
        }
    }

    private string ExpectIdentifier(string message)
    {
        if (Current.Kind != TokenKind.Identifier)
        {
            Fail(QueryCodes.Syntax, message, Current.Span);
        }

        return tokens[_index++].Text;
    }

    private string ExpectAlias()
    {
        if (Current.Kind is not (TokenKind.Identifier or TokenKind.QuotedIdentifier))
        {
            Fail(QueryCodes.Syntax, "Expected an alias.", Current.Span);
        }

        return tokens[_index++].Text;
    }

    private LiteralExpr ExpectString(string message)
    {
        if (Current.Kind != TokenKind.String)
        {
            Fail(QueryCodes.Syntax, message, Current.Span);
        }

        Token token = tokens[_index++];
        return new LiteralExpr(LiteralKind.String, token.Text, token.Span);
    }

    // An identifier or (SQL) quoted identifier, as a string literal carrying its span.
    private LiteralExpr ExpectName(string message)
    {
        if (Current.Kind is not (TokenKind.Identifier or TokenKind.QuotedIdentifier))
        {
            Fail(QueryCodes.Syntax, message, Current.Span);
        }

        Token token = tokens[_index++];
        return new LiteralExpr(LiteralKind.String, token.Text, token.Span);
    }

    private static string Describe(Token token)
    {
        return token.Kind switch
        {
            TokenKind.EndOfInput => "the end of the query",
            TokenKind.String => "a string",
            TokenKind.Integer or TokenKind.Decimal => "a number",
            TokenKind.Parameter => "a parameter",
            TokenKind.Symbol => DiagnosticBag.Token(token.Text),
            _ => "the word " + DiagnosticBag.Token(token.Text),
        };
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void Fail(string code, string message, TextSpan span)
    {
        diagnostics.Add(code, message, span);
        throw new ParseFailedException();
    }

    internal sealed class ParseFailedException : Exception;
}

/// <summary>Rewrites expression trees bottom-up; the callback returns a replacement or null to keep the node.</summary>
internal static class ExprRewriter
{
    public static Expr Rewrite(Expr expr, Func<Expr, Expr?> replace)
    {
        Expr rebuilt = expr switch
        {
            UnaryExpr u => u with { Operand = Rewrite(u.Operand, replace) },
            BinaryExpr b => b with { Left = Rewrite(b.Left, replace), Right = Rewrite(b.Right, replace) },
            LikeExpr l => l with { Value = Rewrite(l.Value, replace), Pattern = Rewrite(l.Pattern, replace) },
            InListExpr i => i with { Value = Rewrite(i.Value, replace), Items = [.. i.Items.Select(x => Rewrite(x, replace))] },
            InSubqueryExpr s => s with { Value = Rewrite(s.Value, replace) },
            BetweenExpr b => b with { Value = Rewrite(b.Value, replace), Low = Rewrite(b.Low, replace), High = Rewrite(b.High, replace) },
            IsNullExpr n => n with { Value = Rewrite(n.Value, replace) },
            CallExpr c => c with { Arguments = [.. c.Arguments.Select(x => Rewrite(x, replace))] },
            CastExpr c => c with { Value = Rewrite(c.Value, replace) },
            CaseExpr c => c with
            {
                Branches = [.. c.Branches.Select(b => new CaseBranch(Rewrite(b.When, replace), Rewrite(b.Then, replace)))],
                Else = c.Else is null ? null : Rewrite(c.Else, replace),
            },
            ExistsExpr e => Rewrite(e.Column, replace) is ColumnExpr column ? e with { Column = column } : e,
            _ => expr,
        };
        return replace(rebuilt) ?? rebuilt;
    }
}

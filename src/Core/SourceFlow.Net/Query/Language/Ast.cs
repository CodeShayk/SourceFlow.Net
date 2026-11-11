namespace SourceFlow.Query;

/// <summary>A range of the query text: the start offset and the length, in UTF-16 code units.</summary>
public readonly record struct TextSpan(int Start, int Length)
{
    /// <summary>The offset just after the span.</summary>
    public int End => Start + Length;

    /// <summary>A span covering both spans.</summary>
    public TextSpan Cover(TextSpan other)
    {
        int start = Math.Min(Start, other.Start);
        return new TextSpan(start, Math.Max(End, other.End) - start);
    }
}

/// <summary>The surface syntax a query was written in (doc 14 §3). Both parse into the same <see cref="QueryAst"/>.</summary>
public enum QueryLanguage
{
    /// <summary>The pipeline syntax: <c>from … | where … | project …</c>.</summary>
    Sfql = 0,

    /// <summary>The read-only <c>SELECT</c> subset (doc 14 §4.2).</summary>
    Sql = 1,
}

// ---------------------------------------------------------------- expressions

/// <summary>An SFQL expression (CESQL v1 extended, doc 14 §4.1).</summary>
public abstract record Expr(TextSpan Span);

/// <summary>The kind of a literal.</summary>
public enum LiteralKind
{
    /// <summary><c>null</c>.</summary>
    Null,

    /// <summary><c>true</c> / <c>false</c>.</summary>
    Boolean,

    /// <summary>A whole number (<see cref="long"/>).</summary>
    Integer,

    /// <summary>A decimal number (<see cref="decimal"/>), for example <c>12.50</c> or <c>12.50m</c>.</summary>
    Decimal,

    /// <summary>A string.</summary>
    String,

    /// <summary>A duration (<see cref="TimeSpan"/>): <c>7d</c>, <c>12h</c>, <c>30m</c>, <c>15s</c>.</summary>
    Duration,

    /// <summary>A timestamp (<see cref="DateTimeOffset"/>): <c>datetime('2026-01-01T00:00:00Z')</c>.</summary>
    DateTime,
}

/// <summary>A literal value. It always becomes a bound parameter in generated SQL (rule I-3).</summary>
public sealed record LiteralExpr(LiteralKind Kind, object? Value, TextSpan Span) : Expr(Span);

/// <summary>A named parameter, <c>@name</c>.</summary>
public sealed record ParameterExpr(string Name, TextSpan Span) : Expr(Span);

/// <summary>A column or attribute path: <c>stream</c>, <c>e.time</c>, <c>data.items[0].sku</c>.</summary>
/// <param name="Qualifier">The source alias, when the path is qualified.</param>
/// <param name="Path">The segments: names (<see cref="string"/>) and array indexes (<see cref="int"/>).</param>
/// <param name="Span">The position.</param>
public sealed record ColumnExpr(string? Qualifier, IReadOnlyList<object> Path, TextSpan Span) : Expr(Span)
{
    /// <summary>The path as text, for example <c>data.items[0].sku</c>.</summary>
    public string PathText => string.Concat(Path.Select((s, i) => s is int index ? $"[{index}]" : (i == 0 ? "" : ".") + s));

    /// <summary>The first segment.</summary>
    public string Head => (string)Path[0];
}

/// <summary>A unary operator.</summary>
public enum UnaryOperator
{
    /// <summary>Logical <c>NOT</c>.</summary>
    Not,

    /// <summary>Arithmetic negation.</summary>
    Negate,
}

/// <summary>A unary operation.</summary>
public sealed record UnaryExpr(UnaryOperator Operator, Expr Operand, TextSpan Span) : Expr(Span);

/// <summary>A binary operator.</summary>
public enum BinaryOperator
{
    /// <summary><c>OR</c>.</summary>
    Or,

    /// <summary><c>XOR</c>.</summary>
    Xor,

    /// <summary><c>AND</c>.</summary>
    And,

    /// <summary><c>=</c>.</summary>
    Equal,

    /// <summary><c>!=</c> or <c>&lt;&gt;</c>.</summary>
    NotEqual,

    /// <summary><c>&lt;</c>.</summary>
    Less,

    /// <summary><c>&lt;=</c>.</summary>
    LessOrEqual,

    /// <summary><c>&gt;</c>.</summary>
    Greater,

    /// <summary><c>&gt;=</c>.</summary>
    GreaterOrEqual,

    /// <summary><c>+</c>.</summary>
    Add,

    /// <summary><c>-</c>.</summary>
    Subtract,

    /// <summary><c>*</c>.</summary>
    Multiply,

    /// <summary><c>/</c>.</summary>
    Divide,

    /// <summary><c>%</c>.</summary>
    Modulo,
}

/// <summary>A binary operation.</summary>
public sealed record BinaryExpr(BinaryOperator Operator, Expr Left, Expr Right, TextSpan Span) : Expr(Span);

/// <summary><c>x [NOT] LIKE pattern</c> (CESQL <c>%</c> and <c>_</c> wildcards).</summary>
public sealed record LikeExpr(Expr Value, Expr Pattern, bool Negated, TextSpan Span) : Expr(Span);

/// <summary><c>x [NOT] IN (a, b, …)</c>.</summary>
public sealed record InListExpr(Expr Value, IReadOnlyList<Expr> Items, bool Negated, TextSpan Span) : Expr(Span);

/// <summary><c>x [NOT] IN (SELECT …)</c> (SQL syntax, G-12).</summary>
public sealed record InSubqueryExpr(Expr Value, QueryAst Subquery, bool Negated, TextSpan Span) : Expr(Span);

/// <summary><c>[NOT] EXISTS (SELECT …)</c> (SQL syntax, G-12).</summary>
public sealed record ExistsSubqueryExpr(QueryAst Subquery, bool Negated, TextSpan Span) : Expr(Span);

/// <summary>CESQL <c>EXISTS attribute</c>: the attribute or field is present.</summary>
public sealed record ExistsExpr(ColumnExpr Column, TextSpan Span) : Expr(Span);

/// <summary><c>x [NOT] BETWEEN low AND high</c>.</summary>
public sealed record BetweenExpr(Expr Value, Expr Low, Expr High, bool Negated, TextSpan Span) : Expr(Span);

/// <summary><c>x IS [NOT] NULL</c> (true for missing and for JSON null, doc 14 §5.5).</summary>
public sealed record IsNullExpr(Expr Value, bool Negated, TextSpan Span) : Expr(Span);

/// <summary>
/// A function or aggregate call. Names are lower-case. <c>count()</c> and <c>COUNT(*)</c> are both <c>count</c> with no
/// arguments; <c>count_distinct(x)</c> and <c>COUNT(DISTINCT x)</c> are both <c>count</c> with <see cref="Distinct"/>.
/// </summary>
public sealed record CallExpr(string Name, IReadOnlyList<Expr> Arguments, bool Distinct, TextSpan Span) : Expr(Span);

/// <summary><c>cast(x as type)</c>.</summary>
public sealed record CastExpr(Expr Value, string Type, TextSpan Span) : Expr(Span);

/// <summary>One <c>WHEN … THEN …</c> branch.</summary>
public sealed record CaseBranch(Expr When, Expr Then);

/// <summary><c>CASE WHEN c THEN a … [ELSE b] END</c>.</summary>
public sealed record CaseExpr(IReadOnlyList<CaseBranch> Branches, Expr? Else, TextSpan Span) : Expr(Span);

// ---------------------------------------------------------------- sources

/// <summary>A query source (doc 14 §5).</summary>
public abstract record SourceRef(string? Alias, TextSpan Span);

/// <summary><c>events [of "t1", "t2" [version N]] [upcast]</c>: the global log, optionally narrowed to logical types.</summary>
public sealed record EventsSource(IReadOnlyList<LiteralExpr> Types, Expr? Version, bool Upcast, string? Alias, TextSpan Span) : SourceRef(Alias, Span);

/// <summary><c>stream @id</c>: one stream in version order.</summary>
public sealed record StreamSource(Expr Stream, string? Alias, TextSpan Span) : SourceRef(Alias, Span);

/// <summary><c>readmodel "collection"</c>: a projection's documents.</summary>
public sealed record ReadModelSource(LiteralExpr Collection, string? Alias, TextSpan Span) : SourceRef(Alias, Span);

/// <summary>How a temporal aggregate read is bounded.</summary>
public enum AsOfKind
{
    /// <summary>The latest state.</summary>
    Latest,

    /// <summary>Up to and including a stream version.</summary>
    Version,

    /// <summary>Up to and including a decision time.</summary>
    Time,
}

/// <summary><c>aggregate "name" id @id [as of version|time …]</c>: one aggregate's state (doc 14 §5.6).</summary>
public sealed record AggregateSource(LiteralExpr Name, Expr Id, AsOfKind AsOf, Expr? Bound, string? Alias, TextSpan Span) : SourceRef(Alias, Span);

/// <summary><c>schema [of "type"]</c>: catalog introspection (doc 14 §5.7).</summary>
public sealed record SchemaSource(LiteralExpr? Type, string? Alias, TextSpan Span) : SourceRef(Alias, Span);

// ---------------------------------------------------------------- query

/// <summary>How a join keeps rows (doc 14 §4.3).</summary>
public enum JoinKind
{
    /// <summary><c>INNER JOIN</c>.</summary>
    Inner,

    /// <summary><c>LEFT [OUTER] JOIN</c>.</summary>
    Left,
}

/// <summary>One equality of a join condition (G-7).</summary>
public sealed record JoinEquality(ColumnExpr Left, ColumnExpr Right);

/// <summary>A join: a source and a conjunction of column equalities.</summary>
public sealed record JoinClause(JoinKind Kind, SourceRef Source, IReadOnlyList<JoinEquality> On, TextSpan Span);

/// <summary>One output column. <see cref="StarQualifier"/> is set for <c>*</c> (empty string) or <c>alias.*</c>.</summary>
public sealed record SelectItem(Expr? Expression, string? Alias, string? StarQualifier, TextSpan Span)
{
    /// <summary>True for <c>*</c> and <c>alias.*</c>.</summary>
    public bool IsStar => StarQualifier is not null;
}

/// <summary>Where nulls sort.</summary>
public enum NullsOrder
{
    /// <summary>The provider default (nulls last ascending).</summary>
    Default,

    /// <summary><c>NULLS FIRST</c>.</summary>
    First,

    /// <summary><c>NULLS LAST</c>.</summary>
    Last,
}

/// <summary>One sort key.</summary>
public sealed record SortItem(Expr Expression, bool Descending, NullsOrder Nulls, TextSpan Span);

/// <summary>An <c>options (name = value, …)</c> entry; calls such as <c>at_least(@token)</c> are <see cref="CallExpr"/> values.</summary>
public sealed record QueryOption(string Name, Expr Value, TextSpan Span);

/// <summary>
/// The normalised query (doc 14 §3): what both syntaxes parse to. Pipeline stages are mapped onto the clauses of a
/// <c>SELECT</c>: <c>where</c> before <c>summarize</c> is <see cref="Where"/>, after it <see cref="Having"/>;
/// <c>summarize … by</c> is <see cref="GroupBy"/> plus aggregate select items; <c>project</c> is <see cref="Select"/>.
/// </summary>
/// <param name="Source">The primary source.</param>
/// <param name="Joins">Joins, in order.</param>
/// <param name="Where">The row filter.</param>
/// <param name="Select">The output columns; null for the source's default columns (G-3).</param>
/// <param name="Distinct">True for <c>DISTINCT</c> / <c>distinct</c>.</param>
/// <param name="GroupBy">Grouping keys.</param>
/// <param name="Having">The group filter.</param>
/// <param name="OrderBy">Sort keys.</param>
/// <param name="Limit">The row limit (<c>take</c> / <c>LIMIT</c>).</param>
/// <param name="Offset">Rows skipped (<c>skip</c> / <c>OFFSET</c>, read models only).</param>
/// <param name="Options">Query options.</param>
/// <param name="Language">The syntax it was written in.</param>
/// <param name="Span">The whole query.</param>
public sealed record QueryAst(
    SourceRef Source,
    IReadOnlyList<JoinClause> Joins,
    Expr? Where,
    IReadOnlyList<SelectItem>? Select,
    bool Distinct,
    IReadOnlyList<Expr> GroupBy,
    Expr? Having,
    IReadOnlyList<SortItem> OrderBy,
    Expr? Limit,
    Expr? Offset,
    IReadOnlyList<QueryOption> Options,
    QueryLanguage Language,
    TextSpan Span)
{
    /// <summary>The canonical text of the query, independent of syntax and positions (for equivalence and golden tests).</summary>
    public override string ToString()
    {
        return AstPrinter.Print(this);
    }
}

namespace SourceFlow.Query;

/// <summary>
/// A query diagnostic (doc 14 §13). Messages are constant templates: they carry codes, positions and at most a short
/// JSON-encoded token, never the caller's query text or parameter values (rule S-3).
/// </summary>
/// <param name="Code">The <c>SFQ</c> code.</param>
/// <param name="Message">The message.</param>
/// <param name="Line">The 1-based line.</param>
/// <param name="Column">The 1-based column.</param>
/// <param name="Length">The length of the offending text.</param>
public sealed record QueryDiagnostic(string Code, string Message, int Line, int Column, int Length)
{
    /// <inheritdoc />
    public override string ToString()
    {
        return $"{Code} ({Line},{Column}): {Message}";
    }
}

/// <summary>The SFQL diagnostic codes (doc 14 §13).</summary>
public static class QueryCodes
{
    /// <summary>Syntax error.</summary>
    public const string Syntax = "SFQ0101";

    /// <summary>Unsupported construct or statement (read-only, allow-listed grammar).</summary>
    public const string Unsupported = "SFQ0102";

    /// <summary>Request limit exceeded.</summary>
    public const string Limit = "SFQ0103";

    /// <summary>Forbidden characters.</summary>
    public const string ForbiddenCharacters = "SFQ0104";

    /// <summary><c>data</c> field on an un-narrowed <c>events</c> source.</summary>
    public const string OpaqueData = "SFQ0201";

    /// <summary>Unknown field or source, or incompatible types across versions.</summary>
    public const string UnknownName = "SFQ0202";

    /// <summary>Type mismatch.</summary>
    public const string TypeMismatch = "SFQ0203";

    /// <summary>Type not owned by this service (Q-B1).</summary>
    public const string NotOwned = "SFQ0204";

    /// <summary>Ambiguous column.</summary>
    public const string Ambiguous = "SFQ0205";

    /// <summary>Output neither aggregated nor grouped.</summary>
    public const string NotGrouped = "SFQ0206";

    /// <summary>Personal-data field used as a filter, sort, group or join key.</summary>
    public const string PersonalData = "SFQ0301";

    /// <summary>Unbounded scan.</summary>
    public const string UnboundedScan = "SFQ0401";

    /// <summary>Non-queryable field without a bounding predicate.</summary>
    public const string NotQueryable = "SFQ0402";

    /// <summary>Upcast row limit exceeded.</summary>
    public const string UpcastLimit = "SFQ0403";

    /// <summary>Too many joins or subqueries.</summary>
    public const string TooManyJoins = "SFQ0404";

    /// <summary>Join condition not on an indexed key.</summary>
    public const string JoinNotIndexed = "SFQ0405";

    /// <summary>Join of two unbounded <c>events</c> sources.</summary>
    public const string UnboundedEventJoin = "SFQ0406";

    /// <summary>Estimated cost above the ceiling.</summary>
    public const string CostCeiling = "SFQ0407";

    /// <summary>Too many result groups.</summary>
    public const string TooManyGroups = "SFQ0408";

    /// <summary><c>OFFSET</c>/<c>skip</c> not allowed or too large.</summary>
    public const string Offset = "SFQ0409";

    /// <summary>Permission denied.</summary>
    public const string PermissionDenied = "SFQ0501";

    /// <summary>Continuation token invalid for this query or schema.</summary>
    public const string Continuation = "SFQ0601";

    /// <summary>Consistency wait timed out.</summary>
    public const string ConsistencyTimeout = "SFQ0602";
}

/// <summary>Request limits (doc 14 §9.4). Defaults are configurable within the hard caps.</summary>
public sealed record QueryLimits
{
    /// <summary>The default limits.</summary>
    public static QueryLimits Default { get; } = new();

    /// <summary>The longest query text, in characters (default 16 KB, cap 64 KB).</summary>
    public int MaxTextLength { get; init; } = 16 * 1024;

    /// <summary>The most tokens (default 4,000, cap 16,000).</summary>
    public int MaxTokens { get; init; } = 4000;

    /// <summary>The deepest expression nesting (default 32, cap 64).</summary>
    public int MaxDepth { get; init; } = 32;

    /// <summary>The most joins (default 3, cap 5).</summary>
    public int MaxJoins { get; init; } = 3;

    /// <summary>The most subqueries (default 2, cap 4).</summary>
    public int MaxSubqueries { get; init; } = 2;

    /// <summary>The most items in an <c>IN</c> list (default 1,000, cap 10,000).</summary>
    public int MaxInItems { get; init; } = 1000;

    /// <summary>The most parameters (default 100, cap 500).</summary>
    public int MaxParameters { get; init; } = 100;

    /// <summary>The most selected columns (default 64, cap 256).</summary>
    public int MaxColumns { get; init; } = 64;

    /// <summary>The most grouping keys (default 8, cap 32).</summary>
    public int MaxGroupingKeys { get; init; } = 8;

    /// <summary>The most rows a query may request (default 1,000, cap 10,000).</summary>
    public int MaxRows { get; init; } = 1000;

    /// <summary>The page size when the query has no limit (default 100, cap 1,000).</summary>
    public int PageSize { get; init; } = 100;

    /// <summary>The largest <c>OFFSET</c> (read models only; default 10,000, cap 100,000).</summary>
    public int MaxOffset { get; init; } = 10_000;

    /// <summary>Clamps every value to its hard cap.</summary>
    public QueryLimits Capped()
    {
        return this with
        {
            MaxTextLength = Math.Clamp(MaxTextLength, 1, 64 * 1024),
            MaxTokens = Math.Clamp(MaxTokens, 1, 16_000),
            MaxDepth = Math.Clamp(MaxDepth, 1, 64),
            MaxJoins = Math.Clamp(MaxJoins, 0, 5),
            MaxSubqueries = Math.Clamp(MaxSubqueries, 0, 4),
            MaxInItems = Math.Clamp(MaxInItems, 1, 10_000),
            MaxParameters = Math.Clamp(MaxParameters, 0, 500),
            MaxColumns = Math.Clamp(MaxColumns, 1, 256),
            MaxGroupingKeys = Math.Clamp(MaxGroupingKeys, 1, 32),
            MaxRows = Math.Clamp(MaxRows, 1, 10_000),
            PageSize = Math.Clamp(PageSize, 1, 1000),
            MaxOffset = Math.Clamp(MaxOffset, 0, 100_000),
        };
    }
}

/// <summary>The result of parsing: the query, or the diagnostics that stopped it.</summary>
public sealed record ParseResult(QueryAst? Query, IReadOnlyList<QueryDiagnostic> Diagnostics)
{
    /// <summary>True when the query parsed without errors.</summary>
    public bool Success => Query is not null && Diagnostics.Count == 0;
}

/// <summary>Collects diagnostics with line and column positions computed from the text.</summary>
internal sealed class DiagnosticBag(string text)
{
    private readonly List<QueryDiagnostic> _items = [];

    public IReadOnlyList<QueryDiagnostic> Items => _items;

    public bool HasErrors => _items.Count > 0;

    public void Add(string code, string message, TextSpan span)
    {
        (int line, int column) = Position(span.Start);
        _items.Add(new QueryDiagnostic(code, message, line, column, Math.Max(0, span.Length)));
    }

    /// <summary>A short, JSON-encoded rendering of a token for messages (S-3: at most 64 characters, never raw).</summary>
    public static string Token(string value)
    {
        string shortened = value.Length > 64 ? value[..61] + "..." : value;
        return System.Text.Json.JsonSerializer.Serialize(shortened, QueryJsonContext.Default.String);
    }

    private (int Line, int Column) Position(int offset)
    {
        int line = 1;
        int column = 1;
        for (int i = 0; i < Math.Min(offset, text.Length); i++)
        {
            if (text[i] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }

        return (line, column);
    }
}

/// <summary>Source-generated JSON for query diagnostics (AOT-safe).</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class QueryJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

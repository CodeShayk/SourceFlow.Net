namespace SourceFlow.Query;

/// <summary>The kind of a bound source (doc 14 §5).</summary>
public enum SourceKind
{
    /// <summary>The global log.</summary>
    Events,

    /// <summary>One stream.</summary>
    Stream,

    /// <summary>A read model's documents.</summary>
    ReadModel,

    /// <summary>One aggregate's state.</summary>
    Aggregate,

    /// <summary>Catalog introspection.</summary>
    Schema,
}

/// <summary>A source resolved against the catalog.</summary>
/// <param name="Index">The position: 0 is the primary source, then joins in order.</param>
/// <param name="Name">The alias, or a default name when the source has none.</param>
/// <param name="Kind">The kind.</param>
/// <param name="Syntax">The parsed source.</param>
/// <param name="EventTypes">The narrowed event types (<c>of …</c>); empty means opaque <c>data</c>.</param>
/// <param name="Version">A single version restriction (<c>version N</c>), when given as a literal.</param>
/// <param name="ReadModel">The read model, for read-model sources.</param>
/// <param name="Aggregate">The aggregate, for aggregate sources.</param>
public sealed record BoundSource(
    int Index,
    string Name,
    SourceKind Kind,
    SourceRef Syntax,
    IReadOnlyList<QueryEventType> EventTypes,
    int? Version,
    QueryReadModel? ReadModel,
    QueryAggregate? Aggregate)
{
    /// <summary>True for <c>events</c> and <c>stream</c> sources.</summary>
    public bool IsEventLike => Kind is SourceKind.Events or SourceKind.Stream;
}

/// <summary>What a column refers to.</summary>
public enum ColumnKind
{
    /// <summary>A source attribute (doc 14 §5.1): <c>position</c>, <c>time</c>, read-model <c>id</c>, …</summary>
    Attribute,

    /// <summary>A path inside an event's <c>data</c> payload.</summary>
    Data,

    /// <summary>A path inside a read-model document or aggregate state.</summary>
    Field,

    /// <summary>An output column by name (<c>ORDER BY total</c>).</summary>
    Output,
}

/// <summary>A column reference resolved by the binder.</summary>
/// <param name="Kind">What it refers to.</param>
/// <param name="Source">The source index (in the scope it was resolved in).</param>
/// <param name="Attribute">The canonical attribute name, for attributes.</param>
/// <param name="Path">The path below <c>data</c> or the document/state root (empty for whole <c>data</c>).</param>
/// <param name="Type">The type.</param>
/// <param name="PersonalData">True when any version of the field is <c>[PersonalData]</c>.</param>
/// <param name="Queryable">True when the field is <c>[Queryable]</c> in every version that has it.</param>
/// <param name="VersionPaths">
/// For event <c>data</c> paths: per event type and version, the JSON path in that version (renames applied), or null
/// when the field is missing in that version (doc 14 §5.4). Null for opaque <c>data</c>.
/// </param>
/// <param name="Output">The output index, for <see cref="ColumnKind.Output"/>.</param>
/// <param name="OuterDepth">0 for the query's own sources; 1 for an outer query's (correlated subqueries).</param>
public sealed record BoundColumn(
    ColumnKind Kind,
    int Source,
    string? Attribute,
    IReadOnlyList<object> Path,
    QueryType Type,
    bool PersonalData,
    bool Queryable,
    IReadOnlyDictionary<(string Type, int Version), IReadOnlyList<object>?>? VersionPaths,
    int Output = -1,
    int OuterDepth = 0);

/// <summary>An output column.</summary>
/// <param name="Name">The unique output name.</param>
/// <param name="Type">The type.</param>
/// <param name="Expression">The expression.</param>
/// <param name="PersonalData">True when the value is personal data (redacted without the PII permission, doc 14 §8).</param>
public sealed record BoundOutput(string Name, QueryType Type, Expr Expression, bool PersonalData);

/// <summary>Consistency levels (doc 14 §6).</summary>
public enum QueryConsistency
{
    /// <summary>The source default: <c>horizon</c> for events, <c>latest</c> for streams, <c>eventual</c> for read models.</summary>
    Default,

    /// <summary>Events below the safe horizon (monotonic pages).</summary>
    Horizon,

    /// <summary>Every committed event.</summary>
    Latest,

    /// <summary>Whatever the projection has applied.</summary>
    Eventual,

    /// <summary>Wait until read models pass a consistency token.</summary>
    AtLeast,
}

/// <summary>The query's <c>options (…)</c>, typed.</summary>
public sealed record BoundOptions
{
    /// <summary>The consistency level.</summary>
    public QueryConsistency Consistency { get; init; }

    /// <summary>The token expression for <see cref="QueryConsistency.AtLeast"/>.</summary>
    public Expr? Token { get; init; }

    /// <summary>The consistency wait timeout.</summary>
    public Expr? Timeout { get; init; }

    /// <summary>True for <c>on_timeout = 'stale'</c> (return stale results instead of failing).</summary>
    public bool StaleOnTimeout { get; init; }

    /// <summary>True for <c>allow_scan = true</c> (needs <c>sourceflow.query.scan</c>).</summary>
    public bool AllowScan { get; init; }

    /// <summary>True for <c>tenants = 'all'</c> (needs <c>sourceflow.query.cross-tenant</c>).</summary>
    public bool AllTenants { get; init; }
}

/// <summary>
/// A query bound to a service's catalog: every name resolved, every expression typed, output columns named, ordering
/// made total (G-6). The policy, the in-memory interpreter and the SQL compilers all work from this.
/// </summary>
public sealed class BoundQuery
{
    private readonly IReadOnlyDictionary<Expr, BoundColumn> _columns;
    private readonly IReadOnlyDictionary<Expr, QueryType> _types;
    private readonly IReadOnlyDictionary<QueryAst, BoundQuery> _subqueries;

    internal BoundQuery(
        QueryAst ast,
        QueryCatalog catalog,
        IReadOnlyList<BoundSource> sources,
        IReadOnlyList<BoundOutput> outputs,
        IReadOnlyList<SortItem> orderBy,
        bool grouped,
        IReadOnlyDictionary<string, QueryType> parameters,
        BoundOptions options,
        IReadOnlyDictionary<Expr, BoundColumn> columns,
        IReadOnlyDictionary<Expr, QueryType> types,
        IReadOnlyDictionary<QueryAst, BoundQuery> subqueries)
    {
        Ast = ast;
        Catalog = catalog;
        Sources = sources;
        Outputs = outputs;
        OrderBy = orderBy;
        Grouped = grouped;
        Parameters = parameters;
        Options = options;
        _columns = columns;
        _types = types;
        _subqueries = subqueries;
    }

    /// <summary>The parsed query.</summary>
    public QueryAst Ast { get; }

    /// <summary>The catalog it was bound against.</summary>
    public QueryCatalog Catalog { get; }

    /// <summary>The sources: the primary source, then joins.</summary>
    public IReadOnlyList<BoundSource> Sources { get; }

    /// <summary>The output columns.</summary>
    public IReadOnlyList<BoundOutput> Outputs { get; }

    /// <summary>The ordering, with the G-6 tie-break keys appended.</summary>
    public IReadOnlyList<SortItem> OrderBy { get; }

    /// <summary>True for grouped or aggregated queries.</summary>
    public bool Grouped { get; }

    /// <summary>The parameters and their inferred types.</summary>
    public IReadOnlyDictionary<string, QueryType> Parameters { get; }

    /// <summary>The typed options.</summary>
    public BoundOptions Options { get; }

    /// <summary>The resolution of a column expression.</summary>
    public BoundColumn Column(Expr column)
    {
        return _columns[column];
    }

    /// <summary>The resolution of a column expression, if it was bound.</summary>
    public bool TryGetColumn(Expr column, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BoundColumn? bound)
    {
        return _columns.TryGetValue(column, out bound);
    }

    /// <summary>The type of an expression.</summary>
    public QueryType TypeOf(Expr expression)
    {
        return _types.TryGetValue(expression, out QueryType type) ? type : QueryType.Any;
    }

    /// <summary>The bound form of a subquery.</summary>
    public BoundQuery Subquery(QueryAst subquery)
    {
        return _subqueries[subquery];
    }
}

/// <summary>The result of binding: the bound query, or the diagnostics that stopped it.</summary>
public sealed record BindResult(BoundQuery? Query, IReadOnlyList<QueryDiagnostic> Diagnostics)
{
    /// <summary>True when the query bound without errors.</summary>
    public bool Success => Query is not null && Diagnostics.Count == 0;
}

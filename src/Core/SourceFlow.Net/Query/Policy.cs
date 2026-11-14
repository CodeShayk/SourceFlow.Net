using System.Globalization;

namespace SourceFlow.Query;

/// <summary>The query permissions (doc 14 §8).</summary>
public static class QueryPermission
{
    /// <summary>Query the event log and streams.</summary>
    public const string Events = "sourceflow.query.events";

    /// <summary>Query read models.</summary>
    public const string ReadModels = "sourceflow.query.readmodels";

    /// <summary>Read aggregate state.</summary>
    public const string Aggregates = "sourceflow.query.aggregates";

    /// <summary>Read the catalog.</summary>
    public const string Schema = "sourceflow.query.schema";

    /// <summary>See personal data in clear text.</summary>
    public const string PersonalData = "sourceflow.query.pii";

    /// <summary>Override the cost guard with <c>allow_scan</c>.</summary>
    public const string Scan = "sourceflow.query.scan";

    /// <summary>Query every tenant with <c>tenants = 'all'</c>.</summary>
    public const string CrossTenant = "sourceflow.query.cross-tenant";

    /// <summary>Run ad-hoc queries through the query API.</summary>
    public const string Adhoc = "sourceflow.query.adhoc";

    /// <summary>Validate queries.</summary>
    public const string Validate = "sourceflow.query.validate";

    /// <summary>Explain queries (reveals the physical schema: operators only).</summary>
    public const string Explain = "sourceflow.query.explain";
}

/// <summary>The permissions a caller holds.</summary>
public sealed class QueryPermissions
{
    private readonly HashSet<string>? _granted;

    private QueryPermissions(HashSet<string>? granted)
    {
        _granted = granted;
    }

    /// <summary>The service's own identity (in-process calls): every permission.</summary>
    public static QueryPermissions Service { get; } = new(null);

    /// <summary>A caller with the given permissions.</summary>
    public static QueryPermissions Of(params string[] granted)
    {
        return new QueryPermissions(new HashSet<string>(granted, StringComparer.Ordinal));
    }

    /// <summary>True when the permission is held.</summary>
    public bool Has(string permission)
    {
        return _granted is null || _granted.Contains(permission);
    }
}

/// <summary>Cost-guard and limit settings (doc 14 §7.3, §9.4).</summary>
public sealed record QueryPolicySettings
{
    /// <summary>The default settings.</summary>
    public static QueryPolicySettings Default { get; } = new();

    /// <summary>The widest time range that bounds an events query (default 31 days).</summary>
    public TimeSpan MaxTimeRange { get; init; } = TimeSpan.FromDays(31);

    /// <summary>The request limits.</summary>
    public QueryLimits Limits { get; init; } = QueryLimits.Default;

    /// <summary>How long <c>consistency = at_least(@token)</c> waits when the query gives no <c>timeout</c> (default 5 s).</summary>
    public TimeSpan ConsistencyTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The longest consistency wait a query may ask for (default 30 s).</summary>
    public TimeSpan MaxConsistencyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The most result groups a grouped query may produce (default 10,000; SFQ0408).</summary>
    public int MaxGroups { get; init; } = 10_000;
}

/// <summary>
/// Policy checks after binding (doc 14 §7.3, §8): permissions (SFQ0501), personal data as keys (SFQ0301), the cost
/// guard for event sources (SFQ0401/0402), join rules (SFQ0405/0406), offset (SFQ0409) and row limits (SFQ0103).
/// Tenant isolation is applied by every executor (and again by PostgreSQL row-level security), not here.
/// </summary>
public static class SfqlPolicy
{
    /// <summary>Checks a bound query; returns the diagnostics (empty when allowed).</summary>
    /// <param name="query">The bound query.</param>
    /// <param name="text">The query text (for positions).</param>
    /// <param name="permissions">The caller's permissions.</param>
    /// <param name="parameters">Parameter values, when known (time-range bounds and limits use them).</param>
    /// <param name="settings">Settings; <see cref="QueryPolicySettings.Default"/> when null.</param>
    public static IReadOnlyList<QueryDiagnostic> Check(BoundQuery query, string text, QueryPermissions permissions, IReadOnlyDictionary<string, object?>? parameters = null, QueryPolicySettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(permissions);
        var diagnostics = new DiagnosticBag(text ?? string.Empty);
        new PolicyChecker(query, permissions, parameters ?? new Dictionary<string, object?>(), settings ?? QueryPolicySettings.Default, diagnostics).Run();
        return diagnostics.Items;
    }
}

// A subquery is checked like the query it sits in (doc 14 §8, G-12): its sources need their permissions, an events
// source is under the cost guard, and personal data can't be a key. Options (allow_scan, tenants) are the outer query's.
internal sealed class PolicyChecker(BoundQuery query, QueryPermissions permissions, IReadOnlyDictionary<string, object?> parameters, QueryPolicySettings settings, DiagnosticBag diagnostics, BoundQuery? outer = null)
{
    private BoundOptions Options => (outer ?? query).Options;

    public void Run()
    {
        int before = diagnostics.Items.Count;
        RunOwn();
        if (diagnostics.Items.Count > before)
        {
            return;
        }

        foreach (QueryAst subquery in Subqueries(query.Ast))
        {
            new PolicyChecker(query.Subquery(subquery), permissions, parameters, settings, diagnostics, outer ?? query).Run();
            if (diagnostics.Items.Count > before)
            {
                return;
            }
        }
    }

    private static IEnumerable<QueryAst> Subqueries(QueryAst ast)
    {
        var found = new List<QueryAst>();
        void Walk(Expr? e)
        {
            switch (e)
            {
                case null:
                    return;
                case InSubqueryExpr s:
                    found.Add(s.Subquery);
                    break;
                case ExistsSubqueryExpr s:
                    found.Add(s.Subquery);
                    break;
            }

            foreach (Expr child in Binder.Children(e))
            {
                Walk(child);
            }
        }

        Walk(ast.Where);
        Walk(ast.Having);
        return found;
    }

    private void RunOwn()
    {
        if (!Permissions() || !PersonalData())
        {
            return;
        }

        bool scan = Options.AllowScan && permissions.Has(QueryPermission.Scan);
        IReadOnlyList<Expr> conjuncts = Conjuncts(query.Ast.Where);
        var bounded = new Dictionary<int, bool>();
        foreach (BoundSource source in query.Sources.Where(s => s.Kind == SourceKind.Events))
        {
            bounded[source.Index] = IsBounded(source, conjuncts) || JoinBounded(source) || CorrelationBounded(source, conjuncts);
        }

        // G-8 first: a join of two event sources reports the join, not the scan.
        if (!scan && query.Sources.Count(s => s.Kind == SourceKind.Events) > 1 && query.Sources.Where(s => s.Kind == SourceKind.Events).Any(s => !IsBounded(s, conjuncts)))
        {
            Add(QueryCodes.UnboundedEventJoin, "Joining two event sources needs both bounded (G-8).", query.Ast.Joins[0].Span);
            return;
        }

        if (!scan)
        {
            foreach (BoundSource source in query.Sources.Where(s => s.Kind == SourceKind.Events && !bounded[s.Index]))
            {
                bool nonQueryableData = conjuncts.Any(c => Columns(c).Any(b => b.Source == source.Index && b.Kind == ColumnKind.Data && !b.Queryable));
                Add(nonQueryableData ? QueryCodes.NotQueryable : QueryCodes.UnboundedScan,
                    nonQueryableData
                        ? "This filters on a field that isn't [Queryable] with nothing that bounds the scan: add a type and time range, a stream, a correlation id or a [Queryable] equality (doc 14 §7.3)."
                        : "Unbounded scan of the event log: narrow it to event types with a time range of at most " + settings.MaxTimeRange.TotalDays.ToString(CultureInfo.InvariantCulture)
                          + " days, a stream, a correlation or causation id, has_tag, a position range or a [Queryable] equality (doc 14 §7.3).",
                    source.Syntax.Span);
                return;
            }
        }

        foreach (JoinClause join in query.Ast.Joins)
        {
            foreach (JoinEquality equality in join.On)
            {
                if (!query.Column(equality.Left).Queryable && !query.Column(equality.Right).Queryable && !scan)
                {
                    Add(QueryCodes.JoinNotIndexed, "A join condition needs an indexed key on at least one side: a read-model id, an event id, stream, aggregatekey, correlation or causation id, or a [Queryable] field (G-7).",
                        equality.Left.Span.Cover(equality.Right.Span));
                    return;
                }
            }
        }

        if (query.Ast.Offset is not null)
        {
            if (query.Sources.Any(s => s.IsEventLike))
            {
                Add(QueryCodes.Offset, "skip/OFFSET is only allowed when every source is a read model; page the event log with continuation tokens (doc 14 §7.4).", query.Ast.Offset.Span);
                return;
            }

            if (Value(query.Ast.Offset) is long offset && offset > settings.Limits.MaxOffset)
            {
                Add(QueryCodes.Offset, $"skip/OFFSET is at most {settings.Limits.MaxOffset}.", query.Ast.Offset.Span);
                return;
            }
        }

        if (query.Ast.Limit is not null && Value(query.Ast.Limit) is long limit && limit > settings.Limits.MaxRows)
        {
            Add(QueryCodes.Limit, $"take/LIMIT is at most {settings.Limits.MaxRows}.", query.Ast.Limit.Span);
        }
    }

    private bool Permissions()
    {
        foreach (BoundSource source in query.Sources)
        {
            string required = source.Kind switch
            {
                SourceKind.Events or SourceKind.Stream => QueryPermission.Events,
                SourceKind.ReadModel => QueryPermission.ReadModels,
                SourceKind.Aggregate => QueryPermission.Aggregates,
                _ => QueryPermission.Schema,
            };
            if (!permissions.Has(required))
            {
                Add(QueryCodes.PermissionDenied, $"Permission '{required}' is required.", source.Syntax.Span);
                return false;
            }
        }

        if (outer is not null)
        {
            return true;   // the options were checked on the outer query
        }

        if (query.Options.AllowScan && !permissions.Has(QueryPermission.Scan))
        {
            Add(QueryCodes.PermissionDenied, $"Permission '{QueryPermission.Scan}' is required for allow_scan.", query.Ast.Span);
            return false;
        }

        if (query.Options.AllTenants && !permissions.Has(QueryPermission.CrossTenant))
        {
            Add(QueryCodes.PermissionDenied, $"Permission '{QueryPermission.CrossTenant}' is required for tenants = 'all'.", query.Ast.Span);
            return false;
        }

        return true;
    }

    // SFQ0301: personal data (ciphertext) can't filter, sort, group or join (doc 14 §8).
    private bool PersonalData()
    {
        IEnumerable<Expr> keys =
        [
            .. query.Ast.Where is null ? [] : new[] { query.Ast.Where },
            .. query.Ast.Having is null ? [] : new[] { query.Ast.Having },
            .. query.Ast.GroupBy,
            .. query.Ast.OrderBy.Select(s => s.Expression),
            .. query.Ast.Joins.SelectMany(j => j.On.SelectMany(e => new Expr[] { e.Left, e.Right })),
        ];
        foreach (Expr key in keys)
        {
            foreach ((Expr column, BoundColumn bound) in ColumnNodes(key))
            {
                bool personal = bound.Kind == ColumnKind.Output ? query.Outputs[bound.Output].PersonalData : bound.PersonalData;
                if (personal && !InsideAggregateCount(key, column))
                {
                    Add(QueryCodes.PersonalData, "A personal-data field can't be used as a filter, sort, group or join key: it is stored encrypted (doc 14 §8).", column.Span);
                    return false;
                }
            }
        }

        return true;
    }

    // count(x) of a personal-data field reveals nothing; any other use does.
    private static bool InsideAggregateCount(Expr root, Expr column)
    {
        bool result = false;
        void Walk(Expr e, bool inCount)
        {
            if (ReferenceEquals(e, column))
            {
                result = inCount;
                return;
            }

            bool here = inCount || e is CallExpr { Name: "count" };
            foreach (Expr child in Binder.Children(e))
            {
                Walk(child, here);
            }
        }

        Walk(root, false);
        return result;
    }

    // ================================================================ cost guard

    private bool IsBounded(BoundSource source, IReadOnlyList<Expr> conjuncts)
    {
        TimeSpan? lower = null;
        DateTimeOffset? from = null;
        DateTimeOffset? to = null;
        bool position = false;
        foreach (Expr conjunct in conjuncts)
        {
            switch (conjunct)
            {
                case BinaryExpr { Operator: BinaryOperator.Equal } eq when KeyEquality(source, eq.Left, eq.Right) || KeyEquality(source, eq.Right, eq.Left):
                    return true;
                case InListExpr { Negated: false } list when IsKey(source, list.Value) && list.Items.All(IsValue):
                    return true;
                case CallExpr { Name: "has_tag" }:
                    return true;
                case BinaryExpr { Operator: BinaryOperator.GreaterOrEqual or BinaryOperator.Greater } ge when IsAttribute(source, ge.Left, "time"):
                    if (ge.Right is CallExpr { Name: "ago" } ago && Value(ago.Arguments[0]) is TimeSpan span)
                    {
                        lower = span;
                    }
                    else if (Timestamp(ge.Right) is { } start)
                    {
                        from = start;
                    }

                    break;
                case BinaryExpr { Operator: BinaryOperator.LessOrEqual or BinaryOperator.Less } le when IsAttribute(source, le.Left, "time"):
                    to = Timestamp(le.Right) ?? to;
                    break;
                case BetweenExpr { Negated: false } between when IsAttribute(source, between.Value, "time"):
                    from = Timestamp(between.Low) ?? from;
                    to = Timestamp(between.High) ?? to;
                    if (between.Low is ParameterExpr && between.High is ParameterExpr && !parameters.ContainsKey(((ParameterExpr)between.Low).Name))
                    {
                        // Validation without values: both bounds present; the range is checked again with values.
                        from ??= DateTimeOffset.MinValue;
                        to ??= DateTimeOffset.MinValue;
                    }

                    break;
                case BetweenExpr { Negated: false } between when IsAttribute(source, between.Value, "position"):
                    position = true;
                    break;
            }
        }

        if (position)
        {
            return true;
        }

        if (source.EventTypes.Count == 0)
        {
            return false;
        }

        if (lower is { } range)
        {
            return range <= settings.MaxTimeRange;
        }

        return from is { } f && to is { } t && t - f <= settings.MaxTimeRange;
    }

    // A joined events source looked up by an indexed key (Example H) is bounded by the other side.
    private bool JoinBounded(BoundSource source)
    {
        if (source.Index == 0)
        {
            return false;
        }

        JoinClause join = query.Ast.Joins[source.Index - 1];
        return join.On.Any(e => (query.Column(e.Left).Source == source.Index && query.Column(e.Left).Queryable)
            || (query.Column(e.Right).Source == source.Index && query.Column(e.Right).Queryable))
            && query.Sources[0].Kind != SourceKind.Events;
    }

    // A subquery's events source correlated to the outer query by an indexed key is a lookup, like a joined events
    // source (G-7, G-12): bounded by the outer rows, unless the outer source is itself events (G-8: each needs a bound).
    private bool CorrelationBounded(BoundSource source, IReadOnlyList<Expr> conjuncts)
    {
        if (outer is null)
        {
            return false;
        }

        return conjuncts.Any(c => c is BinaryExpr { Operator: BinaryOperator.Equal } eq
            && ((IsKey(source, eq.Left) && IsOuterLookup(eq.Right)) || (IsKey(source, eq.Right) && IsOuterLookup(eq.Left))));

        bool IsOuterLookup(Expr expr)
        {
            return query.TryGetColumn(expr, out BoundColumn? column) && column.OuterDepth == 1
                && column.Source < outer.Sources.Count && outer.Sources[column.Source].Kind != SourceKind.Events;
        }
    }

    private bool KeyEquality(BoundSource source, Expr key, Expr value)
    {
        return IsKey(source, key) && IsValue(value);
    }

    private bool IsKey(BoundSource source, Expr expr)
    {
        return query.TryGetColumn(expr, out BoundColumn? column) && column.Source == source.Index && column.OuterDepth == 0
            && ((column.Kind == ColumnKind.Attribute && column.Attribute is "stream" or "id" or "sfcorrelationid" or "sfcausationid" or "aggregatekey")
                || (column.Kind == ColumnKind.Data && column.Queryable));
    }

    private bool IsAttribute(BoundSource source, Expr expr, string attribute)
    {
        return query.TryGetColumn(expr, out BoundColumn? column) && column.Source == source.Index && column.Kind == ColumnKind.Attribute && column.Attribute == attribute;
    }

    private static bool IsValue(Expr expr)
    {
        return expr is LiteralExpr or ParameterExpr;
    }

    private object? Value(Expr expr)
    {
        return expr switch
        {
            LiteralExpr literal => literal.Value,
            ParameterExpr parameter when parameters.TryGetValue(parameter.Name, out object? value) => value switch
            {
                int i => (long)i,
                string s when TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out TimeSpan span) => span,
                _ => value,
            },
            _ => null,
        };
    }

    private DateTimeOffset? Timestamp(Expr expr)
    {
        return Value(expr) switch
        {
            DateTimeOffset d => d,
            DateTime d => new DateTimeOffset(d.ToUniversalTime()),
            string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset d) => d,
            _ => null,
        };
    }

    internal static IReadOnlyList<Expr> Conjuncts(Expr? where)
    {
        var parts = new List<Expr>();
        void Split(Expr? e)
        {
            if (e is BinaryExpr { Operator: BinaryOperator.And } and)
            {
                Split(and.Left);
                Split(and.Right);
            }
            else if (e is not null)
            {
                parts.Add(e);
            }
        }

        Split(where);
        return parts;
    }

    private IEnumerable<BoundColumn> Columns(Expr expr)
    {
        return ColumnNodes(expr).Select(c => c.Bound);
    }

    private IEnumerable<(Expr Column, BoundColumn Bound)> ColumnNodes(Expr expr)
    {
        if (query.TryGetColumn(expr, out BoundColumn? bound))
        {
            yield return (expr, bound);
        }

        foreach (Expr child in Binder.Children(expr))
        {
            foreach ((Expr, BoundColumn) nested in ColumnNodes(child))
            {
                yield return nested;
            }
        }
    }

    private void Add(string code, string message, TextSpan span)
    {
        diagnostics.Add(code, message, span);
    }
}

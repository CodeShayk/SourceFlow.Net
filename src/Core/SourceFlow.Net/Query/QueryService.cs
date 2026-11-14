using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SourceFlow.Runtime;
using SourceFlow.Storage;

namespace SourceFlow.Query;

/// <summary>Per-call query options (doc 14 §9.5).</summary>
public sealed record QueryOptions
{
    /// <summary>The syntax of the text (default pipeline SFQL).</summary>
    public QueryLanguage Language { get; init; } = QueryLanguage.Sfql;

    /// <summary>The tenant to query; null uses the ambient tenant (<see cref="ITenantResolver"/>).</summary>
    public string? Tenant { get; init; }

    /// <summary>The caller's permissions (default: the service identity, which holds every permission).</summary>
    public QueryPermissions Permissions { get; init; } = QueryPermissions.Service;

    /// <summary>Rows per page; null uses <see cref="QueryLimits.PageSize"/>. Capped at <see cref="QueryLimits.MaxRows"/>.</summary>
    public int? PageSize { get; init; }
}

/// <summary>A result column.</summary>
/// <param name="Name">The output name.</param>
/// <param name="Type">The type.</param>
public sealed record QueryColumn(string Name, QueryType Type);

/// <summary>
/// A result row. Values are <see cref="string"/>, <see cref="long"/>, <see cref="decimal"/>, <see cref="bool"/>,
/// <see cref="DateTimeOffset"/> (UTC), <see cref="TimeSpan"/>, <see cref="GlobalPosition"/>, a <see cref="string"/>
/// array (tags), a <see cref="JsonElement"/> (objects and arrays) or null.
/// </summary>
public sealed class QueryRow : IQueryRow
{
    private readonly object?[] _values;

    internal QueryRow(IReadOnlyList<QueryColumn> columns, object?[] values)
    {
        Columns = columns;
        _values = values;
    }

    /// <summary>The columns.</summary>
    public IReadOnlyList<QueryColumn> Columns { get; }

    /// <summary>The values, in column order.</summary>
    public IReadOnlyList<object?> Values => _values;

    /// <summary>The value of a column by index.</summary>
    public object? this[int index] => _values[index];

    /// <summary>The value of a column by name.</summary>
    public object? this[string name]
    {
        get
        {
            for (int i = 0; i < Columns.Count; i++)
            {
                if (string.Equals(Columns[i].Name, name, StringComparison.Ordinal))
                {
                    return _values[i];
                }
            }

            throw new KeyNotFoundException($"The result has no column '{name}'.");
        }
    }

    /// <inheritdoc />
    public bool TryGetColumn(string name, out object? value)
    {
        for (int i = 0; i < Columns.Count; i++)
        {
            if (string.Equals(Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = _values[i];
                return true;
            }
        }

        value = null;
        return false;
    }
}

/// <summary>The consistency applied to one source (doc 14 §6).</summary>
/// <param name="Source">The source, for example <c>readmodel.account-summary</c>.</param>
/// <param name="Level">The level: <c>horizon</c>, <c>latest</c>, <c>eventual</c>, <c>at_least</c> or <c>strong</c>.</param>
/// <param name="Stale">True when an <c>at_least</c> wait timed out with <c>on_timeout = 'stale'</c>.</param>
public sealed record QuerySourceConsistency(string Source, string Level, bool Stale);

/// <summary>One page of results.</summary>
/// <param name="Columns">The columns.</param>
/// <param name="Rows">The rows.</param>
/// <param name="Continuation">The token for the next page, or null on the last page.</param>
/// <param name="Consistency">The consistency applied to each source.</param>
/// <param name="QueryHash">The hash of the normalised query (<c>sha256:…</c>), for audit and plan caching.</param>
public sealed record QueryPage(
    IReadOnlyList<QueryColumn> Columns,
    IReadOnlyList<QueryRow> Rows,
    string? Continuation,
    IReadOnlyList<QuerySourceConsistency> Consistency,
    string QueryHash);

/// <summary>The result of validating a query without running it.</summary>
/// <param name="Diagnostics">Syntax, bind and policy diagnostics; empty when valid.</param>
/// <param name="Columns">The result columns, when the query bound.</param>
public sealed record QueryValidation(IReadOnlyList<QueryDiagnostic> Diagnostics, IReadOnlyList<QueryColumn> Columns)
{
    /// <summary>True when the query has no diagnostics.</summary>
    public bool IsValid => Diagnostics.Count == 0;
}

/// <summary>A query that was rejected or failed; carries diagnostics with positions, never the caller's text (S-3).</summary>
public sealed class QueryException : Exception
{
    /// <summary>Creates the exception.</summary>
    public QueryException(IReadOnlyList<QueryDiagnostic> diagnostics)
        : base(diagnostics.Count > 0 ? $"{diagnostics[0].Code}: {diagnostics[0].Message}" : "The query is not valid.")
    {
        Diagnostics = diagnostics;
    }

    /// <summary>Creates the exception for a single code.</summary>
    public QueryException(string code, string message)
        : this([new QueryDiagnostic(code, message, 0, 0, 0)])
    {
    }

    /// <summary>The diagnostics.</summary>
    public IReadOnlyList<QueryDiagnostic> Diagnostics { get; }

    /// <summary>The first diagnostic's code.</summary>
    public string Code => Diagnostics.Count > 0 ? Diagnostics[0].Code : QueryCodes.Syntax;
}

/// <summary>
/// Runs SFQL and SQL-syntax queries against this service's own data (doc 14 §9.5). Read side only: never use it inside
/// <c>Decide</c>, <c>When</c> or <c>React</c> (Q-R1, SF0015).
/// </summary>
public interface IQueryService
{
    /// <summary>Runs a query and returns one page.</summary>
    /// <param name="query">The query text.</param>
    /// <param name="parameters">Parameter values by name (without <c>@</c>).</param>
    /// <param name="continuation">The previous page's continuation token, or null for the first page.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    ValueTask<QueryPage> QueryPageAsync(string query, IReadOnlyDictionary<string, object?>? parameters = null, string? continuation = null, QueryOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Runs a query and streams every row, page by page.</summary>
    IAsyncEnumerable<QueryRow> QueryAsync(string query, IReadOnlyDictionary<string, object?>? parameters = null, QueryOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Parses, binds and checks a query without running it.</summary>
    ValueTask<QueryValidation> ValidateAsync(string query, IReadOnlyDictionary<string, object?>? parameters = null, QueryOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Runs a named domain query (doc 14 Q-B4) and returns its typed rows (every page).</summary>
    ValueTask<IReadOnlyList<TResult>> ExecuteAsync<TResult>(IDomainQuery<TResult> query, QueryOptions? options = null, CancellationToken cancellationToken = default)
        where TResult : class;

    /// <summary>Explains a query: binds, checks policy and shows the engine statement without running it (doc 14 §7.3).</summary>
    ValueTask<QueryPlan> ExplainAsync(string query, IReadOnlyDictionary<string, object?>? parameters = null, QueryOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Starts a typed query over an event type's payloads (doc 14 §9.5).</summary>
    EventQuery<TEvent> Events<TEvent>()
        where TEvent : class, IEvent;

    /// <summary>Starts a typed query over the read model whose document is <typeparamref name="TDocument"/>.</summary>
    ReadModelQuery<TDocument> ReadModel<TDocument>()
        where TDocument : class;
}

/// <summary>What a query engine runs: a bound, policy-checked query with converted parameters.</summary>
/// <param name="Query">The bound query.</param>
/// <param name="Parameters">Parameter values, converted to query values.</param>
/// <param name="Tenant">The tenant every source is filtered to (unless <c>tenants = 'all'</c>).</param>
/// <param name="Now">The query's <c>now()</c>.</param>
/// <param name="After">The keyset: return only rows whose sort keys come after these (null for the first page).</param>
/// <param name="Skip">Rows to skip after the keyset (<c>skip</c>/<c>OFFSET</c>, first page only).</param>
/// <param name="Take">The most rows to return.</param>
/// <param name="Settings">The policy settings (run-time limits such as <see cref="QueryPolicySettings.MaxGroups"/>).</param>
public sealed record QueryExecution(
    BoundQuery Query,
    IReadOnlyDictionary<string, object?> Parameters,
    string Tenant,
    DateTimeOffset Now,
    IReadOnlyList<object?>? After,
    int Skip,
    int Take,
    QueryPolicySettings Settings)
{
    /// <summary>
    /// The collection serving each read model (by name) for this execution: the alias target of a projection rebuilt
    /// blue/green (doc 06 §6). Read models not listed use <see cref="QueryReadModel.Collection"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Collections { get; init; }

    /// <summary>The collection to read for <paramref name="readModel"/>.</summary>
    public string CollectionOf(QueryReadModel readModel)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        return Collections is not null && Collections.TryGetValue(readModel.Name, out string? collection) ? collection : readModel.Collection;
    }
}

/// <summary>An explained query (doc 14 §7.3): what would run, without running it.</summary>
/// <param name="Columns">The result columns.</param>
/// <param name="Sources">The consistency each source would use.</param>
/// <param name="Engine">The engine that would run it (<c>in-memory</c>, <c>postgresql</c>, …).</param>
/// <param name="Statement">The engine's statement with placeholders only (SQL engines), or null.</param>
/// <param name="ParameterCount">The number of bound parameters.</param>
/// <param name="QueryHash">The query hash.</param>
public sealed record QueryPlan(
    IReadOnlyList<QueryColumn> Columns,
    IReadOnlyList<QuerySourceConsistency> Sources,
    string Engine,
    string? Statement,
    int ParameterCount,
    string QueryHash);

/// <summary>An engine that can show its statement for a query without running it (<c>/query/explain</c>).</summary>
public interface IQueryExplainer
{
    /// <summary>The engine's name, for example <c>postgresql</c>.</summary>
    string EngineName { get; }

    /// <summary>The statement for <paramref name="execution"/>, with placeholders only (never values), and its parameter count.</summary>
    (string Statement, int ParameterCount) Explain(QueryExecution execution);
}

/// <summary>A row from a query engine: its output values and its sort-key values (the keyset of the next page).</summary>
public sealed record QueryResultRow(IReadOnlyList<object?> Values, IReadOnlyList<object?> SortKeys);

/// <summary>
/// A storage provider's query executor (SPI): the SQL compilers in the storage packages implement it (doc 14 §7.1).
/// Without one, queries run in the in-memory interpreter over the storage ports.
/// </summary>
public interface IQueryEngine
{
    /// <summary>True when this engine can run <paramref name="query"/>.</summary>
    bool CanExecute(BoundQuery query);

    /// <summary>Runs the query: rows in sort order, after the keyset, skipped and limited.</summary>
    IAsyncEnumerable<QueryResultRow> ExecuteAsync(QueryExecution execution, CancellationToken cancellationToken);
}

/// <summary>The in-memory interpreter as a query engine: the reference semantics.</summary>
internal sealed class InterpreterQueryEngine(SourceFlowRuntime runtime) : IQueryEngine
{
    public bool CanExecute(BoundQuery query)
    {
        return true;
    }

    public async IAsyncEnumerable<QueryResultRow> ExecuteAsync(QueryExecution execution, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var interpreter = new Interpreter(execution.Query, new QueryEnvironment(runtime, execution.Tenant, execution.Now, execution.Settings.MaxGroups) { Collections = execution.Collections }, execution.Parameters);
        List<InterpretedRow> rows = await interpreter.RunAsync(cancellationToken).ConfigureAwait(false);
        object?[]? after = execution.After is null ? null : [.. execution.After];
        int skipped = 0;
        int returned = 0;
        foreach (InterpretedRow row in rows)
        {
            if (returned >= execution.Take)
            {
                yield break;
            }

            if (after is not null && interpreter.CompareKeys(row.SortKeys, after) <= 0)
            {
                continue;
            }

            if (skipped < execution.Skip)
            {
                skipped++;
                continue;
            }

            returned++;
            yield return new QueryResultRow(row.Outputs, row.SortKeys);
        }
    }
}

/// <summary>
/// The query pipeline (doc 14 §7.1): request guard and parser, binder, parameter conversion, policy, consistency wait,
/// execution through a provider engine or the interpreter, PII redaction and keyset paging.
/// </summary>
internal sealed class QueryService : IQueryService
{
    private const int PlanCacheSize = 1024;
    private const string Redacted = "[redacted]";

    private readonly SourceFlowRuntime _runtime;
    private readonly IReadOnlyList<IQueryEngine> _engines;
    private readonly QueryPolicySettings _settings;
    private readonly IQueryEngine _interpreter;
    private readonly Lazy<QueryCatalog> _catalog;
    private readonly ConcurrentDictionary<(QueryLanguage Language, string Text), Plan> _plans = new();

    public QueryService(SourceFlowRuntime runtime, IEnumerable<IQueryEngine> engines, QueryPolicySettings? settings = null)
    {
        _runtime = runtime;
        _engines = [.. engines];
        _settings = settings ?? QueryPolicySettings.Default;
        _interpreter = new InterpreterQueryEngine(runtime);
        _catalog = new Lazy<QueryCatalog>(() => QueryCatalog.Build(runtime.Model));
    }

    public async ValueTask<QueryPage> QueryPageAsync(string query, IReadOnlyDictionary<string, object?>? parameters = null, string? continuation = null, QueryOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new QueryOptions();
        Plan plan = PlanFor(query, options.Language);
        IReadOnlyDictionary<string, object?> values = Convert(plan.Query, parameters);
        IReadOnlyList<QueryDiagnostic> policy = SfqlPolicy.Check(plan.Query, query, options.Permissions, values, _settings);
        if (policy.Count > 0)
        {
            throw new QueryException(policy);
        }

        Cursor? cursor = continuation is null ? null : Cursor.Decode(continuation, plan.Hash, plan.Query.Catalog.Fingerprint);
        string tenant = options.Tenant ?? _runtime.CurrentTenant;
        DateTimeOffset now = _runtime.Time.GetUtcNow();
        var probe = new Interpreter(plan.Query, new QueryEnvironment(_runtime, tenant, now, _settings.MaxGroups), values);
        IReadOnlyDictionary<string, string> collections = await CollectionsAsync(plan.Query, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<QuerySourceConsistency> consistency = await WaitAsync(plan.Query, probe, collections, cancellationToken).ConfigureAwait(false);

        int limit = Count(probe, plan.Query.Ast.Limit) ?? int.MaxValue;
        cursor?.Validate(plan.Query, limit);
        int remaining = cursor?.Remaining ?? limit;
        int pageSize = Math.Clamp(options.PageSize ?? _settings.Limits.PageSize, 1, Math.Max(1, _settings.Limits.MaxRows));
        int take = Math.Min(pageSize, remaining);
        int skip = cursor is null ? Count(probe, plan.Query.Ast.Offset) ?? 0 : 0;

        var execution = new QueryExecution(plan.Query, values, tenant, now, cursor?.Keys, skip, take + 1, _settings) { Collections = collections };
        IQueryEngine engine = _engines.FirstOrDefault(e => e.CanExecute(plan.Query)) ?? _interpreter;
        var rows = new List<QueryRow>();
        IReadOnlyList<object?>? lastKeys = null;
        bool more = false;
        await foreach (QueryResultRow row in engine.ExecuteAsync(execution, cancellationToken).ConfigureAwait(false))
        {
            if (rows.Count == take)
            {
                more = true;
                break;
            }

            rows.Add(new QueryRow(plan.Columns, Redact(plan.Query, row.Values, options.Permissions)));
            lastKeys = row.SortKeys;
        }

        string? next = more && remaining - take > 0 && lastKeys is not null
            ? new Cursor(plan.Hash, plan.Query.Catalog.Fingerprint, remaining == int.MaxValue ? null : remaining - take, lastKeys).Encode()
            : null;
        return new QueryPage(plan.Columns, rows, next, consistency, plan.Hash);
    }

    public async IAsyncEnumerable<QueryRow> QueryAsync(string query, IReadOnlyDictionary<string, object?>? parameters = null, QueryOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? continuation = null;
        do
        {
            QueryPage page = await QueryPageAsync(query, parameters, continuation, options, cancellationToken).ConfigureAwait(false);
            foreach (QueryRow row in page.Rows)
            {
                yield return row;
            }

            continuation = page.Continuation;
        }
        while (continuation is not null);
    }

    public async ValueTask<IReadOnlyList<TResult>> ExecuteAsync<TResult>(IDomainQuery<TResult> query, QueryOptions? options = null, CancellationToken cancellationToken = default)
        where TResult : class
    {
        ArgumentNullException.ThrowIfNull(query);
        DomainQueryDescriptor descriptor = _runtime.Model.DomainQueries.FirstOrDefault(q => q.QueryType == query.GetType())
            ?? throw new DomainQueryException($"'{query.GetType().Name}' is not a registered domain query; add [DomainQuery] and include its module.");
        return (IReadOnlyList<TResult>)await descriptor.ExecuteAsync(this, query, options, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<QueryPlan> ExplainAsync(string query, IReadOnlyDictionary<string, object?>? parameters = null, QueryOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new QueryOptions();
        Plan plan = PlanFor(query, options.Language);
        IReadOnlyDictionary<string, object?> values = Convert(plan.Query, parameters);
        IReadOnlyList<QueryDiagnostic> policy = SfqlPolicy.Check(plan.Query, query, options.Permissions, values, _settings);
        if (policy.Count > 0)
        {
            throw new QueryException(policy);
        }

        string tenant = options.Tenant ?? _runtime.CurrentTenant;
        var execution = new QueryExecution(plan.Query, values, tenant, _runtime.Time.GetUtcNow(), null, 0,
            Math.Clamp(options.PageSize ?? _settings.Limits.PageSize, 1, Math.Max(1, _settings.Limits.MaxRows)) + 1, _settings)
        {
            Collections = await CollectionsAsync(plan.Query, cancellationToken).ConfigureAwait(false),
        };
        IQueryEngine? engine = _engines.FirstOrDefault(e => e.CanExecute(plan.Query));
        IReadOnlyList<QuerySourceConsistency> sources = [.. plan.Query.Sources.Select(s => new QuerySourceConsistency(SourceLabel(s), Level(s, plan.Query.Options.Consistency), false))];
        if (engine is IQueryExplainer explainer)
        {
            (string statement, int count) = explainer.Explain(execution);
            return new QueryPlan(plan.Columns, sources, explainer.EngineName, statement, count, plan.Hash);
        }

        return new QueryPlan(plan.Columns, sources, "in-memory", null, values.Count, plan.Hash);
    }

    public EventQuery<TEvent> Events<TEvent>()
        where TEvent : class, IEvent
    {
        return new EventQuery<TEvent>(this, _runtime.Model.Registry.Get(typeof(TEvent)));
    }

    public ReadModelQuery<TDocument> ReadModel<TDocument>()
        where TDocument : class
    {
        DocumentProjectionDescriptor projection = _runtime.Model.Projections.OfType<DocumentProjectionDescriptor>().FirstOrDefault(p => p.DocumentType == typeof(TDocument))
            ?? throw new InvalidOperationException($"No document projection maintains '{typeof(TDocument).Name}'.");
        return new ReadModelQuery<TDocument>(this, projection.Name, _runtime.Model.JsonTypeInfoFor(typeof(TDocument)));
    }

    /// <summary>
    /// Binds a domain query against this service's catalog and checks its contract (SF0018 at startup): the text binds,
    /// every parameter is a member, and every result column is an output.
    /// </summary>
    internal IReadOnlyList<string> CheckDomainQuery(DomainQueryDescriptor descriptor)
    {
        var problems = new List<string>();
        Plan plan;
        try
        {
            plan = PlanFor(descriptor.Text, descriptor.Language);
        }
        catch (QueryException e)
        {
            problems.AddRange(e.Diagnostics.Select(d => $"{d.Code} at {d.Line}:{d.Column}: {d.Message}"));
            return problems;
        }

        var members = new HashSet<string>(descriptor.Parameters.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        problems.AddRange(plan.Query.Parameters.Keys.Where(p => !members.Contains(p)).Select(p => $"@{p} is not a member of {descriptor.QueryType.Name}."));
        var outputs = new HashSet<string>(plan.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        problems.AddRange(descriptor.ResultColumns.Where(c => !outputs.Contains(c)).Select(c => $"The result member '{c}' of {descriptor.ResultType.Name} is not an output column."));
        return problems;
    }

    public ValueTask<QueryValidation> ValidateAsync(string query, IReadOnlyDictionary<string, object?>? parameters = null, QueryOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new QueryOptions();
        try
        {
            Plan plan = PlanFor(query, options.Language);
            IReadOnlyDictionary<string, object?> values = parameters is null ? new Dictionary<string, object?>() : Convert(plan.Query, parameters);
            return ValueTask.FromResult(new QueryValidation(SfqlPolicy.Check(plan.Query, query, options.Permissions, values, _settings), plan.Columns));
        }
        catch (QueryException e)
        {
            return ValueTask.FromResult(new QueryValidation(e.Diagnostics, []));
        }
    }

    // ================================================================ planning

    private Plan PlanFor(string text, QueryLanguage language)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_plans.TryGetValue((language, text), out Plan? cached))
        {
            return cached;
        }

        ParseResult parsed = SfqlParser.Parse(text, language, _settings.Limits);
        if (parsed.Query is null || parsed.Diagnostics.Count > 0)
        {
            throw new QueryException(parsed.Diagnostics);
        }

        BindResult bound = SfqlBinder.Bind(parsed.Query, text, _catalog.Value);
        if (!bound.Success)
        {
            throw new QueryException(bound.Diagnostics);
        }

        BoundQuery query = bound.Query!;
        string hash = "sha256:" + System.Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(query.Ast.ToString())));
        var plan = new Plan(query, [.. query.Outputs.Select(o => new QueryColumn(o.Name, o.Type))], hash);
        if (_plans.Count >= PlanCacheSize)
        {
            _plans.Clear();
        }

        _plans[(language, text)] = plan;
        return plan;
    }

    private static int? Count(Interpreter probe, Expr? expression)
    {
        if (expression is null)
        {
            return null;
        }

        return probe.Evaluate(expression, new Frame(null, null, null, null)) switch
        {
            long n when n >= 0 => (int)Math.Min(n, int.MaxValue),
            _ => throw new QueryException(QueryCodes.TypeMismatch, "take/skip needs a non-negative integer."),
        };
    }

    private object?[] Redact(BoundQuery query, IReadOnlyList<object?> values, QueryPermissions permissions)
    {
        object?[] result = [.. values];
        if (!permissions.Has(QueryPermission.PersonalData))
        {
            for (int i = 0; i < result.Length; i++)
            {
                if (query.Outputs[i].PersonalData && result[i] is not null)
                {
                    result[i] = Redacted;
                }
            }
        }

        return result;
    }

    // ================================================================ parameters (SFQ0203)

    private static Dictionary<string, object?> Convert(BoundQuery query, IReadOnlyDictionary<string, object?>? supplied)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var diagnostics = new List<QueryDiagnostic>();
        foreach ((string name, QueryType type) in query.Parameters)
        {
            if (supplied is null || !supplied.TryGetValue(name, out object? raw))
            {
                diagnostics.Add(new QueryDiagnostic(QueryCodes.UnknownName, $"Parameter @{name} has no value.", 0, 0, 0));
                continue;
            }

            if (TryConvert(raw, type, out object? value))
            {
                values[name] = value;
            }
            else
            {
                diagnostics.Add(new QueryDiagnostic(QueryCodes.TypeMismatch, $"Parameter @{name} is not a valid {type.ToString().ToLowerInvariant()}.", 0, 0, 0));
            }
        }

        return diagnostics.Count > 0 ? throw new QueryException(diagnostics) : values;
    }

    internal static bool TryConvert(object? raw, QueryType type, out object? value)
    {
        value = Normalize(raw);
        if (value is null)
        {
            return true;
        }

        switch (type)
        {
            case QueryType.Integer:
                if (value is string si && long.TryParse(si, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedLong))
                {
                    value = parsedLong;
                }

                return value is long;
            case QueryType.Decimal:
                if (value is string sd && decimal.TryParse(sd, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsedDecimal))
                {
                    value = parsedDecimal;
                }

                return value is long or decimal;
            case QueryType.Boolean:
                if (value is string sb && bool.TryParse(sb, out bool parsedBool))
                {
                    value = parsedBool;
                }

                return value is bool;
            case QueryType.Timestamp:
                if (value is string st && DateTimeOffset.TryParse(st, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsedTime))
                {
                    value = parsedTime.ToUniversalTime();
                }

                return value is DateTimeOffset;
            case QueryType.Uuid:
                if (value is string su && Guid.TryParse(su, out Guid parsedGuid))
                {
                    value = parsedGuid.ToString("D");
                    return true;
                }

                return false;
            case QueryType.Duration:
                if (value is string sp && TimeSpan.TryParse(sp, CultureInfo.InvariantCulture, out TimeSpan parsedSpan))
                {
                    value = parsedSpan;
                }

                return value is TimeSpan;
            case QueryType.Position:
                if (value is string sg && GlobalPosition.TryParse(sg, out GlobalPosition parsedPosition))
                {
                    value = parsedPosition;
                }

                return value is GlobalPosition;
            case QueryType.String:
                return value is string;
            default:
                return true;
        }
    }

    private static object? Normalize(object? value)
    {
        return value switch
        {
            null => null,
            string or long or decimal or bool or TimeSpan or GlobalPosition => value,
            int i => (long)i,
            short s => (long)s,
            byte b => (long)b,
            uint u => (long)u,
            ulong u when u <= long.MaxValue => (long)u,
            double d => (decimal)d,
            float f => (decimal)f,
            DateTimeOffset t => t.ToUniversalTime(),
            DateTime t => new DateTimeOffset(t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime()),
            Guid g => g.ToString("D"),
            ConsistencyToken token => token.ToString(),
            Enum e => e.ToString(),
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            JsonElement { ValueKind: JsonValueKind.Number } e => e.TryGetInt64(out long l) ? l : e.GetDecimal(),
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.Null } => null,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }

    // ================================================================ consistency (doc 14 §6)

    // The serving generation of every read model in the catalog (aliases, doc 06 §6); subqueries read the same map.
    private async ValueTask<IReadOnlyDictionary<string, string>> CollectionsAsync(BoundQuery query, CancellationToken cancellationToken)
    {
        var collections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (QueryReadModel model in query.Catalog.ReadModels)
        {
            collections[model.Name] = await _runtime.Generations.ReadCollectionAsync(model.Name, model.Collection, cancellationToken).ConfigureAwait(false);
        }

        return collections;
    }

    private async ValueTask<IReadOnlyList<QuerySourceConsistency>> WaitAsync(BoundQuery query, Interpreter probe, IReadOnlyDictionary<string, string> collections, CancellationToken cancellationToken)
    {
        BoundOptions options = query.Options;
        var stale = new HashSet<int>();
        if (options.Consistency == QueryConsistency.AtLeast)
        {
            object? raw = options.Token is null ? null : probe.Evaluate(options.Token, new Frame(null, null, null, null));
            if (!ConsistencyToken.TryParse(raw as string, out ConsistencyToken token))
            {
                throw new QueryException(QueryCodes.TypeMismatch, "consistency = at_least needs a consistency token.");
            }

            TimeSpan timeout = options.Timeout is null
                ? _settings.ConsistencyTimeout
                : probe.Evaluate(options.Timeout, new Frame(null, null, null, null)) as TimeSpan? ?? _settings.ConsistencyTimeout;
            if (timeout > _settings.MaxConsistencyTimeout)
            {
                timeout = _settings.MaxConsistencyTimeout;
            }

            long started = Stopwatch.GetTimestamp();
            foreach (BoundSource source in query.Sources.Where(s => s.Kind == SourceKind.ReadModel))
            {
                ProjectionDescriptor? projection = _runtime.AsyncProjections.FirstOrDefault(p => p.Name == source.ReadModel!.Name);
                if (projection is null)
                {
                    continue;   // inline projections satisfy any token immediately
                }

                // The generation serving reads: its subscription must reach the token (doc 06 §6).
                string collection = collections.GetValueOrDefault(source.ReadModel!.Name, source.ReadModel!.Collection);
                while (!await CaughtUpAsync("projection:" + collection, token.Position, cancellationToken).ConfigureAwait(false))
                {
                    if (Stopwatch.GetElapsedTime(started) >= timeout)
                    {
                        if (!options.StaleOnTimeout)
                        {
                            throw new QueryException(QueryCodes.ConsistencyTimeout, $"The read model {DiagnosticBag.Token(source.ReadModel!.Name)} did not reach the consistency token in time.");
                        }

                        stale.Add(source.Index);
                        break;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return [.. query.Sources.Select(s => new QuerySourceConsistency(SourceLabel(s), Level(s, options.Consistency), stale.Contains(s.Index)))];
    }

    private async ValueTask<bool> CaughtUpAsync(string subscription, GlobalPosition position, CancellationToken cancellationToken)
    {
        int partitions = _runtime.Options.SubscriptionPartitions;
        for (int partition = 0; partition < partitions; partition++)
        {
            GlobalPosition checkpoint = await _runtime.Storage.Checkpoints.GetAsync(subscription, partition, cancellationToken).ConfigureAwait(false);
            if (checkpoint.CompareTo(position) >= 0)
            {
                continue;
            }

            // Behind the token, but caught up if it has nothing to apply at or before the token in its buckets.
            LogBatch next = await _runtime.Storage.Log.ReadAsync(checkpoint, Buckets.Range(partition, partitions), 1, cancellationToken).ConfigureAwait(false);
            if (next.Events.Count > 0 && next.Events[0].Position.CompareTo(position) <= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static string SourceLabel(BoundSource source)
    {
        return source.Kind switch
        {
            SourceKind.Events => "events",
            SourceKind.Stream => "stream",
            SourceKind.ReadModel => "readmodel." + source.ReadModel!.Name,
            SourceKind.Aggregate => "aggregate." + source.Aggregate!.Name,
            _ => "schema",
        };
    }

    private static string Level(BoundSource source, QueryConsistency requested)
    {
        return source.Kind switch
        {
            SourceKind.Events => requested == QueryConsistency.Latest ? "latest" : "horizon",
            SourceKind.Stream => "latest",
            SourceKind.ReadModel => requested == QueryConsistency.AtLeast ? "at_least" : "eventual",
            _ => "strong",
        };
    }

    private sealed record Plan(BoundQuery Query, IReadOnlyList<QueryColumn> Columns, string Hash);

    // ================================================================ continuation tokens (doc 14 §7.4)

    /// <summary>A keyset cursor: base64url JSON of the query hash, catalog fingerprint, rows remaining and typed sort keys.</summary>
    private sealed record Cursor(string Hash, string Fingerprint, int? Remaining, IReadOnlyList<object?> Keys)
    {
        public string Encode()
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("v", 1);
                writer.WriteString("h", Hash);
                writer.WriteString("f", Fingerprint);
                if (Remaining is { } remaining)
                {
                    writer.WriteNumber("r", remaining);
                }

                writer.WriteStartArray("k");
                foreach (object? key in Keys)
                {
                    writer.WriteStartArray();
                    switch (key)
                    {
                        case null:
                            writer.WriteStringValue("n");
                            break;
                        case long l:
                            writer.WriteStringValue("l");
                            writer.WriteStringValue(l.ToString(CultureInfo.InvariantCulture));
                            break;
                        case decimal d:
                            writer.WriteStringValue("d");
                            writer.WriteStringValue(d.ToString(CultureInfo.InvariantCulture));
                            break;
                        case bool b:
                            writer.WriteStringValue("b");
                            writer.WriteBooleanValue(b);
                            break;
                        case DateTimeOffset t:
                            writer.WriteStringValue("t");
                            writer.WriteStringValue(t.UtcTicks.ToString(CultureInfo.InvariantCulture));
                            break;
                        case TimeSpan t:
                            writer.WriteStringValue("u");
                            writer.WriteStringValue(t.Ticks.ToString(CultureInfo.InvariantCulture));
                            break;
                        case GlobalPosition p:
                            writer.WriteStringValue("p");
                            writer.WriteStringValue(p.ToString());
                            break;
                        case string s:
                            writer.WriteStringValue("s");
                            writer.WriteStringValue(s);
                            break;
                        default:
                            if (QueryValue.IsNull(key))
                            {
                                writer.WriteStringValue("n");
                                break;
                            }

                            throw new QueryException(QueryCodes.Continuation, "A sort key cannot be carried in a continuation token.");
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            return System.Buffers.Text.Base64Url.EncodeToString(buffer.WrittenSpan);
        }

        public static Cursor Decode(string token, string hash, string fingerprint)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(System.Buffers.Text.Base64Url.DecodeFromChars(token));
                JsonElement root = document.RootElement;
                if (root.GetProperty("v").GetInt32() != 1
                    || root.GetProperty("h").GetString() != hash
                    || root.GetProperty("f").GetString() != fingerprint)
                {
                    throw Invalid();
                }

                int? remaining = root.TryGetProperty("r", out JsonElement r) ? r.GetInt32() : null;
                var keys = new List<object?>();
                foreach (JsonElement key in root.GetProperty("k").EnumerateArray())
                {
                    string tag = key[0].GetString()!;
                    keys.Add(tag switch
                    {
                        "n" => null,
                        "l" => long.Parse(key[1].GetString()!, NumberStyles.Integer, CultureInfo.InvariantCulture),
                        "d" => decimal.Parse(key[1].GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture),
                        "b" => key[1].GetBoolean(),
                        "t" => new DateTimeOffset(long.Parse(key[1].GetString()!, NumberStyles.Integer, CultureInfo.InvariantCulture), TimeSpan.Zero),
                        "u" => new TimeSpan(long.Parse(key[1].GetString()!, NumberStyles.Integer, CultureInfo.InvariantCulture)),
                        "p" => GlobalPosition.TryParse(key[1].GetString(), out GlobalPosition p) ? p : throw Invalid(),
                        "s" => key[1].GetString(),
                        _ => throw Invalid(),
                    });
                }

                return new Cursor(hash, fingerprint, remaining, keys);
            }
            catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException or OverflowException)
            {
                throw Invalid();
            }
        }

        /// <summary>
        /// A token is caller input (doc 14 §7.4, §12): it must fit the plan before the engines see it. One key per sort item,
        /// each of its sort item's type, and the rows remaining present and within the query's limit, so a forged token
        /// can't lift the limit or reach a compiler with keys of the wrong shape (a database error, CR-36).
        /// </summary>
        public void Validate(BoundQuery query, int limit)
        {
            if (Keys.Count != query.OrderBy.Count)
            {
                throw Invalid();
            }

            if (limit != int.MaxValue && (Remaining is not { } remaining || remaining <= 0 || remaining > limit))
            {
                throw Invalid();
            }

            for (int i = 0; i < Keys.Count; i++)
            {
                if (Keys[i] is { } key && !Fits(query.TypeOf(query.OrderBy[i].Expression), key))
                {
                    throw Invalid();
                }
            }
        }

        private static bool Fits(QueryType type, object key)
        {
            return type switch
            {
                QueryType.String or QueryType.Uuid => key is string,
                QueryType.Integer or QueryType.Decimal => key is long or decimal,
                QueryType.Boolean => key is bool,
                QueryType.Timestamp => key is DateTimeOffset,
                QueryType.Duration => key is TimeSpan,
                QueryType.Position => key is GlobalPosition,
                _ => true,   // dynamic or structured values: the engines compare them as they come
            };
        }

        private static QueryException Invalid()
        {
            return new QueryException(QueryCodes.Continuation, "The continuation token is not valid for this query or schema.");
        }
    }
}

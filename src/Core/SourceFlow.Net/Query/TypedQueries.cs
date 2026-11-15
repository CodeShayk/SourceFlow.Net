using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using SourceFlow.Messaging;
using SourceFlow.Runtime;

namespace SourceFlow.Query;

/// <summary>An event row returned by the typed builder: the typed payload with its envelope attributes.</summary>
/// <typeparam name="TEvent">The event type.</typeparam>
/// <param name="Data">The payload.</param>
/// <param name="Stream">The stream.</param>
/// <param name="Version">The stream version.</param>
/// <param name="Time">The decision time.</param>
/// <param name="Position">The global position.</param>
public sealed record EventRecord<TEvent>(TEvent Data, string Stream, long Version, DateTimeOffset Time, GlobalPosition Position)
    where TEvent : class, IEvent;

/// <summary>
/// The typed query builder (doc 14 §9.5): a restricted expression subset translated into SFQL text with every value as a
/// parameter, then run through the same parser, binder and policy as any query (no <c>IQueryable</c> provider, no
/// expression compilation). Supported: member comparisons (<c>== != &lt; &lt;= &gt; &gt;=</c>), <c>&amp;&amp;</c>,
/// <c>||</c>, <c>!</c>, <c>== null</c>, <c>string.StartsWith/EndsWith/Contains</c>, constants and captured values.
/// </summary>
/// <typeparam name="TRow">The row type the builder returns.</typeparam>
/// <typeparam name="TSource">The type the lambdas range over.</typeparam>
public abstract class TypedQuery<TRow, TSource>
    where TRow : class
{
    private protected readonly QueryService _service;
    private protected readonly JsonTypeInfo _info;
    private protected readonly List<string> _where = [];
    private protected readonly List<string> _order = [];
    private protected readonly Dictionary<string, object?> _parameters = new(StringComparer.Ordinal);
    private protected string? _take;
    private protected string? _skip;

    private protected TypedQuery(QueryService service, JsonTypeInfo info)
    {
        _service = service;
        _info = info;
    }

    /// <summary>The prefix of a field path in the query (<c>data.</c> for events, empty for documents).</summary>
    private protected abstract string FieldPrefix { get; }

    /// <summary>Adds a filter.</summary>
    public TypedQuery<TRow, TSource> Where(Expression<Func<TSource, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _where.Add(Translate(predicate.Body, predicate.Parameters[0]));
        return this;
    }

    /// <summary>Orders by a member, ascending.</summary>
    public TypedQuery<TRow, TSource> OrderBy<TKey>(Expression<Func<TSource, TKey>> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _order.Add(Path(key.Body, key.Parameters[0]));
        return this;
    }

    /// <summary>Orders by a member, descending.</summary>
    public TypedQuery<TRow, TSource> OrderByDescending<TKey>(Expression<Func<TSource, TKey>> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _order.Add(Path(key.Body, key.Parameters[0]) + " desc");
        return this;
    }

    /// <summary>Returns at most <paramref name="count"/> rows.</summary>
    public TypedQuery<TRow, TSource> Take(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _take = Parameter((long)count);
        return this;
    }

    /// <summary>Skips rows (read models only; SFQ0409 otherwise).</summary>
    public TypedQuery<TRow, TSource> Skip(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _skip = Parameter((long)count);
        return this;
    }

    /// <summary>The generated SFQL text (values are <c>@p…</c> parameters).</summary>
    public abstract string ToSfql();

    /// <summary>The parameter values of <see cref="ToSfql"/>.</summary>
    public IReadOnlyDictionary<string, object?> Parameters => _parameters;

    /// <summary>Runs the query and returns every row.</summary>
    public async ValueTask<IReadOnlyList<TRow>> ToListAsync(QueryOptions? options = null, CancellationToken cancellationToken = default)
    {
        var rows = new List<TRow>();
        await foreach (QueryRow row in _service.QueryAsync(ToSfql(), _parameters, options, cancellationToken).ConfigureAwait(false))
        {
            rows.Add(Materialize(row));
        }

        return rows;
    }

    private protected abstract TRow Materialize(QueryRow row);

    // where …, then the projection, then order, skip and take (the pipeline's stage order).
    private protected string Tail(string projection)
    {
        var text = new StringBuilder();
        foreach (string where in _where)
        {
            text.Append(" | where ").Append(where);
        }

        text.Append(" | project ").Append(projection);
        if (_order.Count > 0)
        {
            text.Append(" | order by ").AppendJoin(", ", _order);
        }

        if (_skip is { } skip)
        {
            text.Append(" | skip ").Append(skip);
        }

        if (_take is { } take)
        {
            text.Append(" | take ").Append(take);
        }

        return text.ToString();
    }

    private protected string Parameter(object? value)
    {
        string name = "p" + _parameters.Count.ToString(CultureInfo.InvariantCulture);
        _parameters[name] = value switch
        {
            int i => (long)i,
            Enum e => Convert.ToInt64(e, CultureInfo.InvariantCulture),
            IStronglyTypedId id => id.ToString(),
            _ => value,
        };
        return "@" + name;
    }

    private string Translate(Expression expression, ParameterExpression row)
    {
        switch (expression)
        {
            case BinaryExpression { NodeType: ExpressionType.AndAlso } and:
                return $"({Translate(and.Left, row)} and {Translate(and.Right, row)})";
            case BinaryExpression { NodeType: ExpressionType.OrElse } or:
                return $"({Translate(or.Left, row)} or {Translate(or.Right, row)})";
            case UnaryExpression { NodeType: ExpressionType.Not } not:
                return $"not ({Translate(not.Operand, row)})";
            case BinaryExpression comparison when Operator(comparison.NodeType) is { } op:
            {
                bool leftIsMember = UsesRow(comparison.Left, row);
                Expression member = leftIsMember ? comparison.Left : comparison.Right;
                Expression value = leftIsMember ? comparison.Right : comparison.Left;
                if (!leftIsMember)
                {
                    op = Mirror(op);
                }

                object? constant = Evaluate(value);
                if (constant is null)
                {
                    return op switch
                    {
                        "=" => $"{Path(member, row)} is null",
                        "<>" => $"{Path(member, row)} is not null",
                        _ => throw Unsupported("an ordering comparison with null"),
                    };
                }

                return $"{Path(member, row)} {op} {Parameter(constant)}";
            }

            case MethodCallExpression { Method.DeclaringType: var type } call when type == typeof(string) && call.Object is not null && call.Arguments.Count == 1
                && call.Method.Name is "StartsWith" or "EndsWith" or "Contains":
            {
                string text = Evaluate(call.Arguments[0]) as string ?? throw Unsupported("a null pattern");
                string escaped = text.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);
                string pattern = call.Method.Name switch
                {
                    "StartsWith" => escaped + "%",
                    "EndsWith" => "%" + escaped,
                    _ => "%" + escaped + "%",
                };
                return $"{Path(call.Object, row)} like {Parameter(pattern)}";
            }

            case MemberExpression member when member.Type == typeof(bool) && UsesRow(member, row):
                return $"{Path(member, row)} = {Parameter(true)}";
            default:
                throw Unsupported(expression.NodeType.ToString());
        }
    }

    // A member path on the row, as JSON names (doc 14 §5): data.amount, or holder for documents.
    private protected string Path(Expression expression, ParameterExpression row)
    {
        var members = new Stack<MemberInfo>();
        Expression? current = expression is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : expression;
        while (current is MemberExpression member)
        {
            members.Push(member.Member);
            current = member.Expression;
        }

        if (current != row || members.Count == 0)
        {
            throw Unsupported("an expression that isn't a member of the row");
        }

        var path = new List<string>();
        JsonTypeInfo? info = _info;
        foreach (MemberInfo member in members)
        {
            JsonPropertyInfo property = info?.Properties.FirstOrDefault(p => p.AttributeProvider is MemberInfo m && m.Name == member.Name)
                ?? throw Unsupported($"member '{member.Name}' (not serialized)");
            path.Add(property.Name);
            info = property.PropertyType == typeof(string) || property.PropertyType.IsPrimitive ? null : _info.Options.GetTypeInfo(property.PropertyType);
        }

        return FieldPrefix + string.Join('.', path.Select(Identifier));
    }

    private static string Identifier(string name)
    {
        return name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && !char.IsAsciiDigit(name[0]) ? name : "`" + name + "`";
    }

    private static bool UsesRow(Expression expression, ParameterExpression row)
    {
        Expression? current = expression is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : expression;
        while (current is MemberExpression member)
        {
            current = member.Expression;
        }

        return current == row;
    }

    // Constants and captured values, read without compiling the expression (AOT-safe).
    private static object? Evaluate(Expression expression)
    {
        switch (expression)
        {
            case ConstantExpression constant:
                return constant.Value;
            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert:
                return Evaluate(convert.Operand);
            case MemberExpression { Member: FieldInfo field } member:
                return field.GetValue(member.Expression is null ? null : Evaluate(member.Expression));
            case MemberExpression { Member: PropertyInfo property } member:
                return property.GetValue(member.Expression is null ? null : Evaluate(member.Expression));
            default:
                throw Unsupported("a value that isn't a constant or a captured variable");
        }
    }

    private static string? Operator(ExpressionType type)
    {
        return type switch
        {
            ExpressionType.Equal => "=",
            ExpressionType.NotEqual => "<>",
            ExpressionType.LessThan => "<",
            ExpressionType.LessThanOrEqual => "<=",
            ExpressionType.GreaterThan => ">",
            ExpressionType.GreaterThanOrEqual => ">=",
            _ => null,
        };
    }

    private static string Mirror(string op)
    {
        return op switch { "<" => ">", "<=" => ">=", ">" => "<", ">=" => "<=", _ => op };
    }

    private static NotSupportedException Unsupported(string what)
    {
        return new NotSupportedException($"The typed query builder doesn't translate {what}; use an SFQL text query instead.");
    }
}

/// <summary>A typed query over one event type's payloads (<c>from events of "…" version N</c>).</summary>
/// <typeparam name="TEvent">The event type.</typeparam>
public sealed class EventQuery<TEvent> : TypedQuery<EventRecord<TEvent>, TEvent>
    where TEvent : class, IEvent
{
    private readonly MessageTypeInfo _type;
    private string? _since;

    internal EventQuery(QueryService service, MessageTypeInfo type)
        : base(service, type.JsonTypeInfo)
    {
        _type = type;
    }

    private protected override string FieldPrefix => "data.";

    /// <summary>Only events decided within <paramref name="window"/> of now (bounds the scan, doc 14 §7.3).</summary>
    public EventQuery<TEvent> Since(TimeSpan window)
    {
        _since = $"time >= ago({Parameter(window)})";
        return this;
    }

    /// <inheritdoc />
    public override string ToSfql()
    {
        string where = _since is null ? string.Empty : " | where " + _since;
        return $"from events of \"{_type.Name}\" version {_type.Version.ToString(CultureInfo.InvariantCulture)}{where}{Tail("position, stream, version, time, data")}";
    }

    private protected override EventRecord<TEvent> Materialize(QueryRow row)
    {
        var data = (TEvent)((JsonElement)row["data"]!).Deserialize(_type.JsonTypeInfo)!;
        return new EventRecord<TEvent>(data, (string)row["stream"]!, (long)row["version"]!, (DateTimeOffset)row["time"]!, (GlobalPosition)row["position"]!);
    }
}

/// <summary>A typed query over a document projection's read model (<c>from readmodel "…"</c>).</summary>
/// <typeparam name="TDocument">The document type.</typeparam>
public sealed class ReadModelQuery<TDocument> : TypedQuery<TDocument, TDocument>
    where TDocument : class
{
    private readonly string _readModel;

    internal ReadModelQuery(QueryService service, string readModel, JsonTypeInfo info)
        : base(service, info)
    {
        _readModel = readModel;
    }

    private protected override string FieldPrefix => string.Empty;

    /// <inheritdoc />
    public override string ToSfql()
    {
        string fields = string.Join(", ", _info.Properties.Select(p => p.Name).Select(n => n.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? n : "`" + n + "`"));
        return $"from readmodel \"{_readModel}\"{Tail(fields)}";
    }

    private protected override TDocument Materialize(QueryRow row)
    {
        var document = new JsonObject();
        for (int i = 0; i < row.Columns.Count; i++)
        {
            document[row.Columns[i].Name] = row[i] switch
            {
                null => null,
                JsonElement e => JsonNode.Parse(e.GetRawText()),
                string s => JsonValue.Create(s),
                long l => JsonValue.Create(l),
                decimal d => JsonValue.Create(d),
                bool b => JsonValue.Create(b),
                DateTimeOffset t => JsonValue.Create(t),
                TimeSpan t => JsonValue.Create(t.ToString("c", CultureInfo.InvariantCulture)),
                var other => JsonValue.Create(Convert.ToString(other, CultureInfo.InvariantCulture)),
            };
        }

        return (TDocument)document.Deserialize(_info)!;
    }
}

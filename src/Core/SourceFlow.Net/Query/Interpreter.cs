using System.Globalization;
using System.Text.Json;
using SourceFlow.Runtime;
using SourceFlow.Storage;

namespace SourceFlow.Query;

/// <summary>The source data of one row: an event, a document, an aggregate state or a catalog entry.</summary>
internal abstract record SourceRow;

internal sealed record EventRow(StoredEvent Event) : SourceRow
{
    private JsonElement? _data;

    public JsonElement Data => _data ??= JsonDocument.Parse(Event.Data).RootElement.Clone();
}

internal sealed record DocumentRow(StoredDocument Document, JsonElement Data) : SourceRow;

internal sealed record StateRow(long Version, JsonElement State) : SourceRow;

internal sealed record SchemaRow(IReadOnlyDictionary<string, object?> Values) : SourceRow;

/// <summary>A joined row: one source row per bound source (null for an unmatched left join).</summary>
internal sealed class Row(SourceRow?[] sources)
{
    public SourceRow?[] Sources { get; } = sources;
}

/// <summary>What an expression is evaluated against.</summary>
internal sealed record Frame(Row? Row, IReadOnlyList<Row>? Group, object?[]? Outputs, Frame? Outer);

/// <summary>One result row before paging: its output values and its sort-key values.</summary>
internal sealed record InterpretedRow(object?[] Outputs, object?[] SortKeys);

/// <summary>What the interpreter runs against: the runtime's storage, the tenant and the query's fixed <c>now()</c>.</summary>
internal sealed record QueryEnvironment(SourceFlowRuntime Runtime, string Tenant, DateTimeOffset Now, int MaxGroups);

/// <summary>
/// The in-memory interpreter (doc 14 §7.1): a tree-walking evaluator over rows read through the storage ports (no
/// code generation or dynamic evaluation, rule S-1). It is the reference semantics the SQL compilers are tested
/// against (cross-provider equivalence), and the executor for stores without a compiler.
/// </summary>
internal sealed class Interpreter(BoundQuery query, QueryEnvironment environment, IReadOnlyDictionary<string, object?> parameters)
{
    private readonly Dictionary<QueryAst, List<Row>> _subqueryRows = new(ReferenceEqualityComparer.Instance);

    public async Task<List<InterpretedRow>> RunAsync(CancellationToken cancellationToken)
    {
        List<Row> rows = await LoadAsync(query, cancellationToken).ConfigureAwait(false);
        foreach (QueryAst subquery in Subqueries(query.Ast))
        {
            _subqueryRows[subquery] = await LoadAsync(query.Subquery(subquery), cancellationToken).ConfigureAwait(false);
        }

        if (query.Ast.Where is not null)
        {
            rows = [.. rows.Where(r => Evaluate(query.Ast.Where, new Frame(r, null, null, null)) is true)];
        }

        var results = new List<InterpretedRow>();
        if (query.Grouped)
        {
            IEnumerable<IReadOnlyList<Row>> groups = query.Ast.GroupBy.Count == 0
                ? [rows]
                : rows.GroupBy(r => QueryValue.Key(query.Ast.GroupBy.Select(k => Evaluate(k, new Frame(r, null, null, null)))), StringComparer.Ordinal)
                    .Select(g => (IReadOnlyList<Row>)[.. g]);
            int count = 0;
            foreach (IReadOnlyList<Row> group in groups)
            {
                if (++count > environment.MaxGroups)
                {
                    throw new QueryException(QueryCodes.TooManyGroups, $"The query produced more than {environment.MaxGroups} groups.");
                }

                var frame = new Frame(null, group, null, null);
                if (query.Ast.Having is not null && Evaluate(query.Ast.Having, frame) is not true)
                {
                    continue;
                }

                results.Add(Shape(frame));
            }
        }
        else
        {
            foreach (Row row in rows)
            {
                results.Add(Shape(new Frame(row, null, null, null)));
            }
        }

        if (query.Ast.Distinct)
        {
            results = [.. results.DistinctBy(r => QueryValue.Key(r.Outputs), StringComparer.Ordinal)];
        }

        results.Sort((a, b) => CompareKeys(a.SortKeys, b.SortKeys));
        return results;
    }

    private InterpretedRow Shape(Frame frame)
    {
        object?[] outputs = [.. query.Outputs.Select(o => Evaluate(o.Expression, frame))];
        Frame withOutputs = frame with { Outputs = outputs };
        object?[] keys = [.. query.OrderBy.Select(s => Evaluate(s.Expression, withOutputs))];
        return new InterpretedRow([.. outputs.Select(QueryValue.Output)], keys);
    }

    /// <summary>Compares sort keys with each key's direction; nulls last ascending and first descending, unless set.</summary>
    public int CompareKeys(object?[] a, object?[] b)
    {
        for (int i = 0; i < query.OrderBy.Count; i++)
        {
            SortItem sort = query.OrderBy[i];
            bool aNull = QueryValue.IsNull(a[i]);
            bool bNull = QueryValue.IsNull(b[i]);
            int result;
            if (aNull || bNull)
            {
                if (aNull && bNull)
                {
                    continue;
                }

                bool nullsFirst = sort.Nulls switch { NullsOrder.First => true, NullsOrder.Last => false, _ => sort.Descending };
                result = aNull ? (nullsFirst ? -1 : 1) : (nullsFirst ? 1 : -1);
                return result;
            }

            result = QueryValue.Compare(a[i], b[i]) ?? string.CompareOrdinal(QueryValue.Key(a[i]), QueryValue.Key(b[i]));
            if (result != 0)
            {
                return sort.Descending ? -result : result;
            }
        }

        return 0;
    }

    // ================================================================ sources and joins

    private async Task<List<Row>> LoadAsync(BoundQuery bound, CancellationToken cancellationToken)
    {
        int width = bound.Sources.Count;
        List<SourceRow> primary = await LoadSourceAsync(bound.Sources[0], cancellationToken).ConfigureAwait(false);
        var rows = new List<Row>(primary.Count);
        foreach (SourceRow source in primary)
        {
            var sources = new SourceRow?[width];
            sources[0] = source;
            rows.Add(new Row(sources));
        }

        for (int j = 0; j < bound.Ast.Joins.Count; j++)
        {
            JoinClause join = bound.Ast.Joins[j];
            int index = j + 1;
            List<SourceRow> right = await LoadSourceAsync(bound.Sources[index], cancellationToken).ConfigureAwait(false);
            var keyColumns = join.On.Select(e => bound.Column(e.Left).Source == index ? (Right: (Expr)e.Left, Left: (Expr)e.Right) : (Right: e.Right, Left: e.Left)).ToArray();
            var table = new Dictionary<string, List<SourceRow>>(StringComparer.Ordinal);
            foreach (SourceRow candidate in right)
            {
                var probe = new SourceRow?[width];
                probe[index] = candidate;
                object?[] values = [.. keyColumns.Select(k => Evaluate(k.Right, new Frame(new Row(probe), null, null, null)))];
                if (values.Any(QueryValue.IsNull))
                {
                    continue;   // NULL never equals anything
                }

                string key = QueryValue.Key(values);
                (table.TryGetValue(key, out List<SourceRow>? bucket) ? bucket : table[key] = []).Add(candidate);
            }

            var joined = new List<Row>();
            foreach (Row row in rows)
            {
                object?[] values = [.. keyColumns.Select(k => Evaluate(k.Left, new Frame(row, null, null, null)))];
                if (!values.Any(QueryValue.IsNull) && table.TryGetValue(QueryValue.Key(values), out List<SourceRow>? matches))
                {
                    foreach (SourceRow match in matches)
                    {
                        SourceRow?[] sources = [.. row.Sources];
                        sources[index] = match;
                        joined.Add(new Row(sources));
                    }
                }
                else if (join.Kind == JoinKind.Left)
                {
                    joined.Add(row);
                }
            }

            rows = joined;
        }

        return rows;
    }

    private async Task<List<SourceRow>> LoadSourceAsync(BoundSource source, CancellationToken cancellationToken)
    {
        StoragePorts storage = environment.Runtime.Storage;
        switch (source.Kind)
        {
            case SourceKind.Events:
            {
                var names = new HashSet<string>(source.EventTypes.Select(t => t.Name), StringComparer.Ordinal);
                long? version = source.Version ?? (((EventsSource)source.Syntax).Version is { } v ? Evaluate(v, new Frame(null, null, null, null)) as long? : null);
                var rows = new List<SourceRow>();
                GlobalPosition after = GlobalPosition.Start;
                while (true)
                {
                    LogBatch batch = await storage.Log.ReadAsync(after, BucketRange.All, 1000, cancellationToken).ConfigureAwait(false);
                    if (batch.Events.Count == 0)
                    {
                        break;
                    }

                    foreach (StoredEvent e in batch.Events)
                    {
                        if ((query.Options.AllTenants || e.TenantId == environment.Tenant)
                            && (names.Count == 0 || names.Contains(e.Type))
                            && (version is null || e.TypeVersion == version))
                        {
                            rows.Add(new EventRow(e));
                        }
                    }

                    after = batch.Events[^1].Position;
                }

                return rows;
            }

            case SourceKind.Stream:
            {
                string id = Text(Evaluate(((StreamSource)source.Syntax).Stream, new Frame(null, null, null, null)));
                StreamSlice slice = await storage.Events.ReadStreamAsync(environment.Tenant, new StreamId(id), new StreamVersion(1), int.MaxValue, cancellationToken).ConfigureAwait(false);
                return [.. slice.Events.Select(e => (SourceRow)new EventRow(e))];
            }

            case SourceKind.ReadModel:
            {
                IReadOnlyList<StoredDocument> documents = await storage.Documents.ListAsync(environment.Tenant, source.ReadModel!.Collection, int.MaxValue, cancellationToken).ConfigureAwait(false);
                return [.. documents.Select(d => (SourceRow)new DocumentRow(d, JsonDocument.Parse(d.Data).RootElement.Clone()))];
            }

            case SourceKind.Aggregate:
                return [await FoldAggregateAsync(source, cancellationToken).ConfigureAwait(false)];
            default:
                return SchemaRows(source);
        }
    }

    // doc 14 §5.6: the aggregate's state folded with its generated Evolve, up to a version or time (strong consistency).
    private async Task<SourceRow> FoldAggregateAsync(BoundSource source, CancellationToken cancellationToken)
    {
        var syntax = (AggregateSource)source.Syntax;
        SourceFlowRuntime runtime = environment.Runtime;
        AggregateDescriptor aggregate = runtime.Model.Aggregates.Values.Single(a => a.Name == source.Aggregate!.Name);
        string id = Text(Evaluate(syntax.Id, new Frame(null, null, null, null)));
        string segment = Guid.TryParse(id, out Guid guid) ? guid.ToString("N") : id;
        var stream = new StreamId($"{aggregate.Name}-{segment}");
        object? bound = syntax.Bound is null ? null : Evaluate(syntax.Bound, new Frame(null, null, null, null));

        StreamSlice slice = await runtime.Storage.Events.ReadStreamAsync(environment.Tenant, stream, new StreamVersion(1), int.MaxValue, cancellationToken).ConfigureAwait(false);
        var decoder = new AggregateStore(runtime);
        object state = aggregate.Initial;
        long version = 0;
        foreach (StoredEvent stored in slice.Events)
        {
            bool include = syntax.AsOf switch
            {
                AsOfKind.Version => stored.StreamVersion.Value <= Convert.ToInt64(bound, CultureInfo.InvariantCulture),
                AsOfKind.Time => QueryValue.Compare(stored.CreatedAt, bound) is <= 0,
                _ => true,
            };
            if (!include)
            {
                break;
            }

            state = aggregate.Evolve(state, (IEvent)decoder.Deserialize(stored, upcast: true));
            version = stored.StreamVersion.Value;
        }

        return new StateRow(version, JsonSerializer.SerializeToElement(state, runtime.Model.JsonTypeInfoFor(aggregate.StateType)));
    }

    private List<SourceRow> SchemaRows(BoundSource source)
    {
        IEnumerable<QueryEventType> types = source.EventTypes.Count > 0
            ? source.EventTypes
            : query.Catalog.EventTypes.OrderBy(t => t.Name, StringComparer.Ordinal);

        var rows = new List<SourceRow>();
        foreach (QueryEventType type in types)
        {
            foreach (QueryEventVersion version in type.Versions)
            {
                foreach (QueryField field in version.Fields)
                {
                    rows.Add(new SchemaRow(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["type"] = type.Name,
                        ["version"] = (long)version.Version,
                        ["field"] = field.Name,
                        ["fieldType"] = field.Type.ToString().ToLowerInvariant(),
                        ["queryable"] = field.Queryable,
                        ["personalData"] = field.PersonalData,
                    }));
                }
            }
        }

        return rows;
    }

    // ================================================================ evaluation

    public object? Evaluate(Expr expr, Frame frame)
    {
        switch (expr)
        {
            case LiteralExpr literal:
                return literal.Value;
            case ParameterExpr parameter:
                return parameters.TryGetValue(parameter.Name, out object? supplied) ? supplied : null;
            case ColumnExpr:
                return Column(expr, query.Column(expr), frame);
            case UnaryExpr { Operator: UnaryOperator.Not } not:
                return Evaluate(not.Operand, frame) is bool b ? !b : null;
            case UnaryExpr negate:
                return Evaluate(negate.Operand, frame) switch { long l => -l, decimal d => -d, _ => null };
            case BinaryExpr binary:
                return Binary(binary, frame);
            case LikeExpr like:
            {
                object? value = Evaluate(like.Value, frame);
                object? pattern = Evaluate(like.Pattern, frame);
                if (value is not string text || pattern is not string p)
                {
                    return null;
                }

                bool match = LikeMatcher.IsMatch(text, p);
                return like.Negated ? !match : match;
            }

            case InListExpr list:
                return In(Evaluate(list.Value, frame), list.Items.Select(i => Evaluate(i, frame)), list.Negated);
            case InSubqueryExpr subquery:
                return In(Evaluate(subquery.Value, frame), SubqueryValues(subquery.Subquery, frame), subquery.Negated);
            case ExistsSubqueryExpr exists:
            {
                bool any = SubqueryRows(exists.Subquery, frame).Any();
                return exists.Negated ? !any : any;
            }

            case ExistsExpr exists:
                return !ReferenceEquals(Column(exists.Column, query.Column(exists.Column), frame), QueryValue.Missing);
            case BetweenExpr between:
            {
                object? value = Evaluate(between.Value, frame);
                bool? result = And(Compare(value, Evaluate(between.Low, frame), c => c >= 0), Compare(value, Evaluate(between.High, frame), c => c <= 0));
                return between.Negated ? Not(result) : result;
            }

            case IsNullExpr isNull:
            {
                bool result = QueryValue.IsNull(isNull.Value is ColumnExpr ? Column(isNull.Value, query.Column(isNull.Value), frame) : Evaluate(isNull.Value, frame));
                return isNull.Negated ? !result : result;
            }

            case CallExpr call:
                return Call(call, frame);
            case CastExpr cast:
                return Cast(Evaluate(cast.Value, frame), cast.Type);
            case CaseExpr @case:
                foreach (CaseBranch branch in @case.Branches)
                {
                    if (Evaluate(branch.When, frame) is true)
                    {
                        return Evaluate(branch.Then, frame);
                    }
                }

                return @case.Else is null ? null : Evaluate(@case.Else, frame);
            default:
                return null;
        }
    }

    private object? Column(Expr expr, BoundColumn column, Frame frame)
    {
        for (int d = 0; d < column.OuterDepth; d++)
        {
            frame = frame.Outer!;
        }

        if (column.Kind == ColumnKind.Output)
        {
            return frame.Outputs![column.Output];
        }

        Row? row = frame.Row ?? (frame.Group is { Count: > 0 } group ? group[0] : null);
        SourceRow? source = row?.Sources[column.Source];
        return source switch
        {
            null => null,
            EventRow e => EventColumn(e, column),
            DocumentRow d => column.Kind == ColumnKind.Attribute
                ? column.Attribute == "id" ? d.Document.Id : d.Document.Version
                : Json(QueryValue.Navigate(d.Data, column.Path), column.Type),
            StateRow s => column.Kind == ColumnKind.Attribute ? s.Version : Json(QueryValue.Navigate(s.State, column.Path), column.Type),
            SchemaRow schema => schema.Values.GetValueOrDefault(column.Attribute!),
            _ => null,
        };
    }

    private static object? EventColumn(EventRow row, BoundColumn column)
    {
        StoredEvent e = row.Event;
        if (column.Kind == ColumnKind.Data)
        {
            if (column.Path.Count == 0)
            {
                return row.Data;
            }

            IReadOnlyList<object>? path = column.Path;
            if (column.VersionPaths is { } versions)
            {
                path = versions.TryGetValue((e.Type, e.TypeVersion), out IReadOnlyList<object>? actual) ? actual : null;
                if (path is null)
                {
                    return QueryValue.Missing;
                }
            }

            return Json(QueryValue.Navigate(row.Data, path), column.Type);
        }

        return column.Attribute switch
        {
            "id" => e.EventId.ToString(),
            "type" => e.Type,
            "typeversion" => (long)e.TypeVersion,
            "source" => e.Metadata.Source,
            "stream" => e.StreamId.Value,
            "aggregatekey" => e.StreamId.AggregateKey,
            "version" => e.StreamVersion.Value,
            "position" => e.Position,
            "time" => e.CreatedAt.ToUniversalTime(),
            "sfcorrelationid" => e.Metadata.CorrelationId.IsNone ? null : e.Metadata.CorrelationId.Value,
            "sfcausationid" => e.Metadata.CausationId?.ToString(),
            "tenant" => e.TenantId,
            "tags" => e.Tags.ToArray(),
            "data" => row.Data,
            _ => null,
        };
    }

    private static object? Json(object navigated, QueryType type)
    {
        return navigated is JsonElement element ? QueryValue.FromJson(element, type) : navigated;
    }

    private object? Binary(BinaryExpr binary, Frame frame)
    {
        switch (binary.Operator)
        {
            case BinaryOperator.And:
                return And(Evaluate(binary.Left, frame) as bool?, Evaluate(binary.Right, frame) as bool?);
            case BinaryOperator.Or:
            {
                bool? left = Evaluate(binary.Left, frame) as bool?;
                bool? right = Evaluate(binary.Right, frame) as bool?;
                return left == true || right == true ? true : left is null || right is null ? null : false;
            }

            case BinaryOperator.Xor:
                return Evaluate(binary.Left, frame) is bool l && Evaluate(binary.Right, frame) is bool r ? l != r : null;
        }

        object? a = Evaluate(binary.Left, frame);
        object? b = Evaluate(binary.Right, frame);
        switch (binary.Operator)
        {
            case BinaryOperator.Equal:
                return QueryValue.Equal(a, b);
            case BinaryOperator.NotEqual:
                return Not(QueryValue.Equal(a, b));
            case BinaryOperator.Less:
                return Compare(a, b, c => c < 0);
            case BinaryOperator.LessOrEqual:
                return Compare(a, b, c => c <= 0);
            case BinaryOperator.Greater:
                return Compare(a, b, c => c > 0);
            case BinaryOperator.GreaterOrEqual:
                return Compare(a, b, c => c >= 0);
        }

        if (QueryValue.IsNull(a) || QueryValue.IsNull(b))
        {
            return null;
        }

        if (a is DateTimeOffset time && b is TimeSpan span)
        {
            return binary.Operator == BinaryOperator.Add ? time + span : binary.Operator == BinaryOperator.Subtract ? time - span : null;
        }

        if (!QueryValue.IsNumber(a) || !QueryValue.IsNumber(b))
        {
            return null;
        }

        if (a is long x && b is long y && binary.Operator != BinaryOperator.Divide)
        {
            return binary.Operator switch
            {
                BinaryOperator.Add => x + y,
                BinaryOperator.Subtract => x - y,
                BinaryOperator.Multiply => x * y,
                _ => y == 0 ? null : x % y,
            };
        }

        decimal m = QueryValue.ToDecimal(a!);
        decimal n = QueryValue.ToDecimal(b!);
        return binary.Operator switch
        {
            BinaryOperator.Add => m + n,
            BinaryOperator.Subtract => m - n,
            BinaryOperator.Multiply => m * n,
            BinaryOperator.Divide => n == 0 ? null : m / n,
            _ => n == 0 ? null : m % n,
        };
    }

    private static bool? Compare(object? a, object? b, Func<int, bool> test)
    {
        return QueryValue.Compare(a, b) is { } c ? test(c) : null;
    }

    private static bool? And(bool? a, bool? b)
    {
        return a == false || b == false ? false : a is null || b is null ? null : true;
    }

    private static bool? Not(bool? value)
    {
        return value is null ? null : !value.Value;
    }

    private static bool? In(object? value, IEnumerable<object?> items, bool negated)
    {
        if (QueryValue.IsNull(value))
        {
            return null;
        }

        bool sawNull = false;
        foreach (object? item in items)
        {
            if (QueryValue.Equal(value, item) == true)
            {
                return !negated;
            }

            sawNull |= QueryValue.IsNull(item);
        }

        return sawNull ? null : negated;
    }

    private IEnumerable<Row> SubqueryRows(QueryAst subquery, Frame outer)
    {
        foreach (Row row in _subqueryRows[subquery])
        {
            if (subquery.Where is null || Evaluate(subquery.Where, new Frame(row, null, null, outer)) is true)
            {
                yield return row;
            }
        }
    }

    private IEnumerable<object?> SubqueryValues(QueryAst subquery, Frame outer)
    {
        Expr selected = subquery.Select![0].Expression!;
        return SubqueryRows(subquery, outer).Select(r => Evaluate(selected, new Frame(r, null, null, outer)));
    }

    private object? Call(CallExpr call, Frame frame)
    {
        if (call.Name is "count" or "sum" or "avg" or "min" or "max")
        {
            return Aggregate(call, frame);
        }

        object?[] args = [.. call.Arguments.Select(a => Evaluate(a, frame))];
        switch (call.Name)
        {
            case "now":
                return environment.Now;
            case "ago":
                return args[0] is TimeSpan span ? environment.Now - span : null;
            case "bin":
                return args[0] is DateTimeOffset at && args[1] is TimeSpan width && width > TimeSpan.Zero
                    ? new DateTimeOffset(at.UtcTicks - (at.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) % width.Ticks, TimeSpan.Zero)
                    : null;
            case "coalesce":
                return args.FirstOrDefault(a => !QueryValue.IsNull(a));
            case "has_tag":
                return args[0] is string tag && frame.Row?.Sources.OfType<EventRow>().FirstOrDefault() is { } e ? e.Event.Tags.Contains(tag) : null;
            case "lower":
                return args[0] is string lower ? lower.ToLowerInvariant() : null;
            case "upper":
                return args[0] is string upper ? upper.ToUpperInvariant() : null;
            case "trim":
                return args[0] is string trim ? trim.Trim() : null;
            case "length":
                return args[0] is string length ? (long)length.Length : null;
            case "concat":
                return args.Any(QueryValue.IsNull) ? null : string.Concat(args.Select(Text));
            case "concat_ws":
                return args[0] is string separator ? string.Join(separator, args.Skip(1).Where(a => !QueryValue.IsNull(a)).Select(Text)) : null;
            case "substring":
            {
                if (args[0] is not string s || args[1] is not long start)
                {
                    return null;
                }

                int from = (int)Math.Clamp(start - 1, 0, s.Length);
                int count = args.Length > 2 && args[2] is long l ? (int)Math.Clamp(l, 0, s.Length - from) : s.Length - from;
                return s.Substring(from, count);
            }

            case "left":
                return args[0] is string ls && args[1] is long ln ? ls[..(int)Math.Clamp(ln, 0, ls.Length)] : null;
            case "right":
                return args[0] is string rs && args[1] is long rn ? rs[(rs.Length - (int)Math.Clamp(rn, 0, rs.Length))..] : null;
            case "abs":
                return args[0] switch { long l => Math.Abs(l), decimal d => Math.Abs(d), _ => null };
            case "floor":
                return args[0] switch { long l => l, decimal d => Math.Floor(d), _ => null };
            case "ceiling":
                return args[0] switch { long l => l, decimal d => Math.Ceiling(d), _ => null };
            case "round":
                return QueryValue.IsNumber(args[0])
                    ? Math.Round(QueryValue.ToDecimal(args[0]!), args.Length > 1 && args[1] is long digits ? (int)Math.Clamp(digits, 0, 28) : 0, MidpointRounding.AwayFromZero)
                    : null;
            case "int":
                return Cast(args[0], "integer");
            case "bool":
                return Cast(args[0], "boolean");
            case "string":
                return Cast(args[0], "string");
            default:
                return null;
        }
    }

    private object? Aggregate(CallExpr call, Frame frame)
    {
        IReadOnlyList<Row> rows = frame.Group ?? (frame.Row is null ? [] : [frame.Row]);
        if (call.Name == "count" && call.Arguments.Count == 0)
        {
            return (long)rows.Count;
        }

        IEnumerable<object?> values = rows.Select(r => Evaluate(call.Arguments[0], new Frame(r, null, null, frame.Outer))).Where(v => !QueryValue.IsNull(v));
        if (call.Distinct)
        {
            values = values.DistinctBy(QueryValue.Key, StringComparer.Ordinal);
        }

        List<object?> list = [.. values];
        switch (call.Name)
        {
            case "count":
                return (long)list.Count;
            case "sum":
                if (list.Count == 0)
                {
                    return null;
                }

                return list.All(v => v is long) ? list.Sum(v => (long)v!) : list.Sum(v => QueryValue.ToDecimal(v!));
            case "avg":
                return list.Count == 0 ? null : list.Average(v => QueryValue.ToDecimal(v!));
            case "min":
                return list.Count == 0 ? null : list.Aggregate((a, b) => QueryValue.Compare(a, b) <= 0 ? a : b);
            default:
                return list.Count == 0 ? null : list.Aggregate((a, b) => QueryValue.Compare(a, b) >= 0 ? a : b);
        }
    }

    private static object? Cast(object? value, string type)
    {
        if (QueryValue.IsNull(value))
        {
            return null;
        }

        string text = Text(value);
        return type switch
        {
            "string" => text,
            "integer" => value switch
            {
                long l => l,
                decimal d => (long)Math.Truncate(d),
                bool b => b ? 1L : 0L,
                _ => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : null,
            },
            "decimal" => value switch
            {
                long l => (decimal)l,
                decimal d => d,
                _ => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed) ? parsed : null,
            },
            "boolean" => value switch
            {
                bool b => b,
                long l => l != 0,
                _ => bool.TryParse(text, out bool parsed) ? parsed : null,
            },
            "timestamp" => value is DateTimeOffset t ? t
                : DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed) ? parsed.ToUniversalTime() : null,
            _ => Guid.TryParse(text, out Guid guid) ? guid.ToString("D") : null,
        };
    }

    private static string Text(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string s => s,
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            DateTimeOffset t => t.ToString("O", CultureInfo.InvariantCulture),
            JsonElement e => e.GetRawText(),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
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
                    Walk(s.Value);
                    return;
                case ExistsSubqueryExpr s:
                    found.Add(s.Subquery);
                    return;
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
}

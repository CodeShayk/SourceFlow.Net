using System.Collections.Frozen;

namespace SourceFlow.Query;

/// <summary>
/// Binds a parsed query to a service's catalog (doc 14 §5, §4.4): resolves sources and columns (Q-B1, I-4), types
/// expressions and parameters, applies per-version renames, names outputs, and checks the grouping rules. Diagnostics:
/// SFQ0201–SFQ0206. Binding stops at the first error.
/// </summary>
public static class SfqlBinder
{
    /// <summary>Binds a query.</summary>
    /// <param name="query">The parsed query.</param>
    /// <param name="text">The query text (for diagnostic positions).</param>
    /// <param name="catalog">The service's catalog.</param>
    public static BindResult Bind(QueryAst query, string text, QueryCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(catalog);
        var diagnostics = new DiagnosticBag(text ?? string.Empty);
        try
        {
            BoundQuery bound = new Binder(catalog, diagnostics).BindQuery(query, outer: null);
            return new BindResult(bound, diagnostics.Items);
        }
        catch (Binder.BindFailedException)
        {
            return new BindResult(null, diagnostics.Items);
        }
    }
}

internal sealed class Binder(QueryCatalog catalog, DiagnosticBag diagnostics)
{
    internal static readonly FrozenDictionary<string, (string Canonical, QueryType Type)> EventAttributes = new Dictionary<string, (string, QueryType)>(StringComparer.Ordinal)
    {
        ["id"] = ("id", QueryType.Uuid),
        ["type"] = ("type", QueryType.String),
        ["typeversion"] = ("typeversion", QueryType.Integer),
        ["source"] = ("source", QueryType.String),
        ["subject"] = ("stream", QueryType.String),
        ["stream"] = ("stream", QueryType.String),
        ["aggregatekey"] = ("aggregatekey", QueryType.String),
        ["version"] = ("version", QueryType.Integer),
        ["position"] = ("position", QueryType.Position),
        ["time"] = ("time", QueryType.Timestamp),
        ["sfcorrelationid"] = ("sfcorrelationid", QueryType.String),
        ["sfcausationid"] = ("sfcausationid", QueryType.String),
        ["tenant"] = ("tenant", QueryType.String),
        ["sftenant"] = ("tenant", QueryType.String),
        ["tags"] = ("tags", QueryType.Array),
        ["data"] = ("data", QueryType.Object),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    internal static readonly FrozenDictionary<string, QueryType> SchemaAttributes = new Dictionary<string, QueryType>(StringComparer.Ordinal)
    {
        ["type"] = QueryType.String,
        ["version"] = QueryType.Integer,
        ["field"] = QueryType.String,
        ["fieldType"] = QueryType.String,
        ["queryable"] = QueryType.Boolean,
        ["personalData"] = QueryType.Boolean,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly string[] EventDefaults = ["position", "stream", "version", "type", "typeversion", "time", "data"];

    private static readonly HashSet<string> AggregateFunctions = new(StringComparer.Ordinal) { "count", "sum", "avg", "min", "max" };

    private readonly Dictionary<Expr, BoundColumn> _columns = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Expr, QueryType> _types = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<QueryAst, BoundQuery> _subqueries = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, QueryType> _parameters = new(StringComparer.Ordinal);

    private enum Clause
    {
        Where,
        Select,
        GroupBy,
        Having,
        OrderBy,
        Join,
        Option,
    }

    internal sealed record Scope(IReadOnlyList<BoundSource> Sources, Scope? Outer);

    private sealed record Context(Scope Scope, Clause Clause, bool Grouped, IReadOnlyList<BoundOutput>? Outputs);

    public BoundQuery BindQuery(QueryAst query, Scope? outer)
    {
        var sources = new List<BoundSource> { BindSource(query.Source, 0) };
        foreach (JoinClause join in query.Joins)
        {
            BoundSource source = BindSource(join.Source, sources.Count);
            if (sources.Any(s => s.Name == source.Name))
            {
                Fail(QueryCodes.Ambiguous, "Each source in a query needs a distinct alias.", join.Source.Span);
            }

            sources.Add(source);
        }

        var scope = new Scope(sources, outer);
        for (int i = 0; i < query.Joins.Count; i++)
        {
            var visible = new Scope([.. sources.Take(i + 2)], outer);
            foreach (JoinEquality equality in query.Joins[i].On)
            {
                QueryType left = BindExpr(equality.Left, new Context(visible, Clause.Join, false, null));
                QueryType right = BindExpr(equality.Right, new Context(visible, Clause.Join, false, null));
                CheckComparable(left, right, equality.Left.Span.Cover(equality.Right.Span));
            }
        }

        if (query.Where is not null)
        {
            ExpectBoolean(BindExpr(query.Where, new Context(scope, Clause.Where, false, null)), query.Where);
        }

        bool grouped = query.GroupBy.Count > 0
            || (query.Select?.Any(i => i.Expression is not null && ContainsAggregate(i.Expression)) ?? false)
            || (query.Having is not null);
        foreach (Expr key in query.GroupBy)
        {
            BindExpr(key, new Context(scope, Clause.GroupBy, false, null));
        }

        HashSet<string> groupKeys = [.. query.GroupBy.Select(AstPrinter.Expr)];
        List<BoundOutput> outputs = BindOutputs(query, scope, grouped, groupKeys);
        if (query.Having is not null)
        {
            ExpectBoolean(BindExpr(query.Having, new Context(scope, Clause.Having, true, null)), query.Having);
            CheckGroupSafe(query.Having, groupKeys);
        }

        var orderBy = new List<SortItem>();
        foreach (SortItem sort in query.OrderBy)
        {
            BindExpr(sort.Expression, new Context(scope, Clause.OrderBy, grouped, outputs));
            if (grouped && !IsOutputReference(sort.Expression))
            {
                CheckGroupSafe(sort.Expression, groupKeys);
            }

            orderBy.Add(sort);
        }

        AppendTieBreak(query, sources, outputs, grouped, orderBy);

        if (query.Limit is not null)
        {
            ExpectCount(query.Limit, "take/LIMIT");
        }

        if (query.Offset is not null)
        {
            ExpectCount(query.Offset, "skip/OFFSET");
        }

        BoundOptions options = BindOptions(query.Options, scope);
        return new BoundQuery(query, catalog, sources, outputs, orderBy, grouped, _parameters, options, _columns, _types, _subqueries);
    }

    // ================================================================ sources

    private BoundSource BindSource(SourceRef source, int index)
    {
        switch (source)
        {
            case EventsSource events:
            {
                var types = new List<QueryEventType>();
                foreach (LiteralExpr name in events.Types)
                {
                    if (!catalog.TryGetEventType((string)name.Value!, out QueryEventType? type))
                    {
                        Fail(QueryCodes.NotOwned, $"Event type {DiagnosticBag.Token((string)name.Value!)} is not owned by this service: queries stay inside the service boundary (Q-B1).", name.Span);
                    }

                    types.Add(type);
                }

                int? version = null;
                if (events.Version is LiteralExpr { Kind: LiteralKind.Integer, Value: long v })
                {
                    version = (int)v;
                    if (types.Count > 0 && types.All(t => t.Versions.All(x => x.Version != version)))
                    {
                        Fail(QueryCodes.UnknownName, $"No listed event type has version {version}.", events.Version.Span);
                    }
                }
                else if (events.Version is not null)
                {
                    BindValue(events.Version, QueryType.Integer);
                }

                return new BoundSource(index, events.Alias ?? "events", SourceKind.Events, source, types, version, null, null);
            }

            case StreamSource stream:
                BindValue(stream.Stream, QueryType.String);
                return new BoundSource(index, stream.Alias ?? "stream", SourceKind.Stream, source, [], null, null, null);
            case ReadModelSource readModel:
            {
                if (!catalog.TryGetReadModel((string)readModel.Collection.Value!, out QueryReadModel? model))
                {
                    Fail(QueryCodes.UnknownName, $"Read model {DiagnosticBag.Token((string)readModel.Collection.Value!)} is not in this service's catalog.", readModel.Collection.Span);
                }

                return new BoundSource(index, readModel.Alias ?? model.Name, SourceKind.ReadModel, source, [], null, model, null);
            }

            case AggregateSource aggregate:
            {
                if (!catalog.TryGetAggregate((string)aggregate.Name.Value!, out QueryAggregate? model))
                {
                    Fail(QueryCodes.UnknownName, $"Aggregate {DiagnosticBag.Token((string)aggregate.Name.Value!)} is not in this service's catalog.", aggregate.Name.Span);
                }

                BindValue(aggregate.Id, QueryType.String);
                if (aggregate.Bound is not null)
                {
                    BindValue(aggregate.Bound, aggregate.AsOf == AsOfKind.Version ? QueryType.Integer : QueryType.Timestamp);
                }

                return new BoundSource(index, aggregate.Alias ?? model.Name, SourceKind.Aggregate, source, [], null, null, model);
            }

            case SchemaSource schema:
            {
                var types = new List<QueryEventType>();
                if (schema.Type is { } name)
                {
                    if (!catalog.TryGetEventType((string)name.Value!, out QueryEventType? type))
                    {
                        Fail(QueryCodes.NotOwned, $"Event type {DiagnosticBag.Token((string)name.Value!)} is not owned by this service (Q-B1).", name.Span);
                    }

                    types.Add(type);
                }

                return new BoundSource(index, schema.Alias ?? "schema", SourceKind.Schema, source, types, null, null, null);
            }

            default:
                Fail(QueryCodes.UnknownName, "Unknown source.", source.Span);
                return null!;
        }
    }

    // ================================================================ outputs

    private List<BoundOutput> BindOutputs(QueryAst query, Scope scope, bool grouped, HashSet<string> groupKeys)
    {
        var items = new List<(Expr Expression, string Name, bool Explicit)>();
        if (query.Select is null)
        {
            if (grouped)
            {
                Fail(QueryCodes.NotGrouped, "A grouped query lists its outputs: the grouping keys and aggregates.", query.Span);
            }

            foreach (BoundSource source in scope.Sources)
            {
                items.AddRange(Defaults(source, query.Source.Span, qualify: scope.Sources.Count > 1));
            }
        }
        else
        {
            foreach (SelectItem item in query.Select)
            {
                if (item.IsStar)
                {
                    if (grouped)
                    {
                        Fail(QueryCodes.NotGrouped, "* can't be used in a grouped query; list the grouping keys and aggregates.", item.Span);
                    }

                    IEnumerable<BoundSource> starred = item.StarQualifier!.Length == 0
                        ? scope.Sources
                        : scope.Sources.Where(s => s.Name == item.StarQualifier).DefaultIfEmpty(null!).Select(s => s ?? FailSource(item));
                    foreach (BoundSource source in starred)
                    {
                        items.AddRange(Defaults(source, item.Span, qualify: scope.Sources.Count > 1));
                    }

                    continue;
                }

                items.Add((item.Expression!, item.Alias ?? QueryParser.OutputName(item.Expression!), item.Alias is not null));
            }
        }

        var outputs = new List<BoundOutput>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach ((Expr expression, string name, bool _) in items)
        {
            QueryType type = BindExpr(expression, new Context(scope, Clause.Select, grouped, null));
            if (grouped)
            {
                CheckGroupSafe(expression, groupKeys);
            }

            string unique = name;
            for (int n = 2; !used.Add(unique); n++)
            {
                unique = $"{name}_{n}";
            }

            outputs.Add(new BoundOutput(unique, type, expression, ContainsPersonalData(expression)));
        }

        return outputs;
    }

    private BoundSource FailSource(SelectItem item)
    {
        Fail(QueryCodes.UnknownName, "Unknown alias in alias.*.", item.Span);
        return null!;
    }

    // A source's default columns (G-3), as synthetic, pre-bound column expressions.
    private IEnumerable<(Expr, string, bool)> Defaults(BoundSource source, TextSpan span, bool qualify)
    {
        string? qualifier = qualify ? source.Name : null;
        string Name(string name)
        {
            return qualify ? $"{source.Name}.{name}" : name;
        }

        switch (source.Kind)
        {
            case SourceKind.Events or SourceKind.Stream:
                foreach (string attribute in EventDefaults)
                {
                    yield return (Synthetic(new BoundColumn(ColumnKind.Attribute, source.Index, attribute, [], EventAttributes[attribute].Type, false, false, null), qualifier, attribute, span), Name(attribute), false);
                }

                break;
            case SourceKind.ReadModel:
                yield return (Synthetic(new BoundColumn(ColumnKind.Attribute, source.Index, "id", [], QueryType.String, false, true, null), qualifier, "id", span), Name("id"), false);
                foreach (QueryField field in source.ReadModel!.Fields.Where(f => f.Name != "id"))
                {
                    yield return (Synthetic(new BoundColumn(ColumnKind.Field, source.Index, null, [field.Name], field.Type, field.PersonalData, field.Queryable, null), qualifier, field.Name, span), Name(field.Name), false);
                }

                break;
            case SourceKind.Aggregate:
                yield return (Synthetic(new BoundColumn(ColumnKind.Attribute, source.Index, "version", [], QueryType.Integer, false, false, null), qualifier, "version", span), Name("version"), false);
                foreach (QueryField field in source.Aggregate!.Fields)
                {
                    yield return (Synthetic(new BoundColumn(ColumnKind.Field, source.Index, null, [field.Name], field.Type, field.PersonalData, false, null), qualifier, field.Name, span), Name(field.Name), false);
                }

                break;
            case SourceKind.Schema:
                foreach ((string attribute, QueryType type) in SchemaAttributes)
                {
                    yield return (Synthetic(new BoundColumn(ColumnKind.Attribute, source.Index, attribute, [], type, false, false, null), qualifier, attribute, span), Name(attribute), false);
                }

                break;
        }
    }

    private ColumnExpr Synthetic(BoundColumn bound, string? qualifier, string name, TextSpan span)
    {
        var column = new ColumnExpr(qualifier, [name], span);
        _columns[column] = bound;
        _types[column] = bound.Type;
        return column;
    }

    // G-6: make ordering total with each source's key (or the grouping keys, or every output for DISTINCT).
    private void AppendTieBreak(QueryAst query, List<BoundSource> sources, List<BoundOutput> outputs, bool grouped, List<SortItem> orderBy)
    {
        var present = new HashSet<string>(orderBy.Select(s => AstPrinter.Expr(s.Expression)), StringComparer.Ordinal);
        foreach (SortItem sort in orderBy.ToArray())
        {
            if (_columns.TryGetValue(sort.Expression, out BoundColumn? c) && c.Kind == ColumnKind.Output)
            {
                present.Add(AstPrinter.Expr(outputs[c.Output].Expression));
            }
        }

        void Add(Expr expression)
        {
            if (present.Add(AstPrinter.Expr(expression)))
            {
                orderBy.Add(new SortItem(expression, false, NullsOrder.Default, expression.Span));
            }
        }

        if (query.Distinct || grouped)
        {
            IEnumerable<Expr> keys = query.Distinct ? outputs.Select(o => o.Expression) : query.GroupBy;
            foreach (Expr key in keys)
            {
                Add(key);
            }

            return;
        }

        foreach (BoundSource source in sources)
        {
            (string? attribute, QueryType type) = source.Kind switch
            {
                SourceKind.Events or SourceKind.Stream => ("position", QueryType.Position),
                SourceKind.ReadModel => ("id", QueryType.String),
                _ => ((string?)null, QueryType.Any),
            };
            if (attribute is not null)
            {
                Add(Synthetic(new BoundColumn(ColumnKind.Attribute, source.Index, attribute, [], type, false, true, null),
                    sources.Count > 1 ? source.Name : null, attribute, query.Span));
            }
        }
    }

    // ================================================================ expressions

    private QueryType BindExpr(Expr expr, Context context)
    {
        QueryType type = expr switch
        {
            LiteralExpr literal => literal.Kind switch
            {
                LiteralKind.Boolean => QueryType.Boolean,
                LiteralKind.Integer => QueryType.Integer,
                LiteralKind.Decimal => QueryType.Decimal,
                LiteralKind.String => QueryType.String,
                LiteralKind.Duration => QueryType.Duration,
                LiteralKind.DateTime => QueryType.Timestamp,
                _ => QueryType.Any,
            },
            ParameterExpr parameter => _parameters.TryAdd(parameter.Name, QueryType.Any) ? QueryType.Any : _parameters[parameter.Name],
            ColumnExpr column => BindColumn(column, context, allowOpaque: false).Type,
            UnaryExpr unary => BindUnary(unary, context),
            BinaryExpr binary => BindBinary(binary, context),
            LikeExpr like => BindLike(like, context),
            InListExpr list => BindInList(list, context),
            InSubqueryExpr subquery => BindInSubquery(subquery, context),
            ExistsSubqueryExpr exists => BindExistsSubquery(exists, context),
            ExistsExpr exists => BindExists(exists, context),
            BetweenExpr between => BindBetween(between, context),
            IsNullExpr isNull => BindIsNull(isNull, context),
            CallExpr call => BindCall(call, context),
            CastExpr cast => BindCast(cast, context),
            CaseExpr @case => BindCase(@case, context),
            _ => QueryType.Any,
        };
        _types[expr] = type;
        return type;
    }

    private BoundColumn BindColumn(ColumnExpr column, Context context, bool allowOpaque)
    {
        if (_columns.TryGetValue(column, out BoundColumn? existing))
        {
            return existing;
        }

        BoundColumn bound = ResolveColumn(column, context, allowOpaque);
        _columns[column] = bound;
        return bound;
    }

    private BoundColumn ResolveColumn(ColumnExpr column, Context context, bool allowOpaque)
    {
        // ORDER BY may name an output column.
        if (context.Clause == Clause.OrderBy && column.Qualifier is null && column.Path.Count == 1 && context.Outputs is { } outputs)
        {
            int index = outputs.ToList().FindIndex(o => o.Name == column.Head);
            if (index >= 0)
            {
                return new BoundColumn(ColumnKind.Output, -1, null, [], outputs[index].Type, outputs[index].PersonalData, false, null, index);
            }
        }

        int depth = 0;
        for (Scope? scope = context.Scope; scope is not null; scope = scope.Outer, depth++)
        {
            if (column.Qualifier is not null)
            {
                BoundSource? source = scope.Sources.FirstOrDefault(s => s.Name == column.Qualifier);
                if (source is null)
                {
                    continue;
                }

                return Resolve(source, column, allowOpaque, report: true)! with { OuterDepth = depth };
            }

            var matches = new List<BoundColumn>();
            foreach (BoundSource source in scope.Sources)
            {
                if (Resolve(source, column, allowOpaque, report: scope.Sources.Count == 1 && scope.Outer is null) is { } found)
                {
                    matches.Add(found with { OuterDepth = depth });
                }
            }

            if (matches.Count > 1)
            {
                Fail(QueryCodes.Ambiguous, $"Column {DiagnosticBag.Token(column.PathText)} is in more than one source; qualify it with an alias (G-10).", column.Span);
            }

            if (matches.Count == 1)
            {
                return matches[0];
            }
        }

        if (column.Qualifier is not null)
        {
            Fail(QueryCodes.UnknownName, $"Unknown alias {DiagnosticBag.Token(column.Qualifier)}.", column.Span);
        }

        // Report against the first source for a precise message.
        return Resolve(context.Scope.Sources[0], column, allowOpaque, report: true)
            ?? throw new BindFailedException();
    }

    // Resolves a path in one source; reports (and fails) or returns null when report is false.
    private BoundColumn? Resolve(BoundSource source, ColumnExpr column, bool allowOpaque, bool report)
    {
        string head = column.Head;
        IReadOnlyList<object> rest = [.. column.Path.Skip(1)];
        switch (source.Kind)
        {
            case SourceKind.Events or SourceKind.Stream:
                if (head == "data")
                {
                    if (rest.Count == 0)
                    {
                        return new BoundColumn(ColumnKind.Data, source.Index, null, [], QueryType.Object, false, false, null);
                    }

                    if (source.EventTypes.Count == 0)
                    {
                        if (allowOpaque)
                        {
                            return new BoundColumn(ColumnKind.Data, source.Index, null, rest, QueryType.Any, false, false, null);
                        }

                        return report
                            ? FailNull(QueryCodes.OpaqueData, "data fields need the events narrowed to their types: from events of \"<type>\" (doc 14 §5.4).", column.Span)
                            : null;
                    }

                    return ResolveData(source, column, rest, report);
                }

                if (rest.Count == 0 && EventAttributes.TryGetValue(head, out (string Canonical, QueryType Type) attribute))
                {
                    bool indexed = attribute.Canonical is "id" or "stream" or "aggregatekey" or "sfcorrelationid" or "sfcausationid" or "position";
                    return new BoundColumn(ColumnKind.Attribute, source.Index, attribute.Canonical, [], attribute.Type, false, indexed, null);
                }

                return report ? FailNull(QueryCodes.UnknownName, $"Unknown event attribute {DiagnosticBag.Token(column.PathText)}; event fields are under data.", column.Span) : null;
            case SourceKind.ReadModel:
                if (rest.Count == 0 && head is "id" or "version")
                {
                    return new BoundColumn(ColumnKind.Attribute, source.Index, head, [], head == "id" ? QueryType.String : QueryType.Integer, false, head == "id", null);
                }

                return ResolveField(source, source.ReadModel!.Fields, column, report);
            case SourceKind.Aggregate:
                if (rest.Count == 0 && head == "version")
                {
                    return new BoundColumn(ColumnKind.Attribute, source.Index, "version", [], QueryType.Integer, false, false, null);
                }

                return ResolveField(source, source.Aggregate!.Fields, column, report);
            default:
                if (rest.Count == 0 && SchemaAttributes.TryGetValue(head, out QueryType schemaType))
                {
                    return new BoundColumn(ColumnKind.Attribute, source.Index, head, [], schemaType, false, false, null);
                }

                return report ? FailNull(QueryCodes.UnknownName, $"Unknown schema column {DiagnosticBag.Token(column.PathText)}.", column.Span) : null;
        }
    }

    private BoundColumn? ResolveData(BoundSource source, ColumnExpr column, IReadOnlyList<object> path, bool report)
    {
        var versionPaths = new Dictionary<(string Type, int Version), IReadOnlyList<object>?>();
        var types = new HashSet<QueryType>();
        bool personal = false;
        bool queryable = true;
        bool found = false;
        foreach (QueryEventType type in source.EventTypes)
        {
            foreach (QueryEventVersion version in type.Versions.Where(v => source.Version is null || v.Version == source.Version))
            {
                QueryField? field = FindPath(version.Fields, type.Latest.Fields, version.Version, path, out IReadOnlyList<object> actual);
                versionPaths[(type.Name, version.Version)] = field is null ? null : actual;
                if (field is not null)
                {
                    found = true;
                    types.Add(field.Type);
                    personal |= field.PersonalData;
                    queryable &= field.Queryable;
                }
            }
        }

        string pathText = string.Concat(path.Select((s, i) => s is int n ? $"[{n}]" : (i == 0 ? "" : ".") + s));
        if (!found)
        {
            return report ? FailNull(QueryCodes.UnknownName, $"Field data.{pathText} is not in any listed event type or version.", column.Span) : null;
        }

        types.Remove(QueryType.Any);
        QueryType resolved = types.Count switch
        {
            0 => QueryType.Any,
            1 => types.First(),
            2 when types.SetEquals([QueryType.Integer, QueryType.Decimal]) => QueryType.Decimal,
            _ => FailType(column, pathText),
        };
        return new BoundColumn(ColumnKind.Data, source.Index, null, path, resolved, personal, queryable, versionPaths);
    }

    private QueryType FailType(ColumnExpr column, string pathText)
    {
        Fail(QueryCodes.UnknownName, $"Field data.{pathText} has incompatible types across the listed event types.", column.Span);
        return QueryType.Any;
    }

    // Walks a field path; the first segment honours [RenamedFrom] declared on the latest version.
    private static QueryField? FindPath(IReadOnlyList<QueryField> fields, IReadOnlyList<QueryField> latest, int version, IReadOnlyList<object> path, out IReadOnlyList<object> actual)
    {
        var resolved = new List<object>(path.Count);
        actual = resolved;
        string head = (string)path[0];
        QueryField? field = fields.FirstOrDefault(f => f.Name == head);
        if (field is null && latest.FirstOrDefault(f => f.Name == head) is { } current && current.RenamedFrom.TryGetValue(version, out string? old))
        {
            field = fields.FirstOrDefault(f => f.Name == old);
        }

        if (field is null)
        {
            return null;
        }

        resolved.Add(field.Name);
        foreach (object segment in path.Skip(1))
        {
            if (segment is int index)
            {
                if (field.Element is null)
                {
                    return null;
                }

                resolved.Add(index);
                field = field.Element;
            }
            else
            {
                field = field.Fields.FirstOrDefault(f => f.Name == (string)segment);
                if (field is null)
                {
                    return null;
                }

                resolved.Add(field.Name);
            }
        }

        return field;
    }

    private BoundColumn? ResolveField(BoundSource source, IReadOnlyList<QueryField> fields, ColumnExpr column, bool report)
    {
        QueryField? field = FindPath(fields, fields, 0, column.Path, out IReadOnlyList<object> actual);
        if (field is null)
        {
            return report ? FailNull(QueryCodes.UnknownName, $"Field {DiagnosticBag.Token(column.PathText)} is not in {source.Name}.", column.Span) : null;
        }

        return new BoundColumn(ColumnKind.Field, source.Index, null, actual, field.Type, field.PersonalData, field.Queryable, null);
    }

    private QueryType BindUnary(UnaryExpr unary, Context context)
    {
        QueryType operand = BindExpr(unary.Operand, context);
        if (unary.Operator == UnaryOperator.Not)
        {
            ExpectBoolean(operand, unary.Operand);
            return QueryType.Boolean;
        }

        ExpectNumeric(operand, unary.Operand);
        return operand;
    }

    private QueryType BindBinary(BinaryExpr binary, Context context)
    {
        QueryType left = BindExpr(binary.Left, context);
        QueryType right = BindExpr(binary.Right, context);
        switch (binary.Operator)
        {
            case BinaryOperator.And or BinaryOperator.Or or BinaryOperator.Xor:
                ExpectBoolean(left, binary.Left);
                ExpectBoolean(right, binary.Right);
                return QueryType.Boolean;
            case BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.Less or BinaryOperator.LessOrEqual or BinaryOperator.Greater or BinaryOperator.GreaterOrEqual:
                Infer(binary.Left, right);
                Infer(binary.Right, left);
                CheckComparable(left, right, binary.Span);
                return QueryType.Boolean;
            default:
                if (left == QueryType.Timestamp && right is QueryType.Duration or QueryType.Any && binary.Operator is BinaryOperator.Add or BinaryOperator.Subtract)
                {
                    Infer(binary.Right, QueryType.Duration);
                    return QueryType.Timestamp;
                }

                ExpectNumeric(left, binary.Left);
                ExpectNumeric(right, binary.Right);
                return left == QueryType.Integer && right == QueryType.Integer && binary.Operator != BinaryOperator.Divide ? QueryType.Integer
                    : left == QueryType.Any && right == QueryType.Any ? QueryType.Any : QueryType.Decimal;
        }
    }

    private QueryType BindLike(LikeExpr like, Context context)
    {
        QueryType value = BindExpr(like.Value, context);
        QueryType pattern = BindExpr(like.Pattern, context);
        Infer(like.Pattern, QueryType.String);
        if (value is not (QueryType.String or QueryType.Any or QueryType.Uuid) || pattern is not (QueryType.String or QueryType.Any))
        {
            Fail(QueryCodes.TypeMismatch, "LIKE compares strings.", like.Span);
        }

        return QueryType.Boolean;
    }

    private QueryType BindInList(InListExpr list, Context context)
    {
        QueryType value = BindExpr(list.Value, context);
        foreach (Expr item in list.Items)
        {
            QueryType itemType = BindExpr(item, context);
            Infer(item, value);
            CheckComparable(value, itemType, item.Span);
        }

        return QueryType.Boolean;
    }

    private QueryType BindInSubquery(InSubqueryExpr subquery, Context context)
    {
        QueryType value = BindExpr(subquery.Value, context);
        BoundQuery bound = BindSubquery(subquery.Subquery, context);
        CheckComparable(value, bound.Outputs[0].Type, subquery.Span);
        return QueryType.Boolean;
    }

    private QueryType BindExistsSubquery(ExistsSubqueryExpr exists, Context context)
    {
        BindSubquery(exists.Subquery, context);
        return QueryType.Boolean;
    }

    private BoundQuery BindSubquery(QueryAst subquery, Context context)
    {
        if (context.Clause is not (Clause.Where or Clause.Having))
        {
            Fail(QueryCodes.Unsupported, "Subqueries are only allowed in WHERE (G-12).", subquery.Span);
        }

        var nested = new Binder(catalog, diagnostics);
        foreach ((string name, QueryType type) in _parameters)
        {
            nested._parameters[name] = type;
        }

        BoundQuery bound = nested.BindQuery(subquery, context.Scope);
        foreach ((Expr key, BoundColumn value) in nested._columns)
        {
            _columns[key] = value;
            if (value.OuterDepth > 0 && !OuterOnlyInEquality(subquery, key))
            {
                Fail(QueryCodes.Unsupported, "A subquery may reference the outer query only in equalities (G-12).", key.Span);
            }
        }

        foreach ((Expr key, QueryType value) in nested._types)
        {
            _types[key] = value;
        }

        foreach ((string name, QueryType type) in nested._parameters)
        {
            _parameters[name] = type;
        }

        _subqueries[subquery] = bound;
        return bound;
    }

    private static bool OuterOnlyInEquality(QueryAst subquery, Expr outer)
    {
        bool found = false;
        bool ok = true;
        void Walk(Expr e, bool inEquality)
        {
            if (ReferenceEquals(e, outer))
            {
                found = true;
                ok &= inEquality;
                return;
            }

            switch (e)
            {
                case BinaryExpr { Operator: BinaryOperator.Equal } b:
                    Walk(b.Left, true);
                    Walk(b.Right, true);
                    break;
                case BinaryExpr b:
                    Walk(b.Left, false);
                    Walk(b.Right, false);
                    break;
                case UnaryExpr u:
                    Walk(u.Operand, false);
                    break;
                default:
                    ExprRewriter.Rewrite(e, n =>
                    {
                        if (ReferenceEquals(n, outer))
                        {
                            found = true;
                            ok = false;
                        }

                        return null;
                    });
                    break;
            }
        }

        if (subquery.Where is not null)
        {
            Walk(subquery.Where, false);
        }

        return !found || ok;
    }

    private QueryType BindExists(ExistsExpr exists, Context context)
    {
        BoundColumn column = BindColumn(exists.Column, context, allowOpaque: true);
        _types[exists.Column] = column.Type;
        return QueryType.Boolean;
    }

    private QueryType BindBetween(BetweenExpr between, Context context)
    {
        QueryType value = BindExpr(between.Value, context);
        QueryType low = BindExpr(between.Low, context);
        QueryType high = BindExpr(between.High, context);
        Infer(between.Low, value);
        Infer(between.High, value);
        CheckComparable(value, low, between.Low.Span);
        CheckComparable(value, high, between.High.Span);
        return QueryType.Boolean;
    }

    private QueryType BindIsNull(IsNullExpr isNull, Context context)
    {
        if (isNull.Value is ColumnExpr column)
        {
            _types[column] = BindColumn(column, context, allowOpaque: true).Type;
        }
        else
        {
            BindExpr(isNull.Value, context);
        }

        return QueryType.Boolean;
    }

    private QueryType BindCall(CallExpr call, Context context)
    {
        if (AggregateFunctions.Contains(call.Name))
        {
            if (context.Clause is Clause.Where or Clause.GroupBy or Clause.Join or Clause.Option)
            {
                Fail(QueryCodes.NotGrouped, "Aggregates can't be used here; filter groups with HAVING (or a where after summarize).", call.Span);
            }

            var inner = context with { Clause = Clause.Where };
            QueryType argument = call.Arguments.Count == 0 ? QueryType.Any : BindAggregateArgument(call.Arguments[0], inner);
            return call.Name switch
            {
                "count" => QueryType.Integer,
                "sum" => argument == QueryType.Integer ? QueryType.Integer : ExpectNumericType(argument, call),
                "avg" => ExpectNumericType(argument, call),
                _ => argument,
            };
        }

        QueryType[] args = [.. call.Arguments.Select(a => BindExpr(a, context))];
        void Arity(int min, int max)
        {
            if (args.Length < min || args.Length > max)
            {
                Fail(QueryCodes.TypeMismatch, $"{call.Name} takes {(min == max ? min.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{min} to {max}")} argument(s).", call.Span);
            }
        }

        switch (call.Name)
        {
            case "now":
                Arity(0, 0);
                return QueryType.Timestamp;
            case "ago":
                Arity(1, 1);
                Infer(call.Arguments[0], QueryType.Duration);
                ExpectType(args[0], QueryType.Duration, call.Arguments[0]);
                return QueryType.Timestamp;
            case "bin":
                Arity(2, 2);
                Infer(call.Arguments[1], QueryType.Duration);
                ExpectType(args[0], QueryType.Timestamp, call.Arguments[0]);
                ExpectType(args[1], QueryType.Duration, call.Arguments[1]);
                return QueryType.Timestamp;
            case "coalesce":
                Arity(1, 16);
                return args.FirstOrDefault(a => a != QueryType.Any);
            case "has_tag":
                Arity(1, 1);
                if (!context.Scope.Sources.Any(s => s.IsEventLike))
                {
                    Fail(QueryCodes.TypeMismatch, "has_tag applies to event sources.", call.Span);
                }

                Infer(call.Arguments[0], QueryType.String);
                return QueryType.Boolean;
            case "lower" or "upper" or "trim":
                Arity(1, 1);
                Infer(call.Arguments[0], QueryType.String);
                return QueryType.String;
            case "length":
                Arity(1, 1);
                return QueryType.Integer;
            case "concat":
                Arity(1, 16);
                return QueryType.String;
            case "concat_ws":
                Arity(2, 16);
                return QueryType.String;
            case "substring":
                Arity(2, 3);
                return QueryType.String;
            case "left" or "right":
                Arity(2, 2);
                return QueryType.String;
            case "abs" or "floor" or "ceiling":
                Arity(1, 1);
                ExpectNumeric(args[0], call.Arguments[0]);
                return args[0] == QueryType.Any ? QueryType.Decimal : args[0];
            case "round":
                Arity(1, 2);
                ExpectNumeric(args[0], call.Arguments[0]);
                return QueryType.Decimal;
            case "int":
                Arity(1, 1);
                return QueryType.Integer;
            case "bool":
                Arity(1, 1);
                return QueryType.Boolean;
            case "string":
                Arity(1, 1);
                return QueryType.String;
            default:
                Fail(QueryCodes.Unsupported, $"Function {DiagnosticBag.Token(call.Name)} is not in the allow-list (I-5).", call.Span);
                return QueryType.Any;
        }
    }

    private QueryType BindAggregateArgument(Expr argument, Context context)
    {
        if (ContainsAggregate(argument))
        {
            Fail(QueryCodes.NotGrouped, "Aggregates can't be nested.", argument.Span);
        }

        return BindExpr(argument, context);
    }

    private QueryType BindCast(CastExpr cast, Context context)
    {
        BindExpr(cast.Value, context);
        return cast.Type switch
        {
            "string" => QueryType.String,
            "integer" => QueryType.Integer,
            "decimal" => QueryType.Decimal,
            "boolean" => QueryType.Boolean,
            "timestamp" => QueryType.Timestamp,
            _ => QueryType.Uuid,
        };
    }

    private QueryType BindCase(CaseExpr @case, Context context)
    {
        QueryType result = QueryType.Any;
        foreach (CaseBranch branch in @case.Branches)
        {
            ExpectBoolean(BindExpr(branch.When, context), branch.When);
            QueryType then = BindExpr(branch.Then, context);
            result = result == QueryType.Any ? then : result;
        }

        if (@case.Else is not null)
        {
            QueryType otherwise = BindExpr(@case.Else, context);
            result = result == QueryType.Any ? otherwise : result;
        }

        return result;
    }

    private void BindValue(Expr value, QueryType expected)
    {
        BindExpr(value, new Context(new Scope([], null), Clause.Option, false, null));
        Infer(value, expected);
    }

    private BoundOptions BindOptions(IReadOnlyList<QueryOption> options, Scope scope)
    {
        var bound = new BoundOptions();
        foreach (QueryOption option in options)
        {
            switch (option.Name)
            {
                case "consistency":
                    if (option.Value is CallExpr { Name: "at_least" } atLeast && atLeast.Arguments.Count == 1)
                    {
                        BindValue(atLeast.Arguments[0], QueryType.String);
                        bound = bound with { Consistency = QueryConsistency.AtLeast, Token = atLeast.Arguments[0] };
                    }
                    else if (option.Value is LiteralExpr { Kind: LiteralKind.String, Value: string level } && Enum.TryParse(level, ignoreCase: true, out QueryConsistency parsed) && parsed != QueryConsistency.AtLeast)
                    {
                        bound = bound with { Consistency = parsed };
                    }
                    else
                    {
                        Fail(QueryCodes.UnknownName, "consistency is horizon, latest, eventual or at_least(@token).", option.Span);
                    }

                    break;
                case "timeout":
                    BindValue(option.Value, QueryType.Duration);
                    bound = bound with { Timeout = option.Value };
                    break;
                case "on_timeout":
                    bound = bound with { StaleOnTimeout = option.Value is LiteralExpr { Value: "stale" } };
                    break;
                case "allow_scan":
                    bound = bound with { AllowScan = option.Value is LiteralExpr { Value: true } };
                    break;
                case "tenants":
                    bound = bound with { AllTenants = option.Value is LiteralExpr { Value: "all" } };
                    break;
                case "include_archived":
                    bound = bound with { IncludeArchived = option.Value is LiteralExpr { Value: true } };
                    break;
                default:
                    Fail(QueryCodes.UnknownName, $"Unknown option {DiagnosticBag.Token(option.Name)}.", option.Span);
                    break;
            }
        }

        _ = scope;
        return bound;
    }

    // ================================================================ checks

    private void Infer(Expr expr, QueryType type)
    {
        if (expr is ParameterExpr parameter && type != QueryType.Any && _parameters.TryGetValue(parameter.Name, out QueryType current) && current == QueryType.Any)
        {
            _parameters[parameter.Name] = type;
            _types[expr] = type;
        }
    }

    private void CheckComparable(QueryType left, QueryType right, TextSpan span)
    {
        if (!Comparable(left, right))
        {
            Fail(QueryCodes.TypeMismatch, $"Can't compare {left.ToString().ToLowerInvariant()} with {right.ToString().ToLowerInvariant()}.", span);
        }
    }

    internal static bool Comparable(QueryType left, QueryType right)
    {
        if (left == right || left == QueryType.Any || right == QueryType.Any)
        {
            return true;
        }

        bool Numeric(QueryType t) => t is QueryType.Integer or QueryType.Decimal;
        bool Textual(QueryType t) => t is QueryType.String or QueryType.Uuid or QueryType.Timestamp;
        return (Numeric(left) && Numeric(right)) || (Textual(left) && Textual(right));
    }

    private void ExpectBoolean(QueryType type, Expr expr)
    {
        if (type is not (QueryType.Boolean or QueryType.Any))
        {
            Fail(QueryCodes.TypeMismatch, $"Expected a boolean condition but this is {type.ToString().ToLowerInvariant()}.", expr.Span);
        }
    }

    private void ExpectNumeric(QueryType type, Expr expr)
    {
        if (type is not (QueryType.Integer or QueryType.Decimal or QueryType.Any))
        {
            Fail(QueryCodes.TypeMismatch, $"Expected a number but this is {type.ToString().ToLowerInvariant()}.", expr.Span);
        }
    }

    private QueryType ExpectNumericType(QueryType type, CallExpr call)
    {
        if (type is not (QueryType.Integer or QueryType.Decimal or QueryType.Any))
        {
            Fail(QueryCodes.TypeMismatch, $"{call.Name} needs a number.", call.Span);
        }

        return QueryType.Decimal;
    }

    private void ExpectType(QueryType type, QueryType expected, Expr expr)
    {
        if (type != expected && type != QueryType.Any)
        {
            Fail(QueryCodes.TypeMismatch, $"Expected {expected.ToString().ToLowerInvariant()} but this is {type.ToString().ToLowerInvariant()}.", expr.Span);
        }
    }

    private void ExpectCount(Expr expr, string what)
    {
        QueryType type = BindExpr(expr, new Context(new Scope([], null), Clause.Option, false, null));
        Infer(expr, QueryType.Integer);
        if (type is not (QueryType.Integer or QueryType.Any) || expr is LiteralExpr { Value: < 0L })
        {
            Fail(QueryCodes.TypeMismatch, $"{what} takes a non-negative whole number.", expr.Span);
        }
    }

    private bool IsOutputReference(Expr expr)
    {
        return _columns.TryGetValue(expr, out BoundColumn? c) && c.Kind == ColumnKind.Output;
    }

    // G-11: outside aggregates, an expression may only use grouping keys.
    private void CheckGroupSafe(Expr expr, HashSet<string> groupKeys)
    {
        if (groupKeys.Contains(AstPrinter.Expr(expr)))
        {
            return;
        }

        switch (expr)
        {
            case CallExpr call when AggregateFunctions.Contains(call.Name):
                return;
            case ColumnExpr column:
                if (_columns.TryGetValue(column, out BoundColumn? bound) && bound.Kind == ColumnKind.Output)
                {
                    return;
                }

                Fail(QueryCodes.NotGrouped, $"{DiagnosticBag.Token(column.PathText)} is neither aggregated nor a grouping key (G-11).", column.Span);
                return;
            case LiteralExpr or ParameterExpr:
                return;
        }

        ExprRewriter.Rewrite(expr, node =>
        {
            if (!ReferenceEquals(node, expr) && node is ColumnExpr or CallExpr && IsDirectChild(expr, node))
            {
                CheckGroupSafe(node, groupKeys);
            }

            return null;
        });
    }

    // True when 'node' is not nested inside an aggregate within 'root' (aggregates are checked as a whole).
    private static bool IsDirectChild(Expr root, Expr node)
    {
        bool insideAggregate = false;
        bool found = false;
        void Walk(Expr e, bool aggregate)
        {
            if (ReferenceEquals(e, node))
            {
                found = true;
                insideAggregate = aggregate;
                return;
            }

            bool here = aggregate || (e is CallExpr c && AggregateFunctions.Contains(c.Name));
            foreach (Expr child in Children(e))
            {
                Walk(child, here);
            }
        }

        Walk(root, false);
        return found && !insideAggregate;
    }

    internal static IEnumerable<Expr> Children(Expr e)
    {
        return e switch
        {
            UnaryExpr u => [u.Operand],
            BinaryExpr b => [b.Left, b.Right],
            LikeExpr l => [l.Value, l.Pattern],
            InListExpr i => [i.Value, .. i.Items],
            InSubqueryExpr s => [s.Value],
            BetweenExpr b => [b.Value, b.Low, b.High],
            IsNullExpr n => [n.Value],
            CallExpr c => c.Arguments,
            CastExpr c => [c.Value],
            CaseExpr c => [.. c.Branches.SelectMany(b => new[] { b.When, b.Then }), .. c.Else is null ? [] : new[] { c.Else }],
            ExistsExpr e2 => [e2.Column],
            _ => [],
        };
    }

    internal static bool ContainsAggregate(Expr expr)
    {
        return (expr is CallExpr call && AggregateFunctions.Contains(call.Name)) || Children(expr).Any(ContainsAggregate);
    }

    private bool ContainsPersonalData(Expr expr)
    {
        return (_columns.TryGetValue(expr, out BoundColumn? c) && c.PersonalData) || Children(expr).Any(ContainsPersonalData);
    }

    private BoundColumn? FailNull(string code, string message, TextSpan span)
    {
        Fail(code, message, span);
        return null;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void Fail(string code, string message, TextSpan span)
    {
        diagnostics.Add(code, message, span);
        throw new BindFailedException();
    }

    internal sealed class BindFailedException : Exception;
}

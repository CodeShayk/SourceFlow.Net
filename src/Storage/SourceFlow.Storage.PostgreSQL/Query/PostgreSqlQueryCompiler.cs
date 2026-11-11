using System.Globalization;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using SourceFlow.Query;

namespace SourceFlow.Storage.PostgreSQL.Query;

/// <summary>How a result column (output or sort key) is read back.</summary>
/// <param name="Column">The first reader ordinal.</param>
/// <param name="Type">The bound type.</param>
/// <param name="Position">True for a global position, read from two columns (<c>tx_id</c>, <c>seq</c>).</param>
internal sealed record ResultShape(int Column, QueryType Type, bool Position);

/// <summary>A compiled statement: SQL text with placeholders only, its typed parameters, and the result shapes.</summary>
internal sealed record CompiledQuery(string Sql, IReadOnlyList<NpgsqlParameter> Parameters, IReadOnlyList<ResultShape> Outputs, IReadOnlyList<ResultShape> Keys);

/// <summary>
/// Compiles a bound SFQL query to one parameterised PostgreSQL statement (doc 14 §7.1, §10; ADR-026). The SQL text is
/// built only from fixed templates, catalog identifiers (the validated schema name and JSON keys from this service's
/// own schemas, quoted) and <c>$n</c> placeholders: every value from the request (literals, parameters, type and
/// collection names, versions, limits, keyset values) is a typed parameter (I-3, QRY-60). The semantics match the
/// in-memory interpreter: SQL three-valued logic, ordinal (<c>COLLATE "C"</c>) text ordering, nulls last ascending and
/// first descending, decimal division with <c>NULLIF</c> for zero divisors.
/// </summary>
internal sealed class PostgreSqlQueryCompiler
{
    private const string Ordinal = " COLLATE \"C\"";

    private readonly QueryExecution _execution;
    private readonly string _schema;
    private readonly List<NpgsqlParameter> _parameters = [];
    private int _aliases;
    private string? _now;
    private string? _tenant;

    private PostgreSqlQueryCompiler(QueryExecution execution, string schema)
    {
        _execution = execution;
        _schema = schema;
    }

    /// <summary>True when every source can be compiled (aggregate folds and schema rows run in the interpreter).</summary>
    public static bool CanCompile(BoundQuery query)
    {
        return query.Sources.All(s => s.Kind is SourceKind.Events or SourceKind.Stream or SourceKind.ReadModel)
            && Subqueries(query).All(CanCompile);
    }

    public static CompiledQuery Compile(QueryExecution execution, string schema)
    {
        return new PostgreSqlQueryCompiler(execution, schema).CompileTop();
    }

    // ================================================================ statement

    private CompiledQuery CompileTop()
    {
        BoundQuery query = _execution.Query;
        var scope = new Scope(query, NewAliases(query), null);
        var select = new List<string>();
        var outputs = new List<ResultShape>();
        var keys = new List<ResultShape>();
        int column = 0;
        for (int i = 0; i < query.Outputs.Count; i++)
        {
            BoundOutput output = query.Outputs[i];
            column = AddResult(select, outputs, $"o{i}", output.Expression, output.Type, scope, column);
        }

        for (int i = 0; i < query.OrderBy.Count; i++)
        {
            // A key with an output's text reuses that output's SQL (one expression to PostgreSQL when grouped).
            Expr key = query.OrderBy[i].Expression;
            string text = key.ToString();
            Expr same = query.Outputs.Select(o => o.Expression).FirstOrDefault(o => o.ToString() == text && TypeOf(o, scope) == TypeOf(key, scope)) ?? key;
            column = AddResult(select, keys, $"k{i}", same, TypeOf(key, scope), scope, column);
        }

        var sql = new StringBuilder();
        sql.Append("SELECT * FROM (SELECT ");
        if (query.Ast.Distinct)
        {
            sql.Append("DISTINCT ");
        }

        sql.AppendJoin(", ", select);
        AppendFromWhere(sql, scope);
        if (query.Ast.GroupBy.Count > 0)
        {
            // A key that is also a select item groups by that item's column, so both are one expression to PostgreSQL.
            sql.Append(" GROUP BY ").AppendJoin(", ", query.Ast.GroupBy.Select(g =>
            {
                int output = Enumerable.Range(0, query.Outputs.Count).FirstOrDefault(i => query.Outputs[i].Type != QueryType.Position
                    && (ReferenceEquals(query.Outputs[i].Expression, g) || query.Outputs[i].Expression.ToString() == g.ToString()), -1);
                return output >= 0 ? (output + 1 + PositionColumnsBefore(query, output)).ToString(CultureInfo.InvariantCulture) : Expression(g, scope);
            }));
        }

        if (query.Ast.Having is not null)
        {
            sql.Append(" HAVING ").Append(Expression(query.Ast.Having, scope));
        }

        sql.Append(") q");
        if (_execution.After is { } after)
        {
            sql.Append(" WHERE ").Append(Keyset(query, keys, after));
        }

        if (keys.Count > 0)
        {
            sql.Append(" ORDER BY ");
            for (int i = 0; i < keys.Count; i++)
            {
                SortItem sort = query.OrderBy[i];
                string direction = (sort.Descending ? " DESC" : " ASC") + (NullsFirst(sort) ? " NULLS FIRST" : " NULLS LAST");
                if (i > 0)
                {
                    sql.Append(", ");
                }

                sql.Append(keys[i].Position
                    ? $"q.k{i}_c{direction}, q.k{i}_s{direction}"
                    : $"q.k{i}{(IsText(keys[i].Type) ? Ordinal : string.Empty)}{direction}");
            }
        }

        sql.Append(" OFFSET ").Append(Parameter((long)_execution.Skip, NpgsqlDbType.Bigint));
        sql.Append(" LIMIT ").Append(Parameter((long)_execution.Take, NpgsqlDbType.Bigint));
        return new CompiledQuery(sql.ToString(), _parameters, outputs, keys);
    }

    private static int PositionColumnsBefore(BoundQuery query, int output)
    {
        return query.Outputs.Take(output).Count(o => o.Type == QueryType.Position);
    }

    private int AddResult(List<string> select, List<ResultShape> shapes, string name, Expr expression, QueryType type, Scope scope, int column)
    {
        if (type == QueryType.Position)
        {
            (string commit, string sequence) = PositionPair(expression, scope);
            select.Add($"{commit} AS {name}_c");
            select.Add($"{sequence} AS {name}_s");
            shapes.Add(new ResultShape(column, type, Position: true));
            return column + 2;
        }

        select.Add($"{Expression(expression, scope)} AS {name}");
        shapes.Add(new ResultShape(column, type, Position: false));
        return column + 1;
    }

    private void AppendFromWhere(StringBuilder sql, Scope scope)
    {
        BoundQuery query = scope.Query;
        var where = new List<string>();
        sql.Append(" FROM ").Append(Table(query.Sources[0])).Append(' ').Append(scope.Aliases[0]);
        where.AddRange(SourceFilters(query.Sources[0], scope.Aliases[0], scope));
        for (int j = 0; j < query.Ast.Joins.Count; j++)
        {
            JoinClause join = query.Ast.Joins[j];
            int index = j + 1;
            BoundSource source = query.Sources[index];
            string alias = scope.Aliases[index];
            var on = new List<string>();
            foreach (JoinEquality equality in join.On)
            {
                on.Add($"({Expression(equality.Left, scope)}) = ({Expression(equality.Right, scope)})");
            }

            on.AddRange(SourceFilters(source, alias, scope));
            if (query.Options.AllTenants && source.Kind == SourceKind.Events && query.Sources[0].Kind == SourceKind.Events)
            {
                on.Add($"{alias}.tenant_id = {scope.Aliases[0]}.tenant_id");   // G-9
            }

            sql.Append(join.Kind == JoinKind.Left ? " LEFT JOIN " : " JOIN ").Append(Table(source)).Append(' ').Append(alias)
                .Append(" ON ").AppendJoin(" AND ", on);
        }

        if (query.Ast.Where is not null)
        {
            where.Add($"({Expression(query.Ast.Where, scope)})");
        }

        if (where.Count > 0)
        {
            sql.Append(" WHERE ").AppendJoin(" AND ", where);
        }
    }

    private string Table(BoundSource source)
    {
        return source.Kind == SourceKind.ReadModel ? $"{_schema}.documents" : $"{_schema}.events";
    }

    // Per-source predicates: tenant (doc 14 §8), type narrowing and version, the collection, and the horizon (§6).
    private IEnumerable<string> SourceFilters(BoundSource source, string alias, Scope scope)
    {
        switch (source.Kind)
        {
            case SourceKind.ReadModel:
                yield return $"{alias}.tenant_id = {Tenant()}";
                yield return $"{alias}.collection = {Parameter(_execution.CollectionOf(source.ReadModel!), NpgsqlDbType.Text)}";
                break;
            case SourceKind.Stream:
                yield return $"{alias}.tenant_id = {Tenant()}";
                yield return $"{alias}.stream_id = ({Value(((StreamSource)source.Syntax).Stream, QueryType.String, scope)})";
                break;
            default:
                if (!scope.Query.Options.AllTenants)
                {
                    yield return $"{alias}.tenant_id = {Tenant()}";
                }

                if (source.EventTypes.Count > 0)
                {
                    yield return $"{alias}.event_type IN ({string.Join(", ", source.EventTypes.Select(t => Parameter(t.Name, NpgsqlDbType.Text)))})";
                }

                if (((EventsSource)source.Syntax).Version is { } version)
                {
                    yield return $"{alias}.type_version = ({Value(version, QueryType.Integer, scope)})";
                }

                if (scope.Query.Options.Consistency != QueryConsistency.Latest)
                {
                    yield return $"{alias}.tx_id < pg_snapshot_xmin(pg_current_snapshot())";
                }

                break;
        }
    }

    // The keyset predicate (doc 14 §7.4): rows strictly after the last row's sort keys, honouring direction and nulls.
    private string Keyset(BoundQuery query, IReadOnlyList<ResultShape> keys, IReadOnlyList<object?> after)
    {
        var disjuncts = new List<string>();
        var equal = new List<string>();
        for (int i = 0; i < keys.Count; i++)
        {
            SortItem sort = query.OrderBy[i];
            ResultShape key = keys[i];
            object? value = after[i];
            string column = key.Position ? $"(q.k{i}_c, q.k{i}_s)" : $"q.k{i}{(IsText(key.Type) ? Ordinal : string.Empty)}";
            string isNull = key.Position ? $"q.k{i}_c IS NULL" : $"q.k{i} IS NULL";
            bool nullsFirst = NullsFirst(sort);
            string greater;
            string same;
            if (value is null)
            {
                greater = nullsFirst ? $"NOT ({isNull})" : "FALSE";
                same = isNull;
            }
            else
            {
                string literal = key.Position ? PositionValue((GlobalPosition)value) : Parameter(value, key.Type);
                greater = $"{column} {(sort.Descending ? "<" : ">")} {literal}" + (nullsFirst ? string.Empty : $" OR {isNull}");
                same = $"{column} = {literal}";
            }

            disjuncts.Add("(" + string.Join(" AND ", equal.Append($"({greater})")) + ")");
            equal.Add($"({same})");
        }

        return "(" + string.Join(" OR ", disjuncts) + ")";
    }

    private static bool NullsFirst(SortItem sort)
    {
        return sort.Nulls switch { NullsOrder.First => true, NullsOrder.Last => false, _ => sort.Descending };
    }

    // ================================================================ expressions

    // The same expression compiles to the same text (and placeholders), so GROUP BY keys match their select items.
    private string Expression(Expr expr, Scope scope)
    {
        if (!scope.Compiled.TryGetValue(expr, out string? sql))
        {
            sql = CompileExpression(expr, scope);
            scope.Compiled[expr] = sql;
        }

        return sql;
    }

    private string CompileExpression(Expr expr, Scope scope)
    {
        switch (expr)
        {
            case LiteralExpr or ParameterExpr:
                return Value(expr, TypeOf(expr, scope), scope);
            case ColumnExpr column:
                return Column(column, scope);
            case UnaryExpr { Operator: UnaryOperator.Not } not:
                return $"NOT ({Expression(not.Operand, scope)})";
            case UnaryExpr negate:
                return $"-({Expression(negate.Operand, scope)})";
            case BinaryExpr binary:
                return Binary(binary, scope);
            case LikeExpr like:
                return $"({Expression(like.Value, scope)}) {(like.Negated ? "NOT LIKE" : "LIKE")} ({Value(like.Pattern, QueryType.String, scope)})";
            case InListExpr list:
            {
                QueryType type = TypeOf(list.Value, scope);
                return $"({Expression(list.Value, scope)}) {(list.Negated ? "NOT IN" : "IN")} ({string.Join(", ", list.Items.Select(i => Operand(i, type, scope)))})";
            }

            case InSubqueryExpr subquery:
                return $"({Expression(subquery.Value, scope)}) {(subquery.Negated ? "NOT IN" : "IN")} ({Subquery(subquery.Subquery, scope, exists: false)})";
            case ExistsSubqueryExpr exists:
                return $"{(exists.Negated ? "NOT EXISTS" : "EXISTS")} ({Subquery(exists.Subquery, scope, exists: true)})";
            case ExistsExpr exists:
                return Exists(exists.Column, scope);
            case BetweenExpr between:
            {
                QueryType type = TypeOf(between.Value, scope);
                string collate = IsText(type) ? Ordinal : string.Empty;
                return $"({Expression(between.Value, scope)}){collate} {(between.Negated ? "NOT BETWEEN" : "BETWEEN")} ({Operand(between.Low, type, scope)}) AND ({Operand(between.High, type, scope)})";
            }

            case IsNullExpr isNull:
                return $"({Expression(isNull.Value, scope)}) {(isNull.Negated ? "IS NOT NULL" : "IS NULL")}";
            case CallExpr call:
                return Call(call, scope);
            case CastExpr cast:
                return Cast(Expression(cast.Value, scope), TypeOf(cast.Value, scope), cast.Type);
            case CaseExpr @case:
            {
                var sql = new StringBuilder("CASE");
                foreach (CaseBranch branch in @case.Branches)
                {
                    sql.Append(" WHEN ").Append(Expression(branch.When, scope)).Append(" THEN ").Append(Expression(branch.Then, scope));
                }

                if (@case.Else is not null)
                {
                    sql.Append(" ELSE ").Append(Expression(@case.Else, scope));
                }

                return sql.Append(" END").ToString();
            }

            default:
                throw Unsupported("expression");
        }
    }

    private string Binary(BinaryExpr binary, Scope scope)
    {
        switch (binary.Operator)
        {
            case BinaryOperator.And:
                return $"({Expression(binary.Left, scope)}) AND ({Expression(binary.Right, scope)})";
            case BinaryOperator.Or:
                return $"({Expression(binary.Left, scope)}) OR ({Expression(binary.Right, scope)})";
            case BinaryOperator.Xor:
                return $"({Expression(binary.Left, scope)}) <> ({Expression(binary.Right, scope)})";
            case BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.Less or BinaryOperator.LessOrEqual or BinaryOperator.Greater or BinaryOperator.GreaterOrEqual:
                return Comparison(binary, scope);
        }

        QueryType leftType = TypeOf(binary.Left, scope);
        QueryType rightType = TypeOf(binary.Right, scope);
        string left = Operand(binary.Left, rightType == QueryType.Duration ? QueryType.Timestamp : leftType, scope);
        string right = Operand(binary.Right, leftType == QueryType.Timestamp ? QueryType.Duration : rightType, scope);
        return binary.Operator switch
        {
            BinaryOperator.Add => $"({left}) + ({right})",
            BinaryOperator.Subtract => $"({left}) - ({right})",
            BinaryOperator.Multiply => $"({left}) * ({right})",
            BinaryOperator.Divide => $"({left})::numeric / NULLIF(({right})::numeric, 0)",
            _ => $"({left}) % NULLIF(({right}), 0)",
        };
    }

    private string Comparison(BinaryExpr binary, Scope scope)
    {
        string op = binary.Operator switch
        {
            BinaryOperator.Equal => "=",
            BinaryOperator.NotEqual => "<>",
            BinaryOperator.Less => "<",
            BinaryOperator.LessOrEqual => "<=",
            BinaryOperator.Greater => ">",
            _ => ">=",
        };
        QueryType leftType = TypeOf(binary.Left, scope);
        QueryType rightType = TypeOf(binary.Right, scope);
        bool leftIsValue = binary.Left is LiteralExpr or ParameterExpr;
        QueryType type = (leftIsValue && binary.Right is not (LiteralExpr or ParameterExpr)) || leftType == QueryType.Any ? rightType : leftType;
        if (type == QueryType.Position || rightType == QueryType.Position)
        {
            (string lc, string ls) = PositionPair(binary.Left, scope);
            (string rc, string rs) = PositionPair(binary.Right, scope);
            return $"({lc}, {ls}) {op} ({rc}, {rs})";
        }

        string collate = IsText(type) && op is not ("=" or "<>") ? Ordinal : string.Empty;
        return $"({Operand(binary.Left, type, scope)}){collate} {op} ({Operand(binary.Right, type, scope)})";
    }

    // A value in the context of another operand's type (a string literal compared with a timestamp becomes a timestamp).
    private string Operand(Expr expr, QueryType context, Scope scope)
    {
        return expr is LiteralExpr or ParameterExpr ? Value(expr, context, scope) : Expression(expr, scope);
    }

    private string Value(Expr expr, QueryType context, Scope scope)
    {
        object? value = expr switch
        {
            LiteralExpr literal => literal.Value,
            ParameterExpr parameter => _execution.Parameters.TryGetValue(parameter.Name, out object? supplied) ? supplied : null,
            _ => null,
        };
        if (expr is not (LiteralExpr or ParameterExpr))
        {
            return Expression(expr, scope);
        }

        if (value is null)
        {
            return context switch
            {
                QueryType.Integer => "NULL::bigint",
                QueryType.Decimal => "NULL::numeric",
                QueryType.Boolean => "NULL::boolean",
                QueryType.Timestamp => "NULL::timestamptz",
                QueryType.Duration => "NULL::interval",
                _ => "NULL::text",
            };
        }

        return value is GlobalPosition position ? PositionValue(position) : Parameter(Coerce(value, context), context);
    }

    private static object Coerce(object value, QueryType context)
    {
        switch (context, value)
        {
            case (QueryType.Timestamp, string text) when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset time):
                return time.ToUniversalTime();
            case (QueryType.Decimal, string text) when decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal number):
                return number;
            case (QueryType.Integer, string text) when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer):
                return integer;
            case (QueryType.Duration, string text) when TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out TimeSpan span):
                return span;
            case (QueryType.Uuid, string text) when Guid.TryParse(text, out Guid guid):
                return guid.ToString("D");
            case (QueryType.Decimal, long integer):
                return (decimal)integer;
            default:
                return value;
        }
    }

    private string Parameter(object value, QueryType context)
    {
        return value switch
        {
            string s => Parameter(s, NpgsqlDbType.Text),
            long l => Parameter(l, NpgsqlDbType.Bigint),
            int i => Parameter((long)i, NpgsqlDbType.Bigint),
            decimal d => Parameter(d, NpgsqlDbType.Numeric),
            bool b => Parameter(b, NpgsqlDbType.Boolean),
            DateTimeOffset t => Parameter(t.ToUniversalTime(), NpgsqlDbType.TimestampTz),
            TimeSpan t => Parameter(t, NpgsqlDbType.Interval),
            _ => throw Unsupported($"a {context.ToString().ToLowerInvariant()} value"),
        };
    }

    private string Parameter(object value, NpgsqlDbType type)
    {
        _parameters.Add(new NpgsqlParameter { Value = value, NpgsqlDbType = type });
        string cast = type switch
        {
            NpgsqlDbType.Text => "text",
            NpgsqlDbType.Bigint => "bigint",
            NpgsqlDbType.Numeric => "numeric",
            NpgsqlDbType.Boolean => "boolean",
            NpgsqlDbType.TimestampTz => "timestamptz",
            NpgsqlDbType.Interval => "interval",
            NpgsqlDbType.Xid8 => "xid8",
            _ => throw Unsupported("a parameter type"),
        };
        return $"${_parameters.Count.ToString(CultureInfo.InvariantCulture)}::{cast}";
    }

    private string PositionValue(GlobalPosition position)
    {
        return $"({Parameter(position.Commit, NpgsqlDbType.Xid8)}, {Parameter((long)position.Sequence, NpgsqlDbType.Bigint)})";
    }

    private (string Commit, string Sequence) PositionPair(Expr expr, Scope scope)
    {
        switch (expr)
        {
            case ColumnExpr column when Resolve(column, scope) is { Column.Kind: ColumnKind.Attribute, Column.Attribute: "position" } resolved:
                return ($"{resolved.Alias}.tx_id", $"{resolved.Alias}.seq");
            case ColumnExpr column when Resolve(column, scope) is { Column.Kind: ColumnKind.Output } output:
                return PositionPair(output.Scope.Query.Outputs[output.Column.Output].Expression, output.Scope);
            case LiteralExpr or ParameterExpr:
            {
                object? value = expr is LiteralExpr literal ? literal.Value
                    : _execution.Parameters.GetValueOrDefault(((ParameterExpr)expr).Name);
                if (value is string text && GlobalPosition.TryParse(text, out GlobalPosition parsed))
                {
                    value = parsed;
                }

                if (value is null)
                {
                    return ("NULL::xid8", "NULL::bigint");
                }

                return value is GlobalPosition position
                    ? (Parameter(position.Commit, NpgsqlDbType.Xid8), Parameter((long)position.Sequence, NpgsqlDbType.Bigint))
                    : throw Unsupported("a position value");
            }

            default:
                throw Unsupported("a position expression");
        }
    }

    private string Exists(ColumnExpr column, Scope scope)
    {
        Resolved resolved = Resolve(column, scope);
        if (resolved.Column.Kind is not (ColumnKind.Data or ColumnKind.Field))
        {
            return "TRUE";
        }

        return $"({Data(resolved, QueryType.Object)}) IS NOT NULL";
    }

    private string Subquery(QueryAst ast, Scope outer, bool exists)
    {
        BoundQuery bound = outer.Query.Subquery(ast);
        var scope = new Scope(bound, NewAliases(bound), outer);
        var sql = new StringBuilder("SELECT ");
        sql.Append(exists ? "1" : Expression(bound.Outputs[0].Expression, scope));
        AppendFromWhere(sql, scope);
        return sql.ToString();
    }

    private string Call(CallExpr call, Scope scope)
    {
        IReadOnlyList<Expr> args = call.Arguments;
        string Arg(int i) => Expression(args[i], scope);
        QueryType ArgType(int i) => TypeOf(args[i], scope);
        switch (call.Name)
        {
            case "count":
                return args.Count == 0 ? "count(*)" : $"count({(call.Distinct ? "DISTINCT " : string.Empty)}{Arg(0)})";
            case "sum" or "avg":
                return $"{call.Name}({(call.Distinct ? "DISTINCT " : string.Empty)}{Arg(0)})";
            case "min" or "max":
                return $"{call.Name}({(call.Distinct ? "DISTINCT " : string.Empty)}({Arg(0)}){(IsText(ArgType(0)) ? Ordinal : string.Empty)})";
            case "now":
                return Now();
            case "ago":
                return $"({Now()} - ({Operand(args[0], QueryType.Duration, scope)}))";
            case "bin":
                return $"date_bin(({Operand(args[1], QueryType.Duration, scope)}), ({Operand(args[0], QueryType.Timestamp, scope)}), to_timestamp(0))";
            case "coalesce":
            {
                QueryType type = args.Select(a => TypeOf(a, scope)).FirstOrDefault(t => t != QueryType.Any);
                return $"COALESCE({string.Join(", ", args.Select(a => Operand(a, type, scope)))})";
            }

            case "has_tag":
            {
                string alias = scope.Aliases[scope.Query.Sources.First(s => s.IsEventLike).Index];
                return $"({Value(args[0], QueryType.String, scope)}) = ANY({alias}.tags)";
            }

            case "lower" or "upper":
                return $"{call.Name}({Arg(0)})";
            case "trim":
                return $"btrim({Arg(0)})";
            case "length":
                return $"char_length({Arg(0)})::bigint";
            case "concat":
                return "(" + string.Join(" || ", args.Select((_, i) => $"({Arg(i)})::text")) + ")";
            case "concat_ws":
                return $"concat_ws({string.Join(", ", args.Select((_, i) => i == 0 ? Arg(i) : $"({Arg(i)})::text"))})";
            case "substring":
                return args.Count > 2
                    ? $"substr({Arg(0)}, greatest(({Arg(1)}), 1)::int, greatest(({Arg(2)}), 0)::int)"
                    : $"substr({Arg(0)}, greatest(({Arg(1)}), 1)::int)";
            case "left" or "right":
                return $"{call.Name}({Arg(0)}, greatest(({Arg(1)}), 0)::int)";
            case "abs":
                return $"abs({Arg(0)})";
            case "floor" or "ceiling":
                return ArgType(0) == QueryType.Integer ? Arg(0) : $"{(call.Name == "floor" ? "floor" : "ceil")}(({Arg(0)})::numeric)";
            case "round":
                return args.Count > 1 ? $"round(({Arg(0)})::numeric, ({Arg(1)})::int)" : $"round(({Arg(0)})::numeric)";
            case "int":
                return Cast(Arg(0), ArgType(0), "integer");
            case "bool":
                return Cast(Arg(0), ArgType(0), "boolean");
            case "string":
                return Cast(Arg(0), ArgType(0), "string");
            default:
                throw Unsupported($"function {call.Name}");
        }
    }

    private static string Cast(string sql, QueryType from, string to)
    {
        return to switch
        {
            "string" => $"({sql})::text",
            "integer" => from switch
            {
                QueryType.Integer => sql,
                QueryType.Decimal => $"trunc({sql})::bigint",
                QueryType.Boolean => $"({sql})::int::bigint",
                _ => $"({sql})::bigint",
            },
            "decimal" => $"({sql})::numeric",
            "boolean" => from == QueryType.Integer ? $"(({sql}) <> 0)" : $"({sql})::boolean",
            "timestamp" => $"({sql})::timestamptz",
            _ => $"({sql})::uuid::text",
        };
    }

    // ================================================================ columns

    private string Column(ColumnExpr expr, Scope scope)
    {
        Resolved resolved = Resolve(expr, scope);
        BoundColumn column = resolved.Column;
        if (column.Kind == ColumnKind.Output)
        {
            return Expression(resolved.Scope.Query.Outputs[column.Output].Expression, resolved.Scope);
        }

        string a = resolved.Alias;
        if (column.Kind is ColumnKind.Data or ColumnKind.Field)
        {
            return Data(resolved, column.Type);
        }

        if (resolved.Source.Kind == SourceKind.ReadModel)
        {
            return column.Attribute == "id" ? $"{a}.id" : $"{a}.version";
        }

        return column.Attribute switch
        {
            "id" => $"{a}.event_id::text",
            "type" => $"{a}.event_type",
            "typeversion" => $"{a}.type_version::bigint",
            "source" => $"({a}.metadata->>'source')",
            "stream" => $"{a}.stream_id",
            "aggregatekey" => $"split_part({a}.stream_id, '-', -1)",
            "version" => $"{a}.stream_version",
            "time" => $"{a}.created_at",
            "sfcorrelationid" => $"({a}.metadata->>'correlationId')",
            "sfcausationid" => $"({a}.metadata->>'causationId')",
            "tenant" => $"{a}.tenant_id",
            "tags" => $"{a}.tags",
            "data" => $"{a}.data",
            "position" => throw Unsupported("position outside comparisons, ordering and results"),
            _ => throw Unsupported($"attribute {column.Attribute}"),
        };
    }

    // A path below data (with per-version renames, doc 14 §5.4) or below a document root, typed.
    private string Data(Resolved resolved, QueryType type)
    {
        BoundColumn column = resolved.Column;
        string a = resolved.Alias;
        if (column.Path.Count == 0)
        {
            return $"{a}.data";
        }

        if (column.VersionPaths is not { } versions)
        {
            return Extract(a, column.Path, type);
        }

        var distinct = versions.Values.Where(p => p is not null).Select(p => string.Join('\u001F', p!)).Distinct().ToList();
        if (distinct.Count == 1 && versions.Values.All(p => p is not null))
        {
            return Extract(a, versions.Values.First()!, type);
        }

        var sql = new StringBuilder("CASE");
        foreach (((string eventType, int version), IReadOnlyList<object>? path) in versions)
        {
            if (path is null)
            {
                continue;   // missing in this version: NULL
            }

            sql.Append($" WHEN {a}.event_type = {Parameter(eventType, NpgsqlDbType.Text)} AND {a}.type_version = {Parameter((long)version, NpgsqlDbType.Bigint)} THEN {Extract(a, path, type)}");
        }

        return sql.Append(" END").ToString();
    }

    internal static string Extract(string alias, IReadOnlyList<object> path, QueryType type)
    {
        var sql = new StringBuilder($"{alias}.data");
        bool json = type is QueryType.Object or QueryType.Array or QueryType.Any;
        for (int i = 0; i < path.Count; i++)
        {
            sql.Append(json || i < path.Count - 1 ? "->" : "->>");
            sql.Append(path[i] is int index ? index.ToString(CultureInfo.InvariantCulture) : JsonKey((string)path[i]));
        }

        string text = sql.ToString();
        return type switch
        {
            QueryType.Any => $"({text})::jsonb",
            QueryType.Integer => $"({text})::bigint",
            QueryType.Decimal => $"({text})::numeric",
            QueryType.Boolean => $"({text})::boolean",
            QueryType.Timestamp => $"({text})::timestamptz",
            QueryType.Duration => $"({text})::interval",
            _ => $"({text})",
        };
    }

    // JSON keys come from this service's catalog, never from the request; quoted as SQL string constants regardless.
    private static string JsonKey(string key)
    {
        foreach (char c in key)
        {
            if (char.IsControl(c))
            {
                throw Unsupported("a JSON key with control characters");
            }
        }

        return "'" + key.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private Resolved Resolve(ColumnExpr expr, Scope scope)
    {
        BoundColumn column = scope.Query.Column(expr);
        Scope target = scope;
        for (int d = 0; d < column.OuterDepth; d++)
        {
            target = target.Outer ?? throw Unsupported("an outer reference");
        }

        if (column.Kind == ColumnKind.Output)
        {
            return new Resolved(column, target, null!, string.Empty);
        }

        return new Resolved(column, target, target.Query.Sources[column.Source], target.Aliases[column.Source]);
    }

    private static QueryType TypeOf(Expr expr, Scope scope)
    {
        if (expr is ColumnExpr && scope.Query.TryGetColumn(expr, out BoundColumn? column))
        {
            if (column.Kind == ColumnKind.Attribute && column.Attribute == "position")
            {
                return QueryType.Position;
            }

            if (column.Kind == ColumnKind.Output)
            {
                Scope target = scope;
                for (int d = 0; d < column.OuterDepth; d++)
                {
                    target = target.Outer!;
                }

                return target.Query.Outputs[column.Output].Type;
            }
        }

        return scope.Query.TypeOf(expr);
    }

    private string Now()
    {
        return _now ??= Parameter(_execution.Now.ToUniversalTime(), NpgsqlDbType.TimestampTz);
    }

    private string Tenant()
    {
        return _tenant ??= Parameter(_execution.Tenant, NpgsqlDbType.Text);
    }

    private string[] NewAliases(BoundQuery query)
    {
        return [.. query.Sources.Select(_ => "t" + (_aliases++).ToString(CultureInfo.InvariantCulture))];
    }

    private static bool IsText(QueryType type)
    {
        return type is QueryType.String or QueryType.Uuid;
    }

    private static IEnumerable<BoundQuery> Subqueries(BoundQuery query)
    {
        var found = new List<BoundQuery>();
        void Walk(Expr? e)
        {
            switch (e)
            {
                case null:
                    return;
                case InSubqueryExpr s:
                    found.Add(query.Subquery(s.Subquery));
                    Walk(s.Value);
                    return;
                case ExistsSubqueryExpr s:
                    found.Add(query.Subquery(s.Subquery));
                    return;
                case UnaryExpr u:
                    Walk(u.Operand);
                    return;
                case BinaryExpr b:
                    Walk(b.Left);
                    Walk(b.Right);
                    return;
            }
        }

        Walk(query.Ast.Where);
        Walk(query.Ast.Having);
        return found;
    }

    private static QueryException Unsupported(string what)
    {
        return new QueryException(QueryCodes.Unsupported, $"The PostgreSQL query compiler does not support {what}.");
    }

    private sealed record Scope(BoundQuery Query, string[] Aliases, Scope? Outer)
    {
        public Dictionary<Expr, string> Compiled { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private sealed record Resolved(BoundColumn Column, Scope Scope, BoundSource Source, string Alias);
}

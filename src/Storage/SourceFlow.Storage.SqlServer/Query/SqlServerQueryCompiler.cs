using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using SourceFlow.Query;

namespace SourceFlow.Storage.SqlServer.Query;

/// <summary>How a result column (output or sort key) is read back.</summary>
/// <param name="Column">The first reader ordinal.</param>
/// <param name="Type">The bound type.</param>
/// <param name="Position">True for a global position, read from two columns (rowversion, <c>seq</c>).</param>
/// <param name="Tags">True for the event <c>tags</c> attribute (a JSON array read back as strings).</param>
internal sealed record ResultShape(int Column, QueryType Type, bool Position, bool Tags = false);

/// <summary>A compiled statement: T-SQL with placeholders only, its typed parameters, and the result shapes.</summary>
internal sealed record CompiledQuery(string Sql, IReadOnlyList<SqlParameter> Parameters, IReadOnlyList<ResultShape> Outputs, IReadOnlyList<ResultShape> Keys);

/// <summary>
/// Compiles a bound SFQL query to one parameterised T-SQL statement (doc 14 §7.1, §10 examples B and G; ADR-026). The SQL
/// text is built only from fixed templates, catalog identifiers (the validated schema name, and JSON paths from this
/// service's own schemas, quoted) and <c>@pN</c> placeholders: every value from the request is a typed parameter (I-3).
/// </summary>
/// <remarks>
/// The semantics match the in-memory interpreter: three-valued logic (conditions and <c>bit</c> values are compiled
/// separately, T-SQL has no boolean scalar), ordinal text (every text value is <c>COLLATE Latin1_General_BIN2</c>, so
/// equality, <c>LIKE</c>, grouping and ordering are case- and accent-sensitive), nulls last ascending and first descending,
/// decimal division with <c>NULLIF</c> for zero divisors (six decimal places, SQL Server's decimal rules). A construct
/// SQL Server cannot express the same way is <see cref="QueryCodes.Unsupported"/>: <see cref="CanCompile"/> then sends the
/// query to the interpreter, which gives the same rows over the same store.
/// </remarks>
internal sealed class SqlServerQueryCompiler
{
    private const string Bin2 = " COLLATE Latin1_General_BIN2";
    private const string Decimal = "decimal(38, 10)";

    private readonly QueryExecution _execution;
    private readonly string _schema;
    private readonly List<SqlParameter> _parameters = [];
    private int _aliases;
    private string? _now;
    private string? _tenant;

    private SqlServerQueryCompiler(QueryExecution execution, string schema)
    {
        _execution = execution;
        _schema = "[" + schema + "]";
    }

    /// <summary>True when the query compiles (aggregate folds, schema rows and unsupported constructs run in the interpreter).</summary>
    public static bool CanCompile(BoundQuery query, string schema)
    {
        if (!query.Sources.All(s => s.Kind is SourceKind.Events or SourceKind.Stream or SourceKind.ReadModel))
        {
            return false;
        }

        try
        {
            var probe = new QueryExecution(query, new Dictionary<string, object?>(), "-", DateTimeOffset.UnixEpoch, null, 0, 1, QueryPolicySettings.Default);
            Compile(probe, schema);
            return true;
        }
        catch (QueryException e) when (e.Code == QueryCodes.Unsupported)
        {
            return false;
        }
    }

    public static CompiledQuery Compile(QueryExecution execution, string schema)
    {
        return new SqlServerQueryCompiler(execution, schema).CompileTop();
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
            // T-SQL groups by expressions, not ordinals: a key that is also a select item reuses that item's exact text.
            sql.Append(" GROUP BY ").AppendJoin(", ", query.Ast.GroupBy.Select(g =>
            {
                BoundOutput? output = query.Outputs.FirstOrDefault(o => o.Type != QueryType.Position && (ReferenceEquals(o.Expression, g) || o.Expression.ToString() == g.ToString()));
                return Value(output?.Expression ?? g, scope);
            }));
        }

        if (query.Ast.Having is not null)
        {
            sql.Append(" HAVING ").Append(Condition(query.Ast.Having, scope));
        }

        sql.Append(") q");
        if (_execution.After is { } after)
        {
            sql.Append(" WHERE ").Append(Keyset(query, keys, after));
        }

        sql.Append(" ORDER BY ");
        if (keys.Count == 0)
        {
            sql.Append("(SELECT NULL)");
        }

        for (int i = 0; i < keys.Count; i++)
        {
            SortItem sort = query.OrderBy[i];
            string direction = sort.Descending ? " DESC" : " ASC";
            string nulls = $"CASE WHEN q.k{i}{(keys[i].Position ? "_c" : string.Empty)} IS NULL THEN {(NullsFirst(sort) ? 0 : 1)} ELSE {(NullsFirst(sort) ? 1 : 0)} END";
            if (i > 0)
            {
                sql.Append(", ");
            }

            sql.Append(nulls).Append(", ");
            sql.Append(keys[i].Position ? $"q.k{i}_c{direction}, q.k{i}_s{direction}" : $"q.k{i}{direction}");
        }

        sql.Append(" OFFSET ").Append(Parameter((long)_execution.Skip, SqlDbType.BigInt)).Append(" ROWS");
        sql.Append(" FETCH NEXT ").Append(Parameter((long)_execution.Take, SqlDbType.BigInt)).Append(" ROWS ONLY");
        return new CompiledQuery(sql.ToString(), _parameters, outputs, keys);
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

        if (type is QueryType.Duration or QueryType.Any)
        {
            throw Unsupported($"a {type.ToString().ToLowerInvariant()} result");
        }

        bool tags = expression is ColumnExpr c && Resolve(c, scope) is { Column: { Kind: ColumnKind.Attribute, Attribute: "tags" } };
        select.Add($"{Value(expression, scope)} AS {name}");
        shapes.Add(new ResultShape(column, type, Position: false, tags));
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
                on.Add($"({Value(equality.Left, scope)}) = ({Value(equality.Right, scope)})");
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
            where.Add($"({Condition(query.Ast.Where, scope)})");
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
                yield return $"{alias}.collection = {Parameter(_execution.CollectionOf(source.ReadModel!), SqlDbType.NVarChar)}";
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
                    yield return $"{alias}.event_type IN ({string.Join(", ", source.EventTypes.Select(t => Parameter(t.Name, SqlDbType.NVarChar)))})";
                }

                if (((EventsSource)source.Syntax).Version is { } version)
                {
                    yield return $"{alias}.type_version = ({Value(version, QueryType.Integer, scope)})";
                }

                if (scope.Query.Options.Consistency != QueryConsistency.Latest)
                {
                    yield return $"{alias}.rv < MIN_ACTIVE_ROWVERSION()";
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
            string isNull = key.Position ? $"q.k{i}_c IS NULL" : $"q.k{i} IS NULL";
            bool nullsFirst = NullsFirst(sort);
            string greater;
            string same;
            string op = sort.Descending ? "<" : ">";
            if (value is null)
            {
                greater = nullsFirst ? $"NOT ({isNull})" : "1 = 0";
                same = isNull;
            }
            else if (key.Position)
            {
                var position = (GlobalPosition)value;
                string c = Parameter(checked((long)position.Commit), SqlDbType.BigInt);
                string s = Parameter((long)Math.Min(position.Sequence, long.MaxValue), SqlDbType.BigInt);
                greater = $"q.k{i}_c {op} {c} OR (q.k{i}_c = {c} AND q.k{i}_s {op} {s})" + (nullsFirst ? string.Empty : $" OR {isNull}");
                same = $"q.k{i}_c = {c} AND q.k{i}_s = {s}";
            }
            else
            {
                string literal = Parameter(value, key.Type);
                greater = $"q.k{i} {op} {literal}" + (nullsFirst ? string.Empty : $" OR {isNull}");
                same = $"q.k{i} = {literal}";
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

    // ================================================================ conditions and values

    // A search condition (WHERE, HAVING, ON, CASE WHEN, AND/OR/NOT operands).
    private string Condition(Expr expr, Scope scope)
    {
        if (scope.Conditions.TryGetValue(expr, out string? cached))
        {
            return cached;
        }

        string sql = expr switch
        {
            UnaryExpr { Operator: UnaryOperator.Not } not => $"NOT ({Condition(not.Operand, scope)})",
            BinaryExpr { Operator: BinaryOperator.And } b => $"({Condition(b.Left, scope)}) AND ({Condition(b.Right, scope)})",
            BinaryExpr { Operator: BinaryOperator.Or } b => $"({Condition(b.Left, scope)}) OR ({Condition(b.Right, scope)})",
            BinaryExpr { Operator: BinaryOperator.Xor } b => $"({Value(b.Left, scope)}) <> ({Value(b.Right, scope)})",
            BinaryExpr { Operator: BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.Less or BinaryOperator.LessOrEqual or BinaryOperator.Greater or BinaryOperator.GreaterOrEqual } b => Comparison(b, scope),
            LikeExpr like => $"({Value(like.Value, scope)}) {(like.Negated ? "NOT LIKE" : "LIKE")} ({Pattern(like.Pattern, scope)})",
            InListExpr list => InList(list, scope),
            InSubqueryExpr subquery => $"({Value(subquery.Value, scope)}) {(subquery.Negated ? "NOT IN" : "IN")} ({Subquery(subquery.Subquery, scope, exists: false)})",
            ExistsSubqueryExpr exists => $"{(exists.Negated ? "NOT EXISTS" : "EXISTS")} ({Subquery(exists.Subquery, scope, exists: true)})",
            ExistsExpr exists => Exists(exists.Column, scope),
            BetweenExpr between => Between(between, scope),
            IsNullExpr isNull => $"({Value(isNull.Value, scope)}) {(isNull.Negated ? "IS NOT NULL" : "IS NULL")}",
            CallExpr { Name: "has_tag" } call => HasTag(call, scope),
            _ => $"({Value(expr, scope)}) = 1",   // a boolean value
        };
        scope.Conditions[expr] = sql;
        return sql;
    }

    // A scalar; a boolean is a bit (NULL when unknown).
    private string Value(Expr expr, Scope scope)
    {
        if (!scope.Values.TryGetValue(expr, out string? sql))
        {
            sql = CompileValue(expr, scope);
            scope.Values[expr] = sql;
        }

        return sql;
    }

    private string CompileValue(Expr expr, Scope scope)
    {
        switch (expr)
        {
            case LiteralExpr or ParameterExpr:
                return Value(expr, TypeOf(expr, scope), scope);
            case ColumnExpr column:
                return Column(column, scope);
            case UnaryExpr { Operator: UnaryOperator.Not } or LikeExpr or InListExpr or InSubqueryExpr or ExistsSubqueryExpr or ExistsExpr or BetweenExpr or IsNullExpr
                or BinaryExpr { Operator: BinaryOperator.And or BinaryOperator.Or or BinaryOperator.Xor or BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.Less or BinaryOperator.LessOrEqual or BinaryOperator.Greater or BinaryOperator.GreaterOrEqual }
                or CallExpr { Name: "has_tag" }:
            {
                string condition = Condition(expr, scope);
                return $"CAST(CASE WHEN {condition} THEN 1 WHEN NOT ({condition}) THEN 0 END AS bit)";
            }

            case UnaryExpr negate:
                return $"-({Value(negate.Operand, scope)})";
            case BinaryExpr binary:
                return Arithmetic(binary, scope);
            case CallExpr call:
                return Call(call, scope);
            case CastExpr cast:
                return Cast(Value(cast.Value, scope), TypeOf(cast.Value, scope), cast.Type);
            case CaseExpr @case:
            {
                var sql = new StringBuilder("CASE");
                foreach (CaseBranch branch in @case.Branches)
                {
                    sql.Append(" WHEN ").Append(Condition(branch.When, scope)).Append(" THEN ").Append(Value(branch.Then, scope));
                }

                if (@case.Else is not null)
                {
                    sql.Append(" ELSE ").Append(Value(@case.Else, scope));
                }

                return sql.Append(" END").ToString();
            }

            default:
                throw Unsupported("expression");
        }
    }

    private string Arithmetic(BinaryExpr binary, Scope scope)
    {
        QueryType leftType = TypeOf(binary.Left, scope);
        QueryType rightType = TypeOf(binary.Right, scope);
        if (leftType == QueryType.Timestamp || rightType == QueryType.Timestamp)
        {
            // timestamp ± a duration value: DATEADD; other timestamp arithmetic runs in the interpreter.
            if (binary.Operator is not (BinaryOperator.Add or BinaryOperator.Subtract) || leftType != QueryType.Timestamp || DurationOf(binary.Right) is not { } duration)
            {
                throw Unsupported("this timestamp arithmetic");
            }

            return AddDuration(Operand(binary.Left, QueryType.Timestamp, scope), binary.Operator == BinaryOperator.Add ? duration : -duration);
        }

        if (leftType == QueryType.Duration || rightType == QueryType.Duration)
        {
            throw Unsupported("duration arithmetic");
        }

        string left = Operand(binary.Left, leftType, scope);
        string right = Operand(binary.Right, rightType, scope);
        return binary.Operator switch
        {
            BinaryOperator.Add => $"({left}) + ({right})",
            BinaryOperator.Subtract => $"({left}) - ({right})",
            BinaryOperator.Multiply => $"({left}) * ({right})",
            BinaryOperator.Divide => $"CAST(({left}) AS {Decimal}) / NULLIF(CAST(({right}) AS {Decimal}), 0)",
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
            return op switch
            {
                "=" => $"({lc}) = ({rc}) AND ({ls}) = ({rs})",
                "<>" => $"(({lc}) <> ({rc}) OR ({ls}) <> ({rs}))",
                _ => $"(({lc}) {op[0]} ({rc}) OR (({lc}) = ({rc}) AND ({ls}) {op} ({rs})))",
            };
        }

        if (type == QueryType.Duration)
        {
            throw Unsupported("duration comparisons");
        }

        return $"({Operand(binary.Left, type, scope)}) {op} ({Operand(binary.Right, type, scope)})";
    }

    private string InList(InListExpr list, Scope scope)
    {
        QueryType type = TypeOf(list.Value, scope);
        return $"({Value(list.Value, scope)}) {(list.Negated ? "NOT IN" : "IN")} ({string.Join(", ", list.Items.Select(i => Operand(i, type, scope)))})";
    }

    private string Between(BetweenExpr between, Scope scope)
    {
        QueryType type = TypeOf(between.Value, scope);
        return $"({Value(between.Value, scope)}) {(between.Negated ? "NOT BETWEEN" : "BETWEEN")} ({Operand(between.Low, type, scope)}) AND ({Operand(between.High, type, scope)})";
    }

    // LIKE in T-SQL also treats [ ] as wildcards: a bracket in the pattern is escaped as [[].
    private string Pattern(Expr pattern, Scope scope)
    {
        if (pattern is LiteralExpr or ParameterExpr)
        {
            object? value = Supplied(pattern);
            return value is string text
                ? Parameter(text.Replace("[", "[[]", StringComparison.Ordinal), SqlDbType.NVarChar) + Bin2
                : Value(pattern, QueryType.String, scope);
        }

        return $"REPLACE({Value(pattern, scope)}, N'[', N'[[]')";
    }

    // A value in the context of another operand's type (a string literal compared with a timestamp becomes a timestamp).
    private string Operand(Expr expr, QueryType context, Scope scope)
    {
        return expr is LiteralExpr or ParameterExpr ? Value(expr, context, scope) : Value(expr, scope);
    }

    private object? Supplied(Expr expr)
    {
        return expr switch
        {
            LiteralExpr literal => literal.Value,
            ParameterExpr parameter => _execution.Parameters.TryGetValue(parameter.Name, out object? supplied) ? supplied : null,
            _ => null,
        };
    }

    private string Value(Expr expr, QueryType context, Scope scope)
    {
        if (expr is not (LiteralExpr or ParameterExpr))
        {
            return Value(expr, scope);
        }

        object? value = Supplied(expr);
        if (value is null)
        {
            return context switch
            {
                QueryType.Integer => "CAST(NULL AS bigint)",
                QueryType.Decimal => $"CAST(NULL AS {Decimal})",
                QueryType.Boolean => "CAST(NULL AS bit)",
                QueryType.Timestamp => "CAST(NULL AS datetimeoffset(7))",
                QueryType.Duration => throw Unsupported("a duration value"),
                _ => "CAST(NULL AS nvarchar(max))",
            };
        }

        if (value is GlobalPosition)
        {
            throw Unsupported("a position outside comparisons");
        }

        return Parameter(Coerce(value, context), context);
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
            string s => Parameter(s, SqlDbType.NVarChar) + Bin2,
            long l => Parameter(l, SqlDbType.BigInt),
            int i => Parameter((long)i, SqlDbType.BigInt),
            decimal d => Parameter(d, SqlDbType.Decimal),
            bool b => Parameter(b, SqlDbType.Bit),
            DateTimeOffset t => Parameter(t.ToUniversalTime(), SqlDbType.DateTimeOffset),
            _ => throw Unsupported($"a {context.ToString().ToLowerInvariant()} value"),
        };
    }

    private string Parameter(object value, SqlDbType type)
    {
        string name = "@p" + (_parameters.Count + 1).ToString(CultureInfo.InvariantCulture);
        var parameter = new SqlParameter(name, type) { Value = value };
        switch (type)
        {
            case SqlDbType.NVarChar:
                parameter.Size = value is string s && s.Length > 4000 ? -1 : 4000;
                break;
            case SqlDbType.Decimal:
                // The value's own precision: a wider declared type would cost scale in COALESCE, CASE and arithmetic.
                var exact = new System.Data.SqlTypes.SqlDecimal((decimal)value);
                parameter.Precision = Math.Max(exact.Precision, (byte)1);
                parameter.Scale = exact.Scale;
                break;
            case SqlDbType.DateTimeOffset:
                parameter.Scale = 7;
                break;
        }

        _parameters.Add(parameter);
        return name;
    }

    private (string Commit, string Sequence) PositionPair(Expr expr, Scope scope)
    {
        switch (expr)
        {
            case ColumnExpr column when Resolve(column, scope) is { Column.Kind: ColumnKind.Attribute, Column.Attribute: "position" } resolved:
                return ($"CAST({resolved.Alias}.rv AS bigint)", $"{resolved.Alias}.seq");
            case ColumnExpr column when Resolve(column, scope) is { Column.Kind: ColumnKind.Output } output:
                return PositionPair(output.Scope.Query.Outputs[output.Column.Output].Expression, output.Scope);
            case LiteralExpr or ParameterExpr:
            {
                object? value = Supplied(expr);
                if (value is string text && GlobalPosition.TryParse(text, out GlobalPosition parsed))
                {
                    value = parsed;
                }

                if (value is null)
                {
                    return ("CAST(NULL AS bigint)", "CAST(NULL AS bigint)");
                }

                return value is GlobalPosition position
                    ? (Parameter(checked((long)position.Commit), SqlDbType.BigInt), Parameter((long)Math.Min(position.Sequence, long.MaxValue), SqlDbType.BigInt))
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
            return "1 = 1";
        }

        if (resolved.Column.Path.Count == 0 || resolved.Column.VersionPaths is not null)
        {
            throw Unsupported("exists() on this path");
        }

        // A present member, including one holding JSON null (as the interpreter's exists()).
        return $"EXISTS (SELECT 1 FROM OPENJSON({resolved.Alias}.data, {Path(resolved.Column.Path.Take(resolved.Column.Path.Count - 1).ToList())}) j WHERE j.[key] = {Parameter(resolved.Column.Path[^1].ToString()!, SqlDbType.NVarChar)}{Bin2})";
    }

    private string HasTag(CallExpr call, Scope scope)
    {
        string alias = scope.Aliases[scope.Query.Sources.First(s => s.IsEventLike).Index];
        return $"EXISTS (SELECT 1 FROM OPENJSON({alias}.tags) j WHERE j.[value]{Bin2} = ({Value(call.Arguments[0], QueryType.String, scope)}))";
    }

    private string Subquery(QueryAst ast, Scope outer, bool exists)
    {
        BoundQuery bound = outer.Query.Subquery(ast);
        var scope = new Scope(bound, NewAliases(bound), outer);
        var sql = new StringBuilder("SELECT ");
        sql.Append(exists ? "1" : Value(bound.Outputs[0].Expression, scope));
        AppendFromWhere(sql, scope);
        return sql.ToString();
    }

    private string Call(CallExpr call, Scope scope)
    {
        IReadOnlyList<Expr> args = call.Arguments;
        string Arg(int i) => Value(args[i], scope);
        QueryType ArgType(int i) => TypeOf(args[i], scope);
        string distinct = call.Distinct ? "DISTINCT " : string.Empty;
        switch (call.Name)
        {
            case "count":
                return args.Count == 0 ? "COUNT_BIG(*)" : $"COUNT_BIG({distinct}{Arg(0)})";
            case "sum":
                return $"SUM({distinct}{Arg(0)})";
            case "avg":
                return $"AVG({distinct}CAST({Arg(0)} AS {Decimal}))";
            case "min" or "max":
                return ArgType(0) == QueryType.Boolean ? throw Unsupported("min/max of booleans") : $"{call.Name.ToUpperInvariant()}({distinct}{Arg(0)})";
            case "now":
                return Now();
            case "ago":
                return DurationOf(args[0]) is { } ago ? AddDuration(Now(), -ago) : throw Unsupported("ago() of a computed duration");
            case "bin":
            {
                if (DurationOf(args[1]) is not { } width || width <= TimeSpan.Zero)
                {
                    throw Unsupported("bin() of a computed duration");
                }

                (string part, long count) = width.Ticks % TimeSpan.TicksPerSecond == 0 && width.TotalSeconds <= int.MaxValue
                    ? ("second", (long)width.TotalSeconds)
                    : width.Ticks % TimeSpan.TicksPerMillisecond == 0 && width.TotalMilliseconds <= int.MaxValue ? ("millisecond", (long)width.TotalMilliseconds) : throw Unsupported("this bin() width");
                return $"DATE_BUCKET({part}, {count.ToString(CultureInfo.InvariantCulture)}, ({Operand(args[0], QueryType.Timestamp, scope)}), CAST('1970-01-01T00:00:00+00:00' AS datetimeoffset(7)))";
            }

            case "coalesce":
            {
                QueryType type = args.Select(a => TypeOf(a, scope)).FirstOrDefault(t => t != QueryType.Any);
                return $"COALESCE({string.Join(", ", args.Select(a => Operand(a, type, scope)))})";
            }

            case "lower" or "upper":
                return $"{call.Name.ToUpperInvariant()}({Arg(0)})";
            case "trim":
                return $"TRIM({Arg(0)})";
            case "length":
                return $"CAST(LEN(({Arg(0)}) + N'.') - 1 AS bigint)";
            case "concat":
                return "(" + string.Join(" + ", args.Select((_, i) => Text(Arg(i), ArgType(i)))) + ")";
            case "concat_ws":
                return $"CONCAT_WS({string.Join(", ", args.Select((_, i) => Text(Arg(i), ArgType(i))))})";
            case "substring":
                return args.Count > 2
                    ? $"SUBSTRING({Arg(0)}, CAST(IIF(({Arg(1)}) < 1, 1, ({Arg(1)})) AS int), CAST(IIF(({Arg(2)}) < 0, 0, ({Arg(2)})) AS int))"
                    : $"SUBSTRING({Arg(0)}, CAST(IIF(({Arg(1)}) < 1, 1, ({Arg(1)})) AS int), 2147483647)";
            case "left" or "right":
                return $"{call.Name.ToUpperInvariant()}({Arg(0)}, CAST(IIF(({Arg(1)}) < 0, 0, ({Arg(1)})) AS int))";
            case "abs":
                return $"ABS({Arg(0)})";
            case "floor" or "ceiling":
                return ArgType(0) == QueryType.Integer ? Arg(0) : $"{call.Name.ToUpperInvariant()}(CAST(({Arg(0)}) AS {Decimal}))";
            case "round":
                return args.Count > 1 ? $"ROUND(CAST(({Arg(0)}) AS {Decimal}), CAST(({Arg(1)}) AS int))" : $"ROUND(CAST(({Arg(0)}) AS {Decimal}), 0)";
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

    // Text of a value, for concatenation: only types whose T-SQL text matches the interpreter's.
    private static string Text(string sql, QueryType type)
    {
        return type switch
        {
            QueryType.String or QueryType.Uuid or QueryType.Any => sql,
            QueryType.Integer => $"CAST(({sql}) AS nvarchar(40)){Bin2}",
            _ => throw Unsupported($"text of a {type.ToString().ToLowerInvariant()}"),
        };
    }

    private static string Cast(string sql, QueryType from, string to)
    {
        return to switch
        {
            "string" => Text(sql, from),
            "integer" => from switch
            {
                QueryType.Integer => sql,
                QueryType.Decimal => $"CAST(ROUND(({sql}), 0, 1) AS bigint)",
                QueryType.Boolean => $"CAST(({sql}) AS bigint)",
                _ => $"CAST(({sql}) AS bigint)",
            },
            "decimal" => $"CAST(({sql}) AS {Decimal})",
            "boolean" => from switch
            {
                QueryType.Integer or QueryType.Decimal => $"CAST(CASE WHEN ({sql}) <> 0 THEN 1 WHEN ({sql}) = 0 THEN 0 END AS bit)",
                QueryType.Boolean => sql,
                _ => $"CAST(CASE LOWER({sql}) WHEN N'true' THEN 1 WHEN N'false' THEN 0 END AS bit)",
            },
            "timestamp" => from == QueryType.Timestamp ? sql : $"CONVERT(datetimeoffset(7), ({sql}), 127)",
            _ => $"LOWER(CONVERT(nvarchar(36), CAST(({sql}) AS uniqueidentifier))){Bin2}",
        };
    }

    // ================================================================ columns

    private string Column(ColumnExpr expr, Scope scope)
    {
        Resolved resolved = Resolve(expr, scope);
        BoundColumn column = resolved.Column;
        if (column.Kind == ColumnKind.Output)
        {
            return Value(resolved.Scope.Query.Outputs[column.Output].Expression, resolved.Scope);
        }

        string a = resolved.Alias;
        if (column.Kind is ColumnKind.Data or ColumnKind.Field)
        {
            return Data(resolved, column.Type);
        }

        if (resolved.Source.Kind == SourceKind.ReadModel)
        {
            return column.Attribute == "id" ? $"{a}.id{Bin2}" : $"{a}.version";
        }

        return column.Attribute switch
        {
            "id" => $"LOWER(CONVERT(nvarchar(36), {a}.event_id)){Bin2}",
            "type" => $"{a}.event_type{Bin2}",
            "typeversion" => $"CAST({a}.type_version AS bigint)",
            "source" => $"JSON_VALUE({a}.metadata, '$.source'){Bin2}",
            "stream" => $"{a}.stream_id{Bin2}",
            "aggregatekey" => $"RIGHT({a}.stream_id, CHARINDEX(N'-', REVERSE({a}.stream_id) + N'-') - 1){Bin2}",
            "version" => $"{a}.stream_version",
            "time" => $"{a}.created_at",
            "sfcorrelationid" => $"JSON_VALUE({a}.metadata, '$.correlationId'){Bin2}",
            "sfcausationid" => $"JSON_VALUE({a}.metadata, '$.causationId'){Bin2}",
            "tenant" => $"{a}.tenant_id{Bin2}",
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

            sql.Append($" WHEN {a}.event_type = {Parameter(eventType, SqlDbType.NVarChar)} AND {a}.type_version = {Parameter((long)version, SqlDbType.BigInt)} THEN {Extract(a, path, type)}");
        }

        return sql.Append(" END").ToString();
    }

    private static string Extract(string alias, IReadOnlyList<object> path, QueryType type)
    {
        string p = Path(path);
        string scalar = $"JSON_VALUE({alias}.data, {p})";
        return type switch
        {
            QueryType.Object or QueryType.Array => $"JSON_QUERY({alias}.data, {p})",
            QueryType.Any => throw Unsupported("an untyped data path"),
            QueryType.Integer => $"CAST({scalar} AS bigint)",
            QueryType.Decimal => $"CAST({scalar} AS {Decimal})",
            QueryType.Boolean => $"CAST(CASE {scalar} WHEN 'true' THEN 1 WHEN 'false' THEN 0 END AS bit)",
            QueryType.Timestamp => $"CONVERT(datetimeoffset(7), {scalar}, 127)",
            QueryType.Duration => throw Unsupported("a duration data path"),
            _ => $"{scalar}{Bin2}",
        };
    }

    // A JSON path literal: keys from this service's catalog, never from the request; quoted and escaped regardless.
    private static string Path(IReadOnlyList<object> path)
    {
        var sb = new StringBuilder("$");
        foreach (object segment in path)
        {
            if (segment is int index)
            {
                sb.Append('[').Append(index.ToString(CultureInfo.InvariantCulture)).Append(']');
                continue;
            }

            string key = (string)segment;
            if (key.Any(char.IsControl))
            {
                throw Unsupported("a JSON key with control characters");
            }

            sb.Append(".\"").Append(key.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
        }

        return "N'" + sb.ToString().Replace("'", "''", StringComparison.Ordinal) + "'";
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

    // A duration known at compile time (a literal or a supplied parameter).
    private TimeSpan? DurationOf(Expr expr)
    {
        return Supplied(expr) switch
        {
            TimeSpan span => span,
            string text when TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out TimeSpan span) => span,
            _ => null,
        };
    }

    // DATEADD takes an int: whole seconds, then the millisecond remainder.
    private string AddDuration(string timestamp, TimeSpan duration)
    {
        long milliseconds = (long)duration.TotalMilliseconds;
        long seconds = milliseconds / 1000;
        long rest = milliseconds % 1000;
        if (Math.Abs(seconds) > int.MaxValue)
        {
            throw Unsupported("a duration over 68 years");
        }

        string sql = $"DATEADD(second, CAST({Parameter(seconds, SqlDbType.BigInt)} AS int), {timestamp})";
        return rest == 0 ? sql : $"DATEADD(millisecond, CAST({Parameter(rest, SqlDbType.BigInt)} AS int), {sql})";
    }

    private string Now()
    {
        return _now ??= Parameter(_execution.Now.ToUniversalTime(), SqlDbType.DateTimeOffset);
    }

    private string Tenant()
    {
        return _tenant ??= Parameter(_execution.Tenant, SqlDbType.NVarChar);
    }

    private string[] NewAliases(BoundQuery query)
    {
        return [.. query.Sources.Select(_ => "t" + (_aliases++).ToString(CultureInfo.InvariantCulture))];
    }

    private static QueryException Unsupported(string what)
    {
        return new QueryException(QueryCodes.Unsupported, $"The SQL Server query compiler does not support {what}.");
    }

    private sealed record Scope(BoundQuery Query, string[] Aliases, Scope? Outer)
    {
        public Dictionary<Expr, string> Values { get; } = new(ReferenceEqualityComparer.Instance);

        public Dictionary<Expr, string> Conditions { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private sealed record Resolved(BoundColumn Column, Scope Scope, BoundSource Source, string Alias);
}

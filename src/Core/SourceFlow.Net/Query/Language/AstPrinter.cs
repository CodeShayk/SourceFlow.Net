using System.Globalization;
using System.Text;

namespace SourceFlow.Query;

/// <summary>
/// Prints a <see cref="QueryAst"/> canonically, without positions or the surface syntax, so that equivalent pipeline and
/// SQL queries print identically (UT-QL-69..78) and golden tests are readable.
/// </summary>
internal static class AstPrinter
{
    public static string Print(QueryAst query)
    {
        var text = new StringBuilder();
        text.Append("from ").Append(Source(query.Source));
        foreach (JoinClause join in query.Joins)
        {
            text.Append(" ").Append(join.Kind == JoinKind.Left ? "left" : "inner").Append(" join ").Append(Source(join.Source)).Append(" on ")
                .Append(string.Join(" and ", join.On.Select(e => $"{Expr(e.Left)} = {Expr(e.Right)}")));
        }

        if (query.Where is not null)
        {
            text.Append(" where ").Append(Expr(query.Where));
        }

        if (query.GroupBy.Count > 0)
        {
            text.Append(" group by ").Append(string.Join(", ", query.GroupBy.Select(Expr)));
        }

        if (query.Having is not null)
        {
            text.Append(" having ").Append(Expr(query.Having));
        }

        text.Append(" select ").Append(query.Distinct ? "distinct " : "");
        text.Append(query.Select is null ? "<default>" : string.Join(", ", query.Select.Select(Item)));
        if (query.OrderBy.Count > 0)
        {
            text.Append(" order by ").Append(string.Join(", ", query.OrderBy.Select(s =>
                Expr(s.Expression) + (s.Descending ? " desc" : " asc") + s.Nulls switch { NullsOrder.First => " nulls first", NullsOrder.Last => " nulls last", _ => "" })));
        }

        if (query.Limit is not null)
        {
            text.Append(" limit ").Append(Expr(query.Limit));
        }

        if (query.Offset is not null)
        {
            text.Append(" offset ").Append(Expr(query.Offset));
        }

        if (query.Options.Count > 0)
        {
            text.Append(" options(").Append(string.Join(", ", query.Options.Select(o => $"{o.Name}={Expr(o.Value)}"))).Append(')');
        }

        return text.ToString();
    }

    private static string Source(SourceRef source)
    {
        string body = source switch
        {
            EventsSource e => "events" + (e.Types.Count == 0 ? "" : " of " + string.Join(", ", e.Types.Select(Expr)))
                + (e.Version is null ? "" : " version " + Expr(e.Version)) + (e.Upcast ? " upcast" : ""),
            StreamSource s => "stream " + Expr(s.Stream),
            ReadModelSource r => "readmodel " + Expr(r.Collection),
            AggregateSource a => $"aggregate {Expr(a.Name)} id {Expr(a.Id)}" + (a.Bound is null ? "" : $" as of {a.AsOf.ToString().ToLowerInvariant()} {Expr(a.Bound)}"),
            SchemaSource s => "schema" + (s.Type is null ? "" : " of " + Expr(s.Type)),
            _ => "?",
        };
        return source.Alias is null ? body : $"{body} as {source.Alias}";
    }

    private static string Item(SelectItem item)
    {
        if (item.IsStar)
        {
            return item.StarQualifier!.Length == 0 ? "*" : item.StarQualifier + ".*";
        }

        return Expr(item.Expression!) + (item.Alias is null ? "" : " as " + item.Alias);
    }

    public static string Expr(Expr expr)
    {
        return expr switch
        {
            LiteralExpr l => Literal(l),
            ParameterExpr p => "@" + p.Name,
            ColumnExpr c => (c.Qualifier is null ? "" : c.Qualifier + ".") + c.PathText,
            UnaryExpr u => u.Operator == UnaryOperator.Not ? $"(not {Expr(u.Operand)})" : $"(-{Expr(u.Operand)})",
            BinaryExpr b => $"({Expr(b.Left)} {Operator(b.Operator)} {Expr(b.Right)})",
            LikeExpr l => $"({Expr(l.Value)}{(l.Negated ? " not" : "")} like {Expr(l.Pattern)})",
            InListExpr i => $"({Expr(i.Value)}{(i.Negated ? " not" : "")} in ({string.Join(", ", i.Items.Select(Expr))}))",
            InSubqueryExpr s => $"({Expr(s.Value)}{(s.Negated ? " not" : "")} in ({Print(s.Subquery)}))",
            ExistsSubqueryExpr s => $"({(s.Negated ? "not " : "")}exists ({Print(s.Subquery)}))",
            ExistsExpr e => $"(exists {Expr(e.Column)})",
            BetweenExpr b => $"({Expr(b.Value)}{(b.Negated ? " not" : "")} between {Expr(b.Low)} and {Expr(b.High)})",
            IsNullExpr n => $"({Expr(n.Value)} is{(n.Negated ? " not" : "")} null)",
            CallExpr c => $"{c.Name}({(c.Distinct ? "distinct " : "")}{string.Join(", ", c.Arguments.Select(Expr))})",
            CastExpr c => $"cast({Expr(c.Value)} as {c.Type})",
            CaseExpr c => "case" + string.Concat(c.Branches.Select(b => $" when {Expr(b.When)} then {Expr(b.Then)}")) + (c.Else is null ? "" : " else " + Expr(c.Else)) + " end",
            _ => "?",
        };
    }

    private static string Literal(LiteralExpr literal)
    {
        return literal.Kind switch
        {
            LiteralKind.Null => "null",
            LiteralKind.Boolean => (bool)literal.Value! ? "true" : "false",
            LiteralKind.Integer => ((long)literal.Value!).ToString(CultureInfo.InvariantCulture),
            LiteralKind.Decimal => ((decimal)literal.Value!).ToString(CultureInfo.InvariantCulture) + "m",
            LiteralKind.String => "'" + ((string)literal.Value!).Replace("'", "''", StringComparison.Ordinal) + "'",
            LiteralKind.Duration => "duration(" + ((TimeSpan)literal.Value!).ToString("c", CultureInfo.InvariantCulture) + ")",
            LiteralKind.DateTime => "datetime('" + ((DateTimeOffset)literal.Value!).ToString("O", CultureInfo.InvariantCulture) + "')",
            _ => "?",
        };
    }

    private static string Operator(BinaryOperator op)
    {
        return op switch
        {
            BinaryOperator.Or => "or",
            BinaryOperator.Xor => "xor",
            BinaryOperator.And => "and",
            BinaryOperator.Equal => "=",
            BinaryOperator.NotEqual => "!=",
            BinaryOperator.Less => "<",
            BinaryOperator.LessOrEqual => "<=",
            BinaryOperator.Greater => ">",
            BinaryOperator.GreaterOrEqual => ">=",
            BinaryOperator.Add => "+",
            BinaryOperator.Subtract => "-",
            BinaryOperator.Multiply => "*",
            BinaryOperator.Divide => "/",
            _ => "%",
        };
    }
}

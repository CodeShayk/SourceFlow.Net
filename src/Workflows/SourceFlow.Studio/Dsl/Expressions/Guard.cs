using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SourceFlow.Studio.Dsl.Expressions
{
    /// <summary>
    /// A workflow guard: a boolean CESQL expression (doc 15 §5.2) over <c>$data.x</c>, <c>event.data.x</c> and
    /// <c>$failure.x</c>. One parser, binder and evaluator serve the validator, the simulator and the generator, which
    /// translates the same tree to C#.
    /// </summary>
    public abstract class GuardExpr
    {
        private protected GuardExpr(int position)
        {
            Position = position;
        }

        /// <summary>The 0-based offset in the guard text.</summary>
        public int Position { get; }
    }

    /// <summary>A literal: a string, a decimal number, a boolean or null.</summary>
    public sealed class GuardLiteral : GuardExpr
    {
        internal GuardLiteral(object? value, int position)
            : base(position)
        {
            Value = value;
        }

        /// <summary>The value: <see cref="string"/>, <see cref="decimal"/>, <see cref="bool"/> or null.</summary>
        public object? Value { get; }
    }

    /// <summary>A path such as <c>$data.amount</c>.</summary>
    public sealed class GuardPath : GuardExpr
    {
        internal GuardPath(string path, int position)
            : base(position)
        {
            Path = path;
        }

        /// <summary>The path text.</summary>
        public string Path { get; }
    }

    /// <summary>A unary operator: <c>NOT</c> or <c>-</c>.</summary>
    public sealed class GuardUnary : GuardExpr
    {
        internal GuardUnary(string op, GuardExpr operand, int position)
            : base(position)
        {
            Operator = op;
            Operand = operand;
        }

        /// <summary><c>NOT</c> or <c>-</c>.</summary>
        public string Operator { get; }

        /// <summary>The operand.</summary>
        public GuardExpr Operand { get; }
    }

    /// <summary>A binary operator: <c>AND OR = != &lt; &lt;= &gt; &gt;= + - * / %</c> and <c>LIKE</c>.</summary>
    public sealed class GuardBinary : GuardExpr
    {
        internal GuardBinary(string op, GuardExpr left, GuardExpr right, int position)
            : base(position)
        {
            Operator = op;
            Left = left;
            Right = right;
        }

        /// <summary>The operator, normalised (<c>&lt;&gt;</c> is <c>!=</c>; keywords upper case).</summary>
        public string Operator { get; }

        /// <summary>The left operand.</summary>
        public GuardExpr Left { get; }

        /// <summary>The right operand.</summary>
        public GuardExpr Right { get; }
    }

    /// <summary><c>x [NOT] IN (a, b, …)</c>.</summary>
    public sealed class GuardIn : GuardExpr
    {
        internal GuardIn(GuardExpr value, IReadOnlyList<GuardExpr> items, bool negated, int position)
            : base(position)
        {
            Value = value;
            Items = items;
            Negated = negated;
        }

        /// <summary>The tested value.</summary>
        public GuardExpr Value { get; }

        /// <summary>The candidates.</summary>
        public IReadOnlyList<GuardExpr> Items { get; }

        /// <summary><c>NOT IN</c>.</summary>
        public bool Negated { get; }
    }

    /// <summary><c>EXISTS path</c>: the value is present and not null.</summary>
    public sealed class GuardExists : GuardExpr
    {
        internal GuardExists(GuardPath path, int position)
            : base(position)
        {
            Path = path;
        }

        /// <summary>The path.</summary>
        public GuardPath Path { get; }
    }

    /// <summary>A parse result.</summary>
    public sealed class GuardParseResult
    {
        internal GuardParseResult(GuardExpr? expression, string? error, int errorPosition)
        {
            Expression = expression;
            Error = error;
            ErrorPosition = errorPosition;
        }

        /// <summary>The tree, or null on error.</summary>
        public GuardExpr? Expression { get; }

        /// <summary>The error, or null.</summary>
        public string? Error { get; }

        /// <summary>Where the error is (0-based).</summary>
        public int ErrorPosition { get; }
    }

    /// <summary>Parses guards: <c>OR AND NOT</c>, comparisons, <c>LIKE</c>, <c>IN</c>, <c>EXISTS</c>, arithmetic, parentheses; keywords in any case.</summary>
    public static class GuardParser
    {
        /// <summary>Parses <paramref name="text"/>.</summary>
        public static GuardParseResult Parse(string text)
        {
            if (text is null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            var parser = new Parser();
            try
            {
                parser.Load(text);
                GuardExpr expression = parser.Expression();
                parser.ExpectEnd();
                return new GuardParseResult(expression, null, 0);
            }
            catch (FormatException ex)
            {
                return new GuardParseResult(null, ex.Message, parser.ErrorPosition);
            }
        }

        private sealed class Parser
        {
            private const int MaxDepth = 32;
            private List<Token> _tokens = new List<Token>();
            private int _index;
            private int _depth;

            public void Load(string text)
            {
                _tokens = Lex(text);
            }

            public int ErrorPosition { get; private set; }

            private Token Current => _tokens[_index];

            public GuardExpr Expression()
            {
                if (++_depth > MaxDepth)
                {
                    throw Fail("the guard is nested too deeply");
                }

                GuardExpr left = And();
                while (Keyword("OR"))
                {
                    int position = _tokens[_index - 1].Position;
                    left = new GuardBinary("OR", left, And(), position);
                }

                _depth--;
                return left;
            }

            public void ExpectEnd()
            {
                if (Current.Kind != TokenKind.End)
                {
                    throw Fail("unexpected '" + Current.Text + "'");
                }
            }

            private GuardExpr And()
            {
                GuardExpr left = Not();
                while (Keyword("AND"))
                {
                    int position = _tokens[_index - 1].Position;
                    left = new GuardBinary("AND", left, Not(), position);
                }

                return left;
            }

            private GuardExpr Not()
            {
                if (Keyword("NOT"))
                {
                    int position = _tokens[_index - 1].Position;
                    return new GuardUnary("NOT", Not(), position);
                }

                return Comparison();
            }

            private GuardExpr Comparison()
            {
                GuardExpr left = Additive();
                if (Current.Kind == TokenKind.Operator && IsComparison(Current.Text))
                {
                    Token op = Next();
                    return new GuardBinary(op.Text == "<>" ? "!=" : op.Text, left, Additive(), op.Position);
                }

                int at = Current.Position;
                bool negated = Keyword("NOT");
                if (Keyword("LIKE"))
                {
                    GuardExpr pattern = Additive();
                    GuardExpr like = new GuardBinary("LIKE", left, pattern, at);
                    return negated ? new GuardUnary("NOT", like, at) : like;
                }

                if (Keyword("IN"))
                {
                    Expect("(");
                    var items = new List<GuardExpr> { Additive() };
                    while (Accept(","))
                    {
                        items.Add(Additive());
                    }

                    Expect(")");
                    return new GuardIn(left, items, negated, at);
                }

                if (negated)
                {
                    throw Fail("expected LIKE or IN after NOT");
                }

                return left;
            }

            private GuardExpr Additive()
            {
                GuardExpr left = Multiplicative();
                while (Current.Kind == TokenKind.Operator && (Current.Text == "+" || Current.Text == "-"))
                {
                    Token op = Next();
                    left = new GuardBinary(op.Text, left, Multiplicative(), op.Position);
                }

                return left;
            }

            private GuardExpr Multiplicative()
            {
                GuardExpr left = Unary();
                while (Current.Kind == TokenKind.Operator && (Current.Text == "*" || Current.Text == "/" || Current.Text == "%"))
                {
                    Token op = Next();
                    left = new GuardBinary(op.Text, left, Unary(), op.Position);
                }

                return left;
            }

            private GuardExpr Unary()
            {
                if (Current.Kind == TokenKind.Operator && Current.Text == "-")
                {
                    Token op = Next();
                    return new GuardUnary("-", Unary(), op.Position);
                }

                return Primary();
            }

            private GuardExpr Primary()
            {
                Token token = Current;
                switch (token.Kind)
                {
                    case TokenKind.Number:
                        Next();
                        return new GuardLiteral(decimal.Parse(token.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture), token.Position);
                    case TokenKind.String:
                        Next();
                        return new GuardLiteral(token.Text, token.Position);
                    case TokenKind.Path:
                        Next();
                        return new GuardPath(token.Text, token.Position);
                    case TokenKind.Word:
                        string word = token.Text.ToUpperInvariant();
                        if (word == "TRUE" || word == "FALSE")
                        {
                            Next();
                            return new GuardLiteral(word == "TRUE", token.Position);
                        }

                        if (word == "NULL")
                        {
                            Next();
                            return new GuardLiteral(null, token.Position);
                        }

                        if (word == "EXISTS")
                        {
                            Next();
                            if (Current.Kind != TokenKind.Path)
                            {
                                throw Fail("EXISTS needs a path such as $data.reviewer");
                            }

                            Token path = Next();
                            return new GuardExists(new GuardPath(path.Text, path.Position), token.Position);
                        }

                        throw Fail("unknown name '" + token.Text + "'; paths start with $data., event.data. or $failure.");
                    case TokenKind.Operator when token.Text == "(":
                        Next();
                        GuardExpr inner = Expression();
                        Expect(")");
                        return inner;
                    case TokenKind.End:
                        throw Fail("the guard ends early");
                    default:
                        throw Fail("unexpected '" + token.Text + "'");
                }
            }

            private static bool IsComparison(string op)
            {
                return op == "=" || op == "!=" || op == "<>" || op == "<" || op == "<=" || op == ">" || op == ">=";
            }

            private bool Keyword(string keyword)
            {
                if (Current.Kind == TokenKind.Word && string.Equals(Current.Text, keyword, StringComparison.OrdinalIgnoreCase))
                {
                    _index++;
                    return true;
                }

                return false;
            }

            private bool Accept(string op)
            {
                if (Current.Kind == TokenKind.Operator && Current.Text == op)
                {
                    _index++;
                    return true;
                }

                return false;
            }

            private void Expect(string op)
            {
                if (!Accept(op))
                {
                    throw Fail("expected '" + op + "'");
                }
            }

            private Token Next()
            {
                return _tokens[_index++];
            }

            private FormatException Fail(string message)
            {
                ErrorPosition = Current.Position;
                return new FormatException(message);
            }

            private List<Token> Lex(string text)
            {
                var tokens = new List<Token>();
                int i = 0;
                while (i < text.Length)
                {
                    char c = text[i];
                    if (char.IsWhiteSpace(c))
                    {
                        i++;
                        continue;
                    }

                    int start = i;
                    if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
                    {
                        while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
                        {
                            i++;
                        }

                        string number = text.Substring(start, i - start);
                        if (!decimal.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _))
                        {
                            ErrorPosition = start;
                            throw new FormatException("'" + number + "' is not a number");
                        }

                        tokens.Add(new Token(TokenKind.Number, number, start));
                    }
                    else if (c == '\'' || c == '"')
                    {
                        var value = new StringBuilder();
                        i++;
                        while (true)
                        {
                            if (i >= text.Length)
                            {
                                ErrorPosition = start;
                                throw new FormatException("unterminated string");
                            }

                            if (text[i] == c)
                            {
                                if (i + 1 < text.Length && text[i + 1] == c)
                                {
                                    value.Append(c);   // doubled quote
                                    i += 2;
                                    continue;
                                }

                                i++;
                                break;
                            }

                            value.Append(text[i++]);
                        }

                        tokens.Add(new Token(TokenKind.String, value.ToString(), start));
                    }
                    else if (c == '$' || char.IsLetter(c) || c == '_')
                    {
                        i++;
                        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || (text[i] == '.' && i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] == '_'))))
                        {
                            i++;
                        }

                        string word = text.Substring(start, i - start);
                        bool path = word.IndexOf('.') > 0;
                        if (c == '$' && !path)
                        {
                            ErrorPosition = start;
                            throw new FormatException("'" + word + "' is not a path; use $data.<field> or $failure.code");
                        }

                        tokens.Add(new Token(path ? TokenKind.Path : TokenKind.Word, word, start));
                    }
                    else
                    {
                        string two = i + 1 < text.Length ? text.Substring(i, 2) : string.Empty;
                        if (two == "<=" || two == ">=" || two == "!=" || two == "<>")
                        {
                            tokens.Add(new Token(TokenKind.Operator, two, start));
                            i += 2;
                        }
                        else if ("=<>+-*/%(),".IndexOf(c) >= 0)
                        {
                            tokens.Add(new Token(TokenKind.Operator, c.ToString(), start));
                            i++;
                        }
                        else
                        {
                            ErrorPosition = start;
                            throw new FormatException("unexpected character '" + c + "'");
                        }
                    }
                }

                tokens.Add(new Token(TokenKind.End, string.Empty, text.Length));
                return tokens;
            }
        }

        private enum TokenKind
        {
            End,
            Number,
            String,
            Word,
            Path,
            Operator,
        }

        private readonly struct Token
        {
            public Token(TokenKind kind, string text, int position)
            {
                Kind = kind;
                Text = text;
                Position = position;
            }

            public TokenKind Kind { get; }

            public string Text { get; }

            public int Position { get; }
        }
    }

    /// <summary>
    /// Type-checks a guard against the paths in scope. Types are the manifest field types; strings, ids, dates and
    /// times compare as text, integers and numbers as numbers.
    /// </summary>
    public static class GuardBinder
    {
        /// <summary>Binds <paramref name="expression"/>; returns the error, or null when it is a valid boolean guard.</summary>
        /// <param name="expression">The parsed guard.</param>
        /// <param name="resolve">The type of a path, or null when it is not in scope.</param>
        /// <param name="scope">The paths that are in scope, for the error message.</param>
        public static string? Bind(GuardExpr expression, Func<string, string?> resolve, string scope)
        {
            if (expression is null)
            {
                throw new ArgumentNullException(nameof(expression));
            }

            try
            {
                string type = TypeOf(expression, resolve, scope);
                return type == "boolean" ? null : "the guard is " + type + ", not true or false";
            }
            catch (FormatException ex)
            {
                return ex.Message;
            }
        }

        private static string TypeOf(GuardExpr expression, Func<string, string?> resolve, string scope)
        {
            switch (expression)
            {
                case GuardLiteral literal:
                    return literal.Value switch
                    {
                        null => "null",
                        string _ => "string",
                        bool _ => "boolean",
                        _ => "number",
                    };
                case GuardPath path:
                    return Normalise(resolve(path.Path) ?? throw new FormatException("'" + path.Path + "' is not in scope here (" + scope + ")"));
                case GuardExists exists:
                    TypeOf(exists.Path, resolve, scope);
                    return "boolean";
                case GuardUnary { Operator: "NOT" } not:
                    Expect(TypeOf(not.Operand, resolve, scope), "boolean", "NOT");
                    return "boolean";
                case GuardUnary negate:
                    Expect(TypeOf(negate.Operand, resolve, scope), "number", "-");
                    return "number";
                case GuardIn @in:
                    string tested = TypeOf(@in.Value, resolve, scope);
                    foreach (GuardExpr item in @in.Items)
                    {
                        Comparable(tested, TypeOf(item, resolve, scope), "IN");
                    }

                    return "boolean";
                case GuardBinary binary:
                    string left = TypeOf(binary.Left, resolve, scope);
                    string right = TypeOf(binary.Right, resolve, scope);
                    switch (binary.Operator)
                    {
                        case "AND":
                        case "OR":
                            Expect(left, "boolean", binary.Operator);
                            Expect(right, "boolean", binary.Operator);
                            return "boolean";
                        case "LIKE":
                            Expect(left, "string", "LIKE");
                            Expect(right, "string", "LIKE");
                            return "boolean";
                        case "+":
                        case "-":
                        case "*":
                        case "/":
                        case "%":
                            Expect(left, "number", binary.Operator);
                            Expect(right, "number", binary.Operator);
                            return "number";
                        default:
                            Comparable(left, right, binary.Operator);
                            if ((binary.Operator != "=" && binary.Operator != "!=") && (left == "boolean" || right == "boolean"))
                            {
                                throw new FormatException("'" + binary.Operator + "' doesn't order true and false");
                            }

                            return "boolean";
                    }

                default:
                    throw new FormatException("unsupported expression");
            }
        }

        // The comparison families: number, string (text, ids, dates, times, uris, durations), boolean.
        private static string Normalise(string type)
        {
            switch (type)
            {
                case "integer":
                case "number":
                    return "number";
                case "boolean":
                    return "boolean";
                case "string":
                case "uuid":
                case "date-time":
                case "date":
                case "time":
                case "uri":
                case "duration":
                    return "string";
                default:
                    return type;   // array, object: not comparable
            }
        }

        private static void Expect(string actual, string expected, string op)
        {
            if (actual != expected && actual != "null")
            {
                throw new FormatException("'" + op + "' needs " + Describe(expected) + ", not " + Describe(actual));
            }
        }

        private static void Comparable(string left, string right, string op)
        {
            if (left == "null" || right == "null" || left == right)
            {
                if (left == "array" || left == "object")
                {
                    throw new FormatException("'" + op + "' can't compare " + Describe(left) + " values");
                }

                return;
            }

            throw new FormatException("'" + op + "' compares " + Describe(left) + " with " + Describe(right));
        }

        private static string Describe(string type)
        {
            return type == "boolean" ? "true/false" : type == "string" ? "text" : type == "number" ? "a number" : type;
        }
    }

    /// <summary>
    /// Evaluates guards over values: <see cref="string"/>, <see cref="decimal"/>, <see cref="bool"/> or null. Three-valued
    /// logic as in CESQL and SQL: a comparison with a missing (null) value is unknown, unknown propagates through NOT,
    /// AND and OR (Kleene), and a guard is true only when it evaluates to true. <c>x = null</c> and <c>x != null</c>
    /// test for a missing value. Errors such as a division by zero make the guard false.
    /// </summary>
    public static class GuardEvaluator
    {
        /// <summary>Evaluates <paramref name="expression"/> to true or false.</summary>
        /// <param name="expression">The parsed guard.</param>
        /// <param name="value">The value of a path, or null when missing.</param>
        public static bool Evaluate(GuardExpr expression, Func<string, object?> value)
        {
            if (expression is null)
            {
                throw new ArgumentNullException(nameof(expression));
            }

            try
            {
                return Eval(expression, value) is true;
            }
            catch (DivideByZeroException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        private static object? Eval(GuardExpr expression, Func<string, object?> value)
        {
            switch (expression)
            {
                case GuardLiteral literal:
                    return literal.Value;
                case GuardPath path:
                    return Normalise(value(path.Path));
                case GuardExists exists:
                    return Normalise(value(exists.Path.Path)) is not null;
                case GuardUnary { Operator: "NOT" } not:
                    return Eval(not.Operand, value) is bool b ? !b : (object?)null;
                case GuardUnary negate:
                    return Eval(negate.Operand, value) is decimal d ? -d : (object?)null;
                case GuardIn @in:
                    object? tested = Eval(@in.Value, value);
                    if (tested is null)
                    {
                        return null;
                    }

                    bool found = false;
                    foreach (GuardExpr item in @in.Items)
                    {
                        found |= Compare(tested, Eval(item, value)) == 0;
                    }

                    return found != @in.Negated;
                case GuardBinary binary:
                    return Binary(binary, value);
                default:
                    return null;
            }
        }

        private static object? Binary(GuardBinary binary, Func<string, object?> value)
        {
            if (binary.Operator == "AND")
            {
                object? l = Eval(binary.Left, value);
                if (l is false)
                {
                    return false;
                }

                object? r = Eval(binary.Right, value);
                return r is false ? false : l is true && r is true ? true : (object?)null;
            }

            if (binary.Operator == "OR")
            {
                object? l = Eval(binary.Left, value);
                if (l is true)
                {
                    return true;
                }

                object? r = Eval(binary.Right, value);
                return r is true ? true : l is false && r is false ? false : (object?)null;
            }

            object? left = Eval(binary.Left, value);
            object? right = Eval(binary.Right, value);
            if ((binary.Operator == "=" || binary.Operator == "!=") && (IsNullLiteral(binary.Left) || IsNullLiteral(binary.Right)))
            {
                bool bothNull = left is null && right is null;
                return binary.Operator == "=" ? bothNull : !bothNull;   // x = null: a test for a missing value
            }

            if (left is null || right is null)
            {
                return null;   // unknown
            }

            switch (binary.Operator)
            {
                case "+": return Arithmetic(left, right, (a, b) => a + b);
                case "-": return Arithmetic(left, right, (a, b) => a - b);
                case "*": return Arithmetic(left, right, (a, b) => a * b);
                case "/": return Arithmetic(left, right, (a, b) => a / b);
                case "%": return Arithmetic(left, right, (a, b) => a % b);
                case "LIKE": return left is string text && right is string pattern ? Like(text, pattern) : (object?)null;
                case "=": return Compare(left, right) is { } eq ? eq == 0 : (object?)null;
                case "!=": return Compare(left, right) is { } ne ? ne != 0 : (object?)null;
                case "<": return Compare(left, right) is { } lt ? lt < 0 : (object?)null;
                case "<=": return Compare(left, right) is { } le ? le <= 0 : (object?)null;
                case ">": return Compare(left, right) is { } gt ? gt > 0 : (object?)null;
                case ">=": return Compare(left, right) is { } ge ? ge >= 0 : (object?)null;
                default: return null;
            }
        }

        private static bool IsNullLiteral(GuardExpr expression)
        {
            return expression is GuardLiteral { Value: null };
        }

        private static object? Arithmetic(object? left, object? right, Func<decimal, decimal, decimal> op)
        {
            return left is decimal a && right is decimal b ? op(a, b) : (object?)null;
        }

        // Null when the values can't be compared (a missing value, or different families).
        private static int? Compare(object? left, object? right)
        {
            switch (left)
            {
                case decimal a when right is decimal b:
                    return a.CompareTo(b);
                case string a when right is string b:
                    return string.CompareOrdinal(a, b) < 0 ? -1 : string.CompareOrdinal(a, b) > 0 ? 1 : 0;
                case bool a when right is bool b:
                    return a == b ? 0 : a ? 1 : -1;
                default:
                    return null;
            }
        }

        private static object? Normalise(object? value)
        {
            switch (value)
            {
                case null: return null;
                case string s: return s;
                case bool b: return b;
                case decimal d: return d;
                case int i: return (decimal)i;
                case long l: return (decimal)l;
                case double f: return (decimal)f;
                case float f: return (decimal)f;
                case Guid g: return g.ToString("D");
                case DateTimeOffset t: return t.ToString("O", CultureInfo.InvariantCulture);
                case IFormattable formattable: return formattable.ToString(null, CultureInfo.InvariantCulture);
                default: return value.ToString();
            }
        }

        // CESQL LIKE: % any run, _ one character, \ escapes.
        private static bool Like(string text, string pattern)
        {
            return Like(text, 0, pattern, 0);
        }

        private static bool Like(string text, int t, string pattern, int p)
        {
            while (p < pattern.Length)
            {
                char c = pattern[p];
                if (c == '%')
                {
                    for (int k = t; k <= text.Length; k++)
                    {
                        if (Like(text, k, pattern, p + 1))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                if (t >= text.Length)
                {
                    return false;
                }

                if (c == '\\' && p + 1 < pattern.Length)
                {
                    p++;
                    c = pattern[p];
                    if (text[t] != c)
                    {
                        return false;
                    }
                }
                else if (c != '_' && text[t] != c)
                {
                    return false;
                }

                t++;
                p++;
            }

            return t == text.Length;
        }
    }
}

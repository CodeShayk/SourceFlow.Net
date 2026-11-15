using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SourceFlow.Messaging;

namespace SourceFlow.Transport;

/// <summary>
/// A CloudEvents SQL (CESQL 1.0) subscription filter (doc 07 §7). Identifiers are CloudEvents attributes
/// (<c>type</c>, <c>source</c>, <c>subject</c>, <c>id</c>, <c>time</c>, extensions such as <c>sftenant</c>); a missing
/// attribute makes the expression an error, and an error never matches. Supported: literals (string, integer, boolean),
/// <c>AND OR XOR NOT</c>, <c>= != &lt;&gt; &lt; &lt;= &gt; &gt;=</c>, <c>+ - * / %</c>, <c>[NOT] LIKE</c>,
/// <c>[NOT] IN (…)</c>, <c>EXISTS attr</c>, and the functions <c>LENGTH CONCAT LOWER UPPER TRIM ABS INT STRING BOOL</c>.
/// Transports translate simple filters to their native form (<see cref="ExactTypes"/>, <see cref="TypePrefixes"/>);
/// otherwise the core evaluates them on receipt, with identical results.
/// </summary>
public sealed class CloudEventFilter
{
    private readonly Node _root;

    private CloudEventFilter(string expression, Node root)
    {
        Expression = expression;
        _root = root;
        ExactTypes = CollectTypes(root);
        TypePrefixes = CollectPrefixes(root);
    }

    /// <summary>The filter text.</summary>
    public string Expression { get; }

    /// <summary>
    /// The CloudEvents types the filter accepts when it is exactly <c>type = '…'</c>, <c>type IN (…)</c> or an OR of
    /// those (natively expressible on every broker); otherwise null.
    /// </summary>
    public IReadOnlyList<string>? ExactTypes { get; }

    /// <summary>The prefixes the filter accepts when it is exactly <c>type LIKE 'prefix%'</c> (or an OR of those); otherwise null.</summary>
    public IReadOnlyList<string>? TypePrefixes { get; }

    /// <summary>Parses a filter.</summary>
    /// <exception cref="FormatException">The text is not a valid filter; the message has the position.</exception>
    public static CloudEventFilter Parse(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        var parser = new Parser(expression);
        Node root = parser.ParseExpression();
        parser.ExpectEnd();
        return new CloudEventFilter(expression, root);
    }

    /// <summary>Parses a filter without throwing.</summary>
    public static bool TryParse(string expression, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CloudEventFilter? filter, out string? error)
    {
        try
        {
            filter = Parse(expression);
            error = null;
            return true;
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            filter = null;
            error = e.Message;
            return false;
        }
    }

    /// <summary>A filter that accepts exactly <paramref name="types"/> (CloudEvents types, <c>{name}.v{version}</c>).</summary>
    public static CloudEventFilter ForTypes(IEnumerable<string> types)
    {
        string[] list = [.. (types ?? throw new ArgumentNullException(nameof(types))).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        if (list.Length == 0)
        {
            throw new ArgumentException("A type filter needs at least one type.", nameof(types));
        }

        return Parse(list.Length == 1 ? $"type = {Quote(list[0])}" : $"type IN ({string.Join(", ", list.Select(Quote))})");
    }

    /// <summary>True when the envelope's CloudEvents attributes match.</summary>
    public bool Matches(MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return Matches(CloudEvents.ToAttributes(envelope));
    }

    /// <summary>True when the attributes match; an evaluation error (missing attribute, bad cast) never matches.</summary>
    public bool Matches(IReadOnlyDictionary<string, string> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        try
        {
            return _root.Evaluate(attributes) is true;
        }
        catch (EvaluationException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Expression;
    }

    /// <summary>Quotes a CESQL string literal.</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
    }

    private static IReadOnlyList<string>? CollectTypes(Node node)
    {
        var types = new List<string>();
        return Collect(node) ? types : null;

        bool Collect(Node n)
        {
            switch (n)
            {
                case Binary { Op: "OR" } or:
                    return Collect(or.Left) && Collect(or.Right);
                case Binary { Op: "=", Left: Attribute { Name: "type" }, Right: Literal { Value: string type } }:
                    types.Add(type);
                    return true;
                case In { Negated: false, Value: Attribute { Name: "type" } } inList when inList.Items.All(i => i is Literal { Value: string }):
                    types.AddRange(inList.Items.Select(i => (string)((Literal)i).Value));
                    return true;
                default:
                    return false;
            }
        }
    }

    private static IReadOnlyList<string>? CollectPrefixes(Node node)
    {
        var prefixes = new List<string>();
        return Collect(node) ? prefixes : null;

        bool Collect(Node n)
        {
            switch (n)
            {
                case Binary { Op: "OR" } or:
                    return Collect(or.Left) && Collect(or.Right);
                case Like { Negated: false, Value: Attribute { Name: "type" } } like
                    when like.Pattern.EndsWith('%') && like.Pattern.IndexOfAny(['%', '_', '\\'], 0, like.Pattern.Length - 1) < 0:
                    prefixes.Add(like.Pattern[..^1]);
                    return true;
                default:
                    return false;
            }
        }
    }

    // ================================================================ evaluation

    private sealed class EvaluationException(string message) : Exception(message);

    private abstract record Node
    {
        public abstract object Evaluate(IReadOnlyDictionary<string, string> attributes);
    }

    private sealed record Literal(object Value) : Node
    {
        public override object Evaluate(IReadOnlyDictionary<string, string> attributes)
        {
            return Value;
        }
    }

    private sealed record Attribute(string Name) : Node
    {
        public override object Evaluate(IReadOnlyDictionary<string, string> attributes)
        {
            return attributes.TryGetValue(Name, out string? value) ? value : throw new EvaluationException($"Missing attribute '{Name}'.");
        }
    }

    private sealed record Exists(string Name) : Node
    {
        public override object Evaluate(IReadOnlyDictionary<string, string> attributes)
        {
            return attributes.ContainsKey(Name);
        }
    }

    private sealed record Not(Node Operand) : Node
    {
        public override object Evaluate(IReadOnlyDictionary<string, string> attributes)
        {
            return !ToBool(Operand.Evaluate(attributes));
        }
    }

    private sealed record Negate(Node Operand) : Node
    {
        public override object Evaluate(IReadOnlyDictionary<string, string> attributes)
        {
            return checked(-ToInt(Operand.Evaluate(attributes)));
        }
    }

    private sealed record Binary(string Op, Node Left, Node Right) : Node
    {
        public override object Evaluate(IReadOnlyDictionary<string, string> attributes)
        {
            switch (Op)
            {
                case "AND":
                    return ToBool(Left.Evaluate(attributes)) && ToBool(Right.Evaluate(attributes));
                case "OR":
                    return ToBool(Left.Evaluate(attributes)) || ToBool(Right.Evaluate(attributes));
                case "XOR":
                    return ToBool(Left.Evaluate(attributes)) ^ ToBool(Right.Evaluate(attributes));
            }

            object left = Left.Evaluate(attributes);
            object right = Right.Evaluate(attributes);
            switch (Op)
            {
                case "+":
                    return checked(ToInt(left) + ToInt(right));
                case "-":
                    return checked(ToInt(left) - ToInt(right));
                case "*":
                    return checked(ToInt(left) * ToInt(right));
                case "/":
                    return ToInt(right) == 0 ? throw new EvaluationException("Division by zero.") : ToInt(left) / ToInt(right);
                case "%":
                    return ToInt(right) == 0 ? throw new EvaluationException("Division by zero.") : ToInt(left) % ToInt(right);
            }

            int compared = Compare(left, right);
            return Op switch
            {
                "=" => compared == 0,
                "!=" or "<>" => compared != 0,
                "<" => compared < 0,
                "<=" => compared <= 0,
                ">" => compared > 0,
                ">=" => compared >= 0,
                _ => throw new EvaluationException($"Unknown operator {Op}."),
            };
        }
    }

    private sealed record In(Node Value, IReadOnlyList<Node> Items, bool Negated) : Node
    {
        public override object Evaluate(IReadOnlyDictionary<string, string> attributes)
        {
            object value = Value.Evaluate(attributes);
            bool found = Items.Any(i => Compare(value, i.Evaluate(attributes)) == 0);
            return found != Negated;
        }
    }

    private sealed record Like(Node Value, string Pattern, bool Negated) : Node
    {
        private readonly Regex _regex = new(ToRegex(Pattern), RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromMilliseconds(100));

        public override object Evaluate(IReadOnlyDictionary<string, string> attributes)
        {
            return _regex.IsMatch(ToText(Value.Evaluate(attributes))) != Negated;
        }

        private static string ToRegex(string pattern)
        {
            var regex = new StringBuilder("^");
            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                if (c == '\\' && i + 1 < pattern.Length)
                {
                    regex.Append(Regex.Escape(pattern[++i].ToString()));
                }
                else
                {
                    regex.Append(c switch { '%' => ".*", '_' => ".", _ => Regex.Escape(c.ToString()) });
                }
            }

            return regex.Append('$').ToString();
        }
    }

    private sealed record Call(string Name, IReadOnlyList<Node> Arguments) : Node
    {
        public override object Evaluate(IReadOnlyDictionary<string, string> attributes)
        {
            object[] args = [.. Arguments.Select(a => a.Evaluate(attributes))];
            return Name switch
            {
                "LENGTH" => (long)ToText(args[0]).Length,
                "CONCAT" => string.Concat(args.Select(ToText)),
                "LOWER" => ToText(args[0]).ToLowerInvariant(),
                "UPPER" => ToText(args[0]).ToUpperInvariant(),
                "TRIM" => ToText(args[0]).Trim(),
                "ABS" => Math.Abs(ToInt(args[0])),
                "INT" => ToInt(args[0]),
                "STRING" => ToText(args[0]),
                "BOOL" => ToBool(args[0]),
                _ => throw new EvaluationException($"Unknown function {Name}."),
            };
        }
    }

    private static int Compare(object left, object right)
    {
        // CESQL implicit casting: an operand is cast to the other's type when the types differ.
        return (left, right) switch
        {
            (long l, _) => l.CompareTo(ToInt(right)),
            (_, long r) => ToInt(left).CompareTo(r),
            (bool l, _) => l.CompareTo(ToBool(right)),
            (_, bool r) => ToBool(left).CompareTo(r),
            _ => string.CompareOrdinal(ToText(left), ToText(right)),
        };
    }

    private static long ToInt(object value)
    {
        return value switch
        {
            long l => l,
            bool b => b ? 1 : 0,
            string s when long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long parsed) => parsed,
            _ => throw new EvaluationException($"'{value}' is not an integer."),
        };
    }

    private static bool ToBool(object value)
    {
        return value switch
        {
            bool b => b,
            string s when s.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
            string s when s.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
            _ => throw new EvaluationException($"'{value}' is not a boolean."),
        };
    }

    private static string ToText(object value)
    {
        return value switch
        {
            string s => s,
            long l => l.ToString(CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            _ => throw new EvaluationException("Not a value."),
        };
    }

    // ================================================================ parsing

    private sealed class Parser(string text)
    {
        private static readonly HashSet<string> Functions = ["LENGTH", "CONCAT", "LOWER", "UPPER", "TRIM", "ABS", "INT", "STRING", "BOOL"];
        private int _position;

        public Node ParseExpression()
        {
            return ParseOr();
        }

        public void ExpectEnd()
        {
            SkipSpace();
            if (_position < text.Length)
            {
                throw Error("an operator or the end of the filter");
            }
        }

        private Node ParseOr()
        {
            Node left = ParseXor();
            while (Keyword("OR"))
            {
                left = new Binary("OR", left, ParseXor());
            }

            return left;
        }

        private Node ParseXor()
        {
            Node left = ParseAnd();
            while (Keyword("XOR"))
            {
                left = new Binary("XOR", left, ParseAnd());
            }

            return left;
        }

        private Node ParseAnd()
        {
            Node left = ParseNot();
            while (Keyword("AND"))
            {
                left = new Binary("AND", left, ParseNot());
            }

            return left;
        }

        private Node ParseNot()
        {
            return Keyword("NOT") ? new Not(ParseNot()) : ParseComparison();
        }

        private Node ParseComparison()
        {
            Node left = ParseSum();
            int mark = _position;
            bool negated = Keyword("NOT");
            if (Keyword("LIKE"))
            {
                SkipSpace();
                if (Peek() is not ('\'' or '"'))
                {
                    throw Error("a string pattern after LIKE");
                }

                return new Like(left, ReadString(), negated);
            }

            if (Keyword("IN"))
            {
                Expect('(');
                var items = new List<Node> { ParseSum() };
                while (Accept(','))
                {
                    items.Add(ParseSum());
                }

                Expect(')');
                return new In(left, items, negated);
            }

            if (negated)
            {
                _position = mark;
                throw Error("LIKE or IN after NOT");
            }

            foreach (string op in new[] { "<=", ">=", "!=", "<>", "=", "<", ">" })
            {
                if (Symbol(op))
                {
                    return new Binary(op, left, ParseSum());
                }
            }

            return left;
        }

        private Node ParseSum()
        {
            Node left = ParseProduct();
            while (true)
            {
                if (Symbol("+"))
                {
                    left = new Binary("+", left, ParseProduct());
                }
                else if (Symbol("-"))
                {
                    left = new Binary("-", left, ParseProduct());
                }
                else
                {
                    return left;
                }
            }
        }

        private Node ParseProduct()
        {
            Node left = ParseUnary();
            while (true)
            {
                string? op = Symbol("*") ? "*" : Symbol("/") ? "/" : Symbol("%") ? "%" : null;
                if (op is null)
                {
                    return left;
                }

                left = new Binary(op, left, ParseUnary());
            }
        }

        private Node ParseUnary()
        {
            return Symbol("-") ? new Negate(ParseUnary()) : ParseAtom();
        }

        private Node ParseAtom()
        {
            SkipSpace();
            if (_position >= text.Length)
            {
                throw Error("a value");
            }

            char c = text[_position];
            if (c == '(')
            {
                _position++;
                Node inner = ParseOr();
                Expect(')');
                return inner;
            }

            if (c is '\'' or '"')
            {
                return new Literal(ReadString());
            }

            if (char.IsAsciiDigit(c))
            {
                int start = _position;
                while (_position < text.Length && char.IsAsciiDigit(text[_position]))
                {
                    _position++;
                }

                return long.TryParse(text.AsSpan(start, _position - start), NumberStyles.None, CultureInfo.InvariantCulture, out long number)
                    ? new Literal(number)
                    : throw Error("an integer within range", start);
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                int start = _position;
                string word = ReadWord();
                string upper = word.ToUpperInvariant();
                switch (upper)
                {
                    case "TRUE":
                        return new Literal(true);
                    case "FALSE":
                        return new Literal(false);
                    case "EXISTS":
                        SkipSpace();
                        return new Exists(ReadAttributeName());
                }

                SkipSpace();
                if (Peek() == '(')
                {
                    if (!Functions.Contains(upper))
                    {
                        throw Error("a known function (LENGTH, CONCAT, LOWER, UPPER, TRIM, ABS, INT, STRING, BOOL)", start);
                    }

                    _position++;
                    var args = new List<Node>();
                    SkipSpace();
                    if (!Accept(')'))
                    {
                        args.Add(ParseOr());
                        while (Accept(','))
                        {
                            args.Add(ParseOr());
                        }

                        Expect(')');
                    }

                    int expected = upper == "CONCAT" ? -1 : 1;
                    if ((expected == 1 && args.Count != 1) || (expected == -1 && args.Count == 0))
                    {
                        throw Error($"{(expected == 1 ? "one argument" : "at least one argument")} for {upper}", start);
                    }

                    return new Call(upper, args);
                }

                if (!IsAttributeName(word))
                {
                    throw Error("an attribute name of lower-case letters and digits", start);
                }

                return new Attribute(word);
            }

            throw Error("a value");
        }

        private string ReadAttributeName()
        {
            int start = _position;
            string word = ReadWord();
            return IsAttributeName(word) ? word : throw Error("an attribute name of lower-case letters and digits", start);
        }

        private static bool IsAttributeName(string word)
        {
            return word.Length is > 0 and <= 20 && word.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch));
        }

        private string ReadWord()
        {
            int start = _position;
            while (_position < text.Length && (char.IsAsciiLetterOrDigit(text[_position]) || text[_position] == '_'))
            {
                _position++;
            }

            return text[start.._position];
        }

        private string ReadString()
        {
            char quote = text[_position];
            int start = _position++;
            var value = new StringBuilder();
            while (_position < text.Length)
            {
                char c = text[_position++];
                if (c == '\\' && _position < text.Length)
                {
                    char escaped = text[_position++];

                    // LIKE escapes (\% \_) are kept for the pattern; quote and backslash escapes are resolved.
                    if (escaped is '%' or '_')
                    {
                        value.Append('\\');
                    }

                    value.Append(escaped);
                }
                else if (c == quote)
                {
                    return value.ToString();
                }
                else
                {
                    value.Append(c);
                }
            }

            throw Error("a closing quote", start);
        }

        private bool Keyword(string keyword)
        {
            SkipSpace();
            int end = _position + keyword.Length;
            if (end <= text.Length
                && string.Compare(text, _position, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) == 0
                && (end == text.Length || !(char.IsAsciiLetterOrDigit(text[end]) || text[end] == '_')))
            {
                _position = end;
                return true;
            }

            return false;
        }

        private bool Symbol(string symbol)
        {
            SkipSpace();
            if (string.CompareOrdinal(text, _position, symbol, 0, symbol.Length) == 0)
            {
                // '<' must not consume the start of '<=' or '<>'; longer symbols are tried first by the callers.
                _position += symbol.Length;
                return true;
            }

            return false;
        }

        private bool Accept(char c)
        {
            SkipSpace();
            if (Peek() == c)
            {
                _position++;
                return true;
            }

            return false;
        }

        private void Expect(char c)
        {
            if (!Accept(c))
            {
                throw Error($"'{c}'");
            }
        }

        private char? Peek()
        {
            return _position < text.Length ? text[_position] : null;
        }

        private void SkipSpace()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position]))
            {
                _position++;
            }
        }

        private FormatException Error(string expected, int? at = null)
        {
            int position = at ?? _position;
            return new FormatException($"CESQL filter: expected {expected} at position {position + 1} in \"{text}\".");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SourceFlow.Studio.Dsl.Json
{
    /// <summary>A parsed JSON value. Objects keep their member order.</summary>
    public abstract class JsonValue
    {
        /// <summary>The member <paramref name="name"/> of an object, or null.</summary>
        public virtual JsonValue? this[string name] => null;

        /// <summary>The value as a string, or null when it is not a string.</summary>
        public virtual string? AsString => null;

        /// <summary>The value as an integer, or null when it is not an integral number.</summary>
        public virtual long? AsInteger => null;

        /// <summary>The value as a number, or null when it is not a number.</summary>
        public virtual double? AsNumber => null;

        /// <summary>The value as a boolean, or null when it is not a boolean.</summary>
        public virtual bool? AsBoolean => null;

        /// <summary>The items of an array (empty for anything else).</summary>
        public virtual IReadOnlyList<JsonValue> Items => Array.Empty<JsonValue>();

        /// <summary>The members of an object (empty for anything else).</summary>
        public virtual IReadOnlyList<KeyValuePair<string, JsonValue>> Members => Array.Empty<KeyValuePair<string, JsonValue>>();
    }

    /// <summary>
    /// A small strict JSON reader (RFC 8259) for service manifests. It runs in the generator too (netstandard2.0),
    /// where System.Text.Json is not available. Duplicate keys and nesting deeper than 64 are rejected.
    /// </summary>
    public static class JsonLite
    {
        private const int MaxDepth = 64;

        /// <summary>Parses <paramref name="text"/>.</summary>
        /// <exception cref="FormatException">The text is not valid JSON.</exception>
        public static JsonValue Parse(string text)
        {
            if (text is null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            var reader = new Reader(text);
            reader.SkipWhitespace();
            JsonValue value = reader.Value(0);
            reader.SkipWhitespace();
            if (!reader.AtEnd)
            {
                throw reader.Error("unexpected text after the value");
            }

            return value;
        }

        private sealed class Reader
        {
            private readonly string _text;
            private int _position;

            public Reader(string text)
            {
                _text = text;
            }

            public bool AtEnd => _position >= _text.Length;

            public FormatException Error(string message)
            {
                int line = 1, column = 1;
                for (int i = 0; i < _position && i < _text.Length; i++)
                {
                    if (_text[i] == '\n')
                    {
                        line++;
                        column = 1;
                    }
                    else
                    {
                        column++;
                    }
                }

                return new FormatException("Invalid JSON at line " + line.ToString(CultureInfo.InvariantCulture) + ", column " + column.ToString(CultureInfo.InvariantCulture) + ": " + message + ".");
            }

            public void SkipWhitespace()
            {
                while (!AtEnd && (_text[_position] == ' ' || _text[_position] == '\t' || _text[_position] == '\n' || _text[_position] == '\r'))
                {
                    _position++;
                }
            }

            public JsonValue Value(int depth)
            {
                if (depth > MaxDepth)
                {
                    throw Error("nested too deeply");
                }

                if (AtEnd)
                {
                    throw Error("unexpected end");
                }

                char c = _text[_position];
                switch (c)
                {
                    case '{': return Object(depth);
                    case '[': return Array(depth);
                    case '"': return new JsonString(String());
                    case 't': Literal("true"); return JsonBoolean.True;
                    case 'f': Literal("false"); return JsonBoolean.False;
                    case 'n': Literal("null"); return JsonNull.Instance;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9'))
                        {
                            return Number();
                        }

                        throw Error("unexpected '" + c + "'");
                }
            }

            private JsonValue Object(int depth)
            {
                _position++;
                var members = new List<KeyValuePair<string, JsonValue>>();
                var names = new HashSet<string>(StringComparer.Ordinal);
                SkipWhitespace();
                if (Peek('}'))
                {
                    _position++;
                    return new JsonObject(members);
                }

                while (true)
                {
                    SkipWhitespace();
                    if (!Peek('"'))
                    {
                        throw Error("expected a property name");
                    }

                    string name = String();
                    if (!names.Add(name))
                    {
                        throw Error("duplicate property '" + name + "'");
                    }

                    SkipWhitespace();
                    Expect(':');
                    SkipWhitespace();
                    members.Add(new KeyValuePair<string, JsonValue>(name, Value(depth + 1)));
                    SkipWhitespace();
                    if (Peek(','))
                    {
                        _position++;
                        continue;
                    }

                    Expect('}');
                    return new JsonObject(members);
                }
            }

            private JsonValue Array(int depth)
            {
                _position++;
                var items = new List<JsonValue>();
                SkipWhitespace();
                if (Peek(']'))
                {
                    _position++;
                    return new JsonArray(items);
                }

                while (true)
                {
                    SkipWhitespace();
                    items.Add(Value(depth + 1));
                    SkipWhitespace();
                    if (Peek(','))
                    {
                        _position++;
                        continue;
                    }

                    Expect(']');
                    return new JsonArray(items);
                }
            }

            private string String()
            {
                _position++;
                var builder = new StringBuilder();
                while (true)
                {
                    if (AtEnd)
                    {
                        throw Error("unterminated string");
                    }

                    char c = _text[_position++];
                    if (c == '"')
                    {
                        return builder.ToString();
                    }

                    if (c < ' ')
                    {
                        throw Error("control character in a string");
                    }

                    if (c != '\\')
                    {
                        builder.Append(c);
                        continue;
                    }

                    if (AtEnd)
                    {
                        throw Error("unterminated escape");
                    }

                    char e = _text[_position++];
                    switch (e)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (_position + 4 > _text.Length
                                || !int.TryParse(_text.Substring(_position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
                            {
                                throw Error("invalid \\u escape");
                            }

                            builder.Append((char)code);
                            _position += 4;
                            break;
                        default:
                            throw Error("invalid escape '\\" + e + "'");
                    }
                }
            }

            private JsonValue Number()
            {
                int start = _position;
                if (Peek('-'))
                {
                    _position++;
                }

                if (Peek('0'))
                {
                    _position++;
                }
                else if (!Digits())
                {
                    throw Error("invalid number");
                }

                bool integral = true;
                if (Peek('.'))
                {
                    integral = false;
                    _position++;
                    if (!Digits())
                    {
                        throw Error("invalid number");
                    }
                }

                if (Peek('e') || Peek('E'))
                {
                    integral = false;
                    _position++;
                    if (Peek('+') || Peek('-'))
                    {
                        _position++;
                    }

                    if (!Digits())
                    {
                        throw Error("invalid number");
                    }
                }

                string text = _text.Substring(start, _position - start);
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || double.IsInfinity(value))
                {
                    throw Error("number out of range");
                }

                long? whole = integral && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l) ? l : (long?)null;
                return new JsonNumber(value, whole);
            }

            private bool Digits()
            {
                int start = _position;
                while (!AtEnd && _text[_position] >= '0' && _text[_position] <= '9')
                {
                    _position++;
                }

                return _position > start;
            }

            private void Literal(string word)
            {
                if (string.CompareOrdinal(_text, _position, word, 0, word.Length) != 0)
                {
                    throw Error("invalid literal");
                }

                _position += word.Length;
            }

            private bool Peek(char c)
            {
                return !AtEnd && _text[_position] == c;
            }

            private void Expect(char c)
            {
                if (!Peek(c))
                {
                    throw Error("expected '" + c + "'");
                }

                _position++;
            }
        }

        private sealed class JsonObject : JsonValue
        {
            private readonly List<KeyValuePair<string, JsonValue>> _members;
            private readonly Dictionary<string, JsonValue> _byName;

            public JsonObject(List<KeyValuePair<string, JsonValue>> members)
            {
                _members = members;
                _byName = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, JsonValue> member in members)
                {
                    _byName[member.Key] = member.Value;
                }
            }

            public override JsonValue? this[string name] => _byName.TryGetValue(name, out JsonValue? value) ? value : null;

            public override IReadOnlyList<KeyValuePair<string, JsonValue>> Members => _members;
        }

        private sealed class JsonArray : JsonValue
        {
            private readonly List<JsonValue> _items;

            public JsonArray(List<JsonValue> items)
            {
                _items = items;
            }

            public override IReadOnlyList<JsonValue> Items => _items;
        }

        private sealed class JsonString : JsonValue
        {
            private readonly string _value;

            public JsonString(string value)
            {
                _value = value;
            }

            public override string? AsString => _value;
        }

        private sealed class JsonNumber : JsonValue
        {
            private readonly double _value;
            private readonly long? _whole;

            public JsonNumber(double value, long? whole)
            {
                _value = value;
                _whole = whole;
            }

            public override double? AsNumber => _value;

            public override long? AsInteger => _whole;
        }

        private sealed class JsonBoolean : JsonValue
        {
            public static readonly JsonBoolean True = new JsonBoolean(true);
            public static readonly JsonBoolean False = new JsonBoolean(false);
            private readonly bool _value;

            private JsonBoolean(bool value)
            {
                _value = value;
            }

            public override bool? AsBoolean => _value;
        }

        private sealed class JsonNull : JsonValue
        {
            public static readonly JsonNull Instance = new JsonNull();
        }
    }
}

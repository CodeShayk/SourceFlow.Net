using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SourceFlow.Query;

/// <summary>
/// Runtime values of the in-memory interpreter. A value is null (JSON null or SQL NULL), <see cref="Missing"/> (an absent
/// field, doc 14 §5.5), a <see cref="string"/>, <see cref="long"/>, <see cref="decimal"/>, <see cref="bool"/>,
/// <see cref="DateTimeOffset"/>, <see cref="TimeSpan"/>, <see cref="GlobalPosition"/>, a <see cref="string"/> array (tags)
/// or a <see cref="JsonElement"/> object or array. Comparisons follow SQL three-valued logic: anything compared with null
/// or missing is unknown (null), so a <c>WHERE</c> drops the row, which is CESQL's "missing → false" and what
/// PostgreSQL does.
/// </summary>
internal static class QueryValue
{
    /// <summary>An absent field or attribute.</summary>
    public static readonly object Missing = new MissingValue();

    public static bool IsNull(object? value)
    {
        return value is null || ReferenceEquals(value, Missing);
    }

    public static object? FromJson(JsonElement element, QueryType type)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return null;
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                if (type != QueryType.Decimal && element.TryGetInt64(out long integer))
                {
                    return integer;
                }

                return element.TryGetDecimal(out decimal number) ? number : (decimal)element.GetDouble();
            case JsonValueKind.String:
                string text = element.GetString()!;
                return type switch
                {
                    QueryType.Timestamp when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset time) => time.ToUniversalTime(),
                    QueryType.Decimal when decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d) => d,
                    QueryType.Integer when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) => l,
                    _ => text,
                };
            default:
                return element.Clone();
        }
    }

    /// <summary>Follows a path of names and indexes; returns the element or <see cref="Missing"/>.</summary>
    public static object Navigate(JsonElement root, IReadOnlyList<object> path)
    {
        JsonElement current = root;
        foreach (object segment in path)
        {
            if (segment is int index)
            {
                if (current.ValueKind != JsonValueKind.Array || index < 0 || index >= current.GetArrayLength())
                {
                    return Missing;
                }

                current = current[index];
            }
            else if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty((string)segment, out current))
            {
                return Missing;
            }
        }

        return current;
    }

    /// <summary>Compares two values; null when either is null/missing or they aren't comparable.</summary>
    public static int? Compare(object? left, object? right)
    {
        if (IsNull(left) || IsNull(right))
        {
            return null;
        }

        switch (left, right)
        {
            case (long a, long b):
                return a.CompareTo(b);
            case (long or decimal, long or decimal):
                return ToDecimal(left!).CompareTo(ToDecimal(right!));
            case (string a, string b):
                return string.CompareOrdinal(a, b) switch { < 0 => -1, > 0 => 1, _ => 0 };
            case (bool a, bool b):
                return a.CompareTo(b);
            case (DateTimeOffset a, DateTimeOffset b):
                return a.CompareTo(b);
            case (DateTimeOffset a, string b) when TryTime(b, out DateTimeOffset tb):
                return a.CompareTo(tb);
            case (string a, DateTimeOffset b) when TryTime(a, out DateTimeOffset ta):
                return ta.CompareTo(b);
            case (TimeSpan a, TimeSpan b):
                return a.CompareTo(b);
            case (GlobalPosition a, GlobalPosition b):
                return a.CompareTo(b);
            case (GlobalPosition a, string b) when GlobalPosition.TryParse(b, out GlobalPosition pb):
                return a.CompareTo(pb);
            default:
                return null;
        }
    }

    public static bool? Equal(object? left, object? right)
    {
        return Compare(left, right) is { } c ? c == 0 : (IsNull(left) || IsNull(right) ? null : false);
    }

    public static decimal ToDecimal(object value)
    {
        return value switch
        {
            long l => l,
            decimal d => d,
            _ => throw new InvalidCastException(),
        };
    }

    public static bool IsNumber(object? value)
    {
        return value is long or decimal;
    }

    private static bool TryTime(string text, out DateTimeOffset time)
    {
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out time);
    }

    /// <summary>A canonical, type-tagged key for grouping, distinct and hash joins (numbers compare by value).</summary>
    public static string Key(object? value)
    {
        return value switch
        {
            null => "n",
            _ when ReferenceEquals(value, Missing) => "n",
            long l => "d:" + ((decimal)l).ToString(CultureInfo.InvariantCulture),
            decimal d => "d:" + (d / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture),
            string s => "s:" + s,
            bool b => b ? "b:1" : "b:0",
            DateTimeOffset t => "t:" + t.UtcTicks.ToString(CultureInfo.InvariantCulture),
            TimeSpan t => "u:" + t.Ticks.ToString(CultureInfo.InvariantCulture),
            GlobalPosition p => "p:" + p,
            string[] tags => "a:" + string.Join('\u001F', tags),
            JsonElement e => "j:" + e.GetRawText(),
            _ => "o:" + value,
        };
    }

    public static string Key(IEnumerable<object?> values)
    {
        var key = new StringBuilder();
        foreach (object? value in values)
        {
            key.Append(Key(value)).Append('\u001E');
        }

        return key.ToString();
    }

    /// <summary>The value as returned to callers: missing becomes null.</summary>
    public static object? Output(object? value)
    {
        return ReferenceEquals(value, Missing) ? null : value;
    }

    private sealed class MissingValue
    {
        public override string ToString()
        {
            return "missing";
        }
    }
}

/// <summary>
/// CESQL <c>LIKE</c>: <c>%</c> matches any run, <c>_</c> one character, <c>\</c> escapes. Iterative with a single
/// back-tracking point (no regular expressions, no exponential cases; doc 14 I-8).
/// </summary>
internal static class LikeMatcher
{
    public static bool IsMatch(string value, string pattern)
    {
        int v = 0;
        int p = 0;
        int starPattern = -1;
        int starValue = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && pattern[p] == '\\' && p + 1 < pattern.Length)
            {
                if (pattern[p + 1] == value[v])
                {
                    p += 2;
                    v++;
                    continue;
                }
            }
            else if (p < pattern.Length && (pattern[p] == '_' || (pattern[p] != '%' && pattern[p] == value[v])))
            {
                p++;
                v++;
                continue;
            }
            else if (p < pattern.Length && pattern[p] == '%')
            {
                starPattern = p++;
                starValue = v;
                continue;
            }

            if (starPattern < 0)
            {
                return false;
            }

            p = starPattern + 1;
            v = ++starValue;
        }

        while (p < pattern.Length && pattern[p] == '%')
        {
            p++;
        }

        return p == pattern.Length;
    }
}

using System.Text;
using SourceFlow.Transport;

namespace SourceFlow.Cloud.Gcp;

/// <summary>
/// CloudEvents SQL filters on Pub/Sub subscriptions (doc 07 §7, ADR-033). A filter that is a list of types
/// (<see cref="CloudEventFilter.ExactTypes"/>) or of type prefixes (<see cref="CloudEventFilter.TypePrefixes"/>) becomes a
/// Pub/Sub filter on <c>attributes.ce-type</c>, which Pub/Sub applies before delivery (it acknowledges the rest). Any other
/// filter, and one whose translation exceeds the 256-byte Pub/Sub limit, is evaluated by the receiver with the same
/// evaluator, so results are identical. Pub/Sub filters are immutable: a changed filter needs a new subscription.
/// </summary>
public static class GcpFilter
{
    /// <summary>The Pub/Sub limit on a filter expression.</summary>
    public const int MaxFilterBytes = 256;

    private const string TypeAttribute = "attributes." + GcpWire.CloudEventsPrefix + "type";
    private const string Or = " OR ";

    /// <summary>The Pub/Sub filter of a CESQL filter, or null when Pub/Sub cannot express it.</summary>
    public static string? Translate(CloudEventFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        IEnumerable<string> terms;
        if (filter.ExactTypes is { } types && types.All(Quotable))
        {
            terms = types.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(t => $"{TypeAttribute} = \"{t}\"");
        }
        else if (filter.TypePrefixes is { } prefixes && prefixes.All(Quotable))
        {
            terms = prefixes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(p => $"hasPrefix({TypeAttribute}, \"{p}\")");
        }
        else
        {
            return null;
        }

        string expression = string.Join(Or, terms);
        return expression.Length > 0 && Encoding.UTF8.GetByteCount(expression) <= MaxFilterBytes ? expression : null;
    }

    /// <summary>The Pub/Sub filter of a CESQL filter text (null text: no filter).</summary>
    public static string? Translate(string? filter)
    {
        return filter is null ? null : Translate(CloudEventFilter.Parse(filter));
    }

    /// <summary>True when two Pub/Sub filters are equivalent (the same OR terms in any order; null and empty mean none).</summary>
    public static bool Equivalent(string? expected, string? actual)
    {
        bool noExpected = string.IsNullOrWhiteSpace(expected);
        bool noActual = string.IsNullOrWhiteSpace(actual);
        if (noExpected || noActual)
        {
            return noExpected == noActual;
        }

        return Terms(expected!).SetEquals(Terms(actual!));
    }

    private static HashSet<string> Terms(string filter)
    {
        return [.. filter.Split(Or, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(t => string.Join(' ', t.Split(' ', StringSplitOptions.RemoveEmptyEntries)))];
    }

    // Pub/Sub string literals: no quote or backslash escapes are needed for ordinary type names.
    private static bool Quotable(string value)
    {
        return value.Length > 0 && !value.Any(c => c is '"' or '\\' || char.IsControl(c));
    }
}

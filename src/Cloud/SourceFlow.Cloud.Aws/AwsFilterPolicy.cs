using System.Text.Json;
using System.Text.Json.Nodes;
using SourceFlow.Transport;

namespace SourceFlow.Cloud.Aws;

/// <summary>
/// CloudEvents SQL filters on SNS subscriptions (doc 07 §7, ADR-033). A filter that is a list of types
/// (<see cref="CloudEventFilter.ExactTypes"/>) or of type prefixes (<see cref="CloudEventFilter.TypePrefixes"/>) becomes
/// an SNS filter policy on <c>ce_type</c>, which SNS applies before delivery. Any other filter is evaluated by the
/// receiver (the subscription has no policy), with the same evaluator, so results are identical.
/// </summary>
public static class AwsFilterPolicy
{
    private const string TypeAttribute = AwsWire.CloudEventsPrefix + "type";

    /// <summary>The SNS filter policy of a CESQL filter, or null when SNS cannot express it.</summary>
    public static string? Translate(CloudEventFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        JsonArray values;
        if (filter.ExactTypes is { } types)
        {
            values = [.. types.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(t => (JsonNode)JsonValue.Create(t))];
        }
        else if (filter.TypePrefixes is { } prefixes)
        {
            values = [.. prefixes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(p => (JsonNode)new JsonObject { ["prefix"] = p })];
        }
        else
        {
            return null;
        }

        return new JsonObject { [TypeAttribute] = values }.ToJsonString();
    }

    /// <summary>The SNS filter policy of a CESQL filter text (null text: no filter).</summary>
    public static string? Translate(string? filter)
    {
        return filter is null ? null : Translate(CloudEventFilter.Parse(filter));
    }

    /// <summary>True when two filter policies are equivalent (JSON equality; null and empty mean no policy).</summary>
    public static bool Equivalent(string? expected, string? actual)
    {
        bool noExpected = string.IsNullOrWhiteSpace(expected);
        bool noActual = string.IsNullOrWhiteSpace(actual);
        if (noExpected || noActual)
        {
            return noExpected == noActual;
        }

        try
        {
            return JsonNode.DeepEquals(Normalize(JsonNode.Parse(expected!)), Normalize(JsonNode.Parse(actual!)));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Orders object properties and the values of policy arrays, which SNS treats as sets.
    private static JsonNode? Normalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var ordered = new JsonObject();
                foreach (KeyValuePair<string, JsonNode?> pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    ordered[pair.Key] = Normalize(pair.Value);
                }

                return ordered;
            case JsonArray array:
                return new JsonArray([.. array.Select(Normalize).OrderBy(n => n?.ToJsonString(), StringComparer.Ordinal)]);
            default:
                return node?.DeepClone();
        }
    }
}

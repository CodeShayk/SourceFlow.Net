using System.Globalization;
using Azure.Messaging.ServiceBus.Administration;
using SourceFlow.Transport;

namespace SourceFlow.Cloud.Azure;

/// <summary>
/// CloudEvents SQL filters as Service Bus subscription rules (doc 07 §7, ADR-033). A subscription delivers what any of its
/// rules matches. A list of types (<see cref="CloudEventFilter.ExactTypes"/>) becomes one correlation rule per type on the
/// <c>cloudEvents:type</c> property; a list of type prefixes (<see cref="CloudEventFilter.TypePrefixes"/>) one SQL
/// <c>LIKE</c> rule per prefix. Any other filter gets a rule that matches everything, and the receiver evaluates it with
/// the same evaluator, so results are identical.
/// </summary>
public static class AzureFilter
{
    /// <summary>The rule that matches everything (no filter, or one Service Bus cannot express).</summary>
    public const string AllRuleName = "sf-all";

    private const string TypeProperty = AzureWire.CloudEventsPrefix + "type";

    /// <summary>The rules of a CESQL filter text (null text: no filter).</summary>
    public static IReadOnlyList<CreateRuleOptions> Rules(string? filter)
    {
        return Rules(filter is null ? null : CloudEventFilter.Parse(filter));
    }

    /// <summary>The rules of a CESQL filter (null: no filter).</summary>
    public static IReadOnlyList<CreateRuleOptions> Rules(CloudEventFilter? filter)
    {
        if (filter?.ExactTypes is { Count: > 0 } types)
        {
            return [.. types.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select((t, i) => new CreateRuleOptions(RuleName("sf-type-", i),
                new CorrelationRuleFilter { ApplicationProperties = { [TypeProperty] = t } }))];
        }

        if (filter?.TypePrefixes is { Count: > 0 } prefixes && prefixes.All(Likeable))
        {
            return [.. prefixes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select((p, i) => new CreateRuleOptions(RuleName("sf-prefix-", i),
                new SqlRuleFilter($"[{TypeProperty}] LIKE '{p}%'")))];
        }

        return [new CreateRuleOptions(AllRuleName, new TrueRuleFilter())];
    }

    /// <summary>True when Service Bus applies the filter itself; otherwise the receiver evaluates it.</summary>
    public static bool IsNative(CloudEventFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return Rules(filter) is not [{ Name: AllRuleName }];
    }

    /// <summary>A comparable description of a rule filter (<c>true</c>, <c>type=…</c>, <c>sql:…</c>).</summary>
    public static string Describe(RuleFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        switch (filter)
        {
            case TrueRuleFilter:
                return "true";
            case SqlRuleFilter sql:
                string expression = string.Join(' ', sql.SqlExpression.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                return expression == "1=1" ? "true" : "sql:" + expression;
            case CorrelationRuleFilter correlation when correlation.ApplicationProperties.Count == 1
                && correlation.ApplicationProperties.TryGetValue(TypeProperty, out object? type)
                && correlation is { CorrelationId: null, MessageId: null, To: null, ReplyTo: null, Subject: null, SessionId: null, ReplyToSessionId: null, ContentType: null }:
                return "type=" + Convert.ToString(type, CultureInfo.InvariantCulture);
            default:
                return "other:" + filter;
        }
    }

    /// <summary>True when two rule sets select the same messages (the same filters, in any order, by any name).</summary>
    public static bool Equivalent(IEnumerable<RuleFilter> expected, IEnumerable<RuleFilter> actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        return new HashSet<string>(expected.Select(Describe), StringComparer.Ordinal).SetEquals(actual.Select(Describe));
    }

    private static string RuleName(string prefix, int index)
    {
        return prefix + index.ToString(CultureInfo.InvariantCulture);
    }

    // A prefix is used in a LIKE pattern as is: wildcards, quotes and escapes would change its meaning.
    private static bool Likeable(string prefix)
    {
        return prefix.Length > 0 && !prefix.Any(c => c is '%' or '_' or '\'' or '[' or ']' || char.IsControl(c));
    }
}

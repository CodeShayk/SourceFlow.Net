using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace SourceFlow.Cloud.Gcp;

/// <summary>
/// Physical Pub/Sub ids of logical endpoints and topics (doc 07 §3). Topics and subscriptions share one id namespace per
/// project, so ids the transport derives carry separators that logical names never contain (<c>~</c> and <c>+</c> are
/// replaced in logical names):
/// <list type="bullet">
///   <item>an event topic is <c>{prefix}{topic}</c>;</item>
///   <item>an endpoint is the topic <c>{prefix}{endpoint}+in</c> and its subscription <c>{prefix}{endpoint}</c>;</item>
///   <item>a service's subscription to a topic is <c>{prefix}{endpoint}~{topic}</c>, one per subscribing service (GAP-13);</item>
///   <item>the dead-letter topic and its subscription are <c>{prefix}{endpoint}+dlq</c>.</item>
/// </list>
/// Ids are deterministic, start with a letter, and fit the 255-character limit (long ids keep a readable head and a hash).
/// </summary>
public static class GcpNaming
{
    /// <summary>The suffix of an endpoint's command topic.</summary>
    public const string EndpointTopicSuffix = "+in";

    /// <summary>The suffix of an endpoint's dead-letter topic and subscription.</summary>
    public const string DeadLetterSuffix = "+dlq";

    /// <summary>Separates the endpoint and the topic in a topic subscription id.</summary>
    public const char SubscriptionSeparator = '~';

    private const int MaxId = 255;
    private const int HashLength = 8;

    /// <summary>The id of an event topic.</summary>
    public static string Topic(string prefix, string topic)
    {
        return Id(Logical(prefix, topic), string.Empty);
    }

    /// <summary>The id of an endpoint's command topic.</summary>
    public static string EndpointTopic(string prefix, string endpoint)
    {
        return Id(Logical(prefix, endpoint), EndpointTopicSuffix);
    }

    /// <summary>The id of an endpoint's command subscription.</summary>
    public static string EndpointSubscription(string prefix, string endpoint)
    {
        return Id(Logical(prefix, endpoint), string.Empty);
    }

    /// <summary>The id of a service's subscription to a topic: <c>{endpoint}~{topic}</c>.</summary>
    public static string TopicSubscription(string prefix, string endpoint, string topic)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);
        return Id(Logical(prefix, endpoint) + SubscriptionSeparator + Sanitize(topic), string.Empty);
    }

    /// <summary>The id of an endpoint's dead-letter topic, which is also the id of its subscription.</summary>
    public static string DeadLetter(string prefix, string endpoint)
    {
        return Id(Logical(prefix, endpoint), DeadLetterSuffix);
    }

    /// <summary>Replaces characters other than letters, digits, <c>-</c>, <c>_</c> and <c>.</c> with <c>-</c>.</summary>
    public static string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return string.Create(value.Length, value, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                span[i] = char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-';
            }
        });
    }

    private static string Logical(string prefix, string logical)
    {
        ArgumentException.ThrowIfNullOrEmpty(logical);
        return Sanitize(prefix ?? string.Empty) + Sanitize(logical);
    }

    // Pub/Sub ids: 3-255 characters, starting with a letter, not starting with "goog".
    private static string Id(string name, string suffix)
    {
        if (name.Length + suffix.Length < 3 || !char.IsAsciiLetter(name[0]) || name.StartsWith("goog", StringComparison.OrdinalIgnoreCase))
        {
            name = "sf-" + name;
        }

        if (name.Length + suffix.Length > MaxId)
        {
            string hash = XxHash32.HashToUInt32(Encoding.UTF8.GetBytes(name)).ToString("x8", CultureInfo.InvariantCulture);
            name = name[..(MaxId - suffix.Length - HashLength - 1)] + "-" + hash;
        }

        return name + suffix;
    }
}

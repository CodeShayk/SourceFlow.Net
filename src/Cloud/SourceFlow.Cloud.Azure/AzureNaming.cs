using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace SourceFlow.Cloud.Azure;

/// <summary>
/// Physical Service Bus names of logical endpoints and topics (doc 07 §3): the queue <c>{prefix}{endpoint}</c>, the topic
/// <c>{prefix}{topic}</c>, and on each subscribed topic the subscription named after the subscribing endpoint, so services
/// fan out and instances compete. Characters outside letters, digits, <c>-</c>, <c>_</c> and <c>.</c> become <c>-</c>; names
/// start and end with a letter or digit and fit the limits (260 for queues and topics, 50 for subscriptions), long ones
/// keeping a readable head and a hash. Queues and topics share one name space in a namespace.
/// </summary>
public static class AzureNaming
{
    /// <summary>The Service Bus limit on queue and topic names.</summary>
    public const int MaxEntityName = 260;

    /// <summary>The Service Bus limit on subscription and rule names.</summary>
    public const int MaxSubscriptionName = 50;

    private const int HashLength = 8;

    /// <summary>The queue of an endpoint.</summary>
    public static string Queue(string prefix, string endpoint)
    {
        return Name(prefix, endpoint, MaxEntityName);
    }

    /// <summary>The topic of a logical topic.</summary>
    public static string Topic(string prefix, string topic)
    {
        return Name(prefix, topic, MaxEntityName);
    }

    /// <summary>The subscription of an endpoint on a topic (one per subscribing service, doc 07 §4).</summary>
    public static string Subscription(string prefix, string endpoint)
    {
        return Name(prefix, endpoint, MaxSubscriptionName);
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

    private static string Name(string prefix, string logical, int max)
    {
        ArgumentException.ThrowIfNullOrEmpty(logical);
        string name = Sanitize((prefix ?? string.Empty) + logical);
        if (!char.IsAsciiLetterOrDigit(name[0]))
        {
            name = "sf" + name;
        }

        if (!char.IsAsciiLetterOrDigit(name[^1]))
        {
            name += "0";
        }

        if (name.Length <= max)
        {
            return name;
        }

        string hash = XxHash32.HashToUInt32(Encoding.UTF8.GetBytes(name)).ToString("x8", CultureInfo.InvariantCulture);
        return name[..(max - HashLength - 1)] + "-" + hash;
    }
}

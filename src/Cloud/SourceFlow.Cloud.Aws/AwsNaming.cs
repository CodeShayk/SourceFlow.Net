using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace SourceFlow.Cloud.Aws;

/// <summary>
/// Physical SQS and SNS names of logical endpoints and topics (doc 07 §3): <c>{prefix}{logical}</c> with characters
/// outside <c>[A-Za-z0-9_-]</c> replaced by <c>-</c>, <c>-dlq</c> for the dead-letter queue and <c>.fifo</c> for ordered
/// resources. Names are deterministic, so every service resolves the same topic, and they fit the 80-character SQS
/// limit (long names keep a readable head and a hash of the full name).
/// </summary>
public static class AwsNaming
{
    /// <summary>The FIFO suffix SQS and SNS require on ordered queues and topics.</summary>
    public const string FifoSuffix = ".fifo";

    /// <summary>The dead-letter queue suffix.</summary>
    public const string DeadLetterSuffix = "-dlq";

    // 80 (SQS) - ".fifo" - "-dlq" = 71 characters for the base name.
    private const int MaxBase = 80 - 5 - 4;
    private const int HashLength = 8;

    /// <summary>The base name (no suffix) of a logical resource.</summary>
    public static string Base(string prefix, string logical)
    {
        ArgumentException.ThrowIfNullOrEmpty(logical);
        string name = Sanitize((prefix ?? string.Empty) + logical);
        if (name.Length <= MaxBase)
        {
            return name;
        }

        string hash = XxHash32.HashToUInt32(Encoding.UTF8.GetBytes(name)).ToString("x8", CultureInfo.InvariantCulture);
        return name[..(MaxBase - HashLength - 1)] + "-" + hash;
    }

    /// <summary>The queue name of an endpoint.</summary>
    public static string Queue(string prefix, string endpoint, bool ordered)
    {
        return Base(prefix, endpoint) + (ordered ? FifoSuffix : string.Empty);
    }

    /// <summary>The dead-letter queue name of an endpoint.</summary>
    public static string DeadLetterQueue(string prefix, string endpoint, bool ordered)
    {
        return Base(prefix, endpoint) + DeadLetterSuffix + (ordered ? FifoSuffix : string.Empty);
    }

    /// <summary>The topic name of a logical topic.</summary>
    public static string Topic(string prefix, string topic, bool ordered)
    {
        return Base(prefix, topic) + (ordered ? FifoSuffix : string.Empty);
    }

    /// <summary>Replaces characters SQS and SNS do not allow in names with <c>-</c>.</summary>
    public static string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return string.Create(value.Length, value, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                span[i] = char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-';
            }
        });
    }

    /// <summary>The last segment of an ARN (the resource name).</summary>
    public static string NameOfArn(string arn)
    {
        ArgumentException.ThrowIfNullOrEmpty(arn);
        int colon = arn.LastIndexOf(':');
        return colon < 0 ? arn : arn[(colon + 1)..];
    }
}

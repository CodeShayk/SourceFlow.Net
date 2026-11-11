using System.Text.RegularExpressions;
using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.PubSub.V1;
using Grpc.Core;

namespace SourceFlow.Cloud.Gcp;

/// <summary>
/// Options of the Google Cloud Pub/Sub transport (doc 07 §6). Every option maps to a client setting; there are no options
/// the transport ignores (GAP-03). Credentials default to Application Default Credentials (workload identity, the
/// metadata server, <c>GOOGLE_APPLICATION_CREDENTIALS</c>); the emulator is detected through <c>PUBSUB_EMULATOR_HOST</c>.
/// </summary>
public sealed partial class GcpTransportOptions
{
    /// <summary>The Google Cloud project that owns the topics and subscriptions. Required.</summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// Whether the clients connect to the emulator named by <c>PUBSUB_EMULATOR_HOST</c>
    /// (<c>ClientBuilderBase.EmulatorDetection</c>). The default uses the emulator when the variable is set.
    /// </summary>
    public EmulatorDetection EmulatorDetection { get; set; } = EmulatorDetection.EmulatorOrProduction;

    /// <summary>
    /// A custom endpoint (<c>ClientBuilderBase.Endpoint</c>), for example the regional
    /// <c>europe-west1-pubsub.googleapis.com:443</c>, which keeps ordered messages in one region. Null uses the global one.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>Explicit credentials (<c>ClientBuilderBase.GoogleCredential</c>). Null uses Application Default Credentials.</summary>
    public GoogleCredential? Credential { get; set; }

    /// <summary>How many times a failed call is retried with backoff before the core's own retry policy takes over.</summary>
    public int MaxRpcRetries { get; set; } = 3;

    /// <summary>The deadline of one call to Pub/Sub, including its retries (<c>CallSettings.Expiration</c>).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a receiver waits between pulls that return nothing, while it waits for the first message. Shorter is more
    /// responsive and makes more (free, empty) pull calls.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// A prefix for every physical topic and subscription id, usually the environment (<c>prod-</c>), so that
    /// environments can share a project (doc 07 §3). Logical names stay unprefixed in routing.
    /// </summary>
    public string NamePrefix { get; set; } = string.Empty;

    /// <summary>How long a subscription keeps unacknowledged messages (<c>message_retention_duration</c>, 10 minutes to 7 days).</summary>
    public TimeSpan MessageRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Validates the options; the host fails at startup when one cannot be honoured.</summary>
    /// <exception cref="ArgumentException">An option is missing or out of range.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProjectId))
        {
            throw new ArgumentException("GcpTransportOptions.ProjectId is required.", nameof(ProjectId));
        }

        if (!ProjectIdPattern().IsMatch(ProjectId))
        {
            throw new ArgumentException($"GcpTransportOptions.ProjectId '{ProjectId}' is not a Google Cloud project id (6-30 lowercase letters, digits and hyphens, starting with a letter).", nameof(ProjectId));
        }

        if (!Enum.IsDefined(EmulatorDetection))
        {
            throw new ArgumentException($"GcpTransportOptions.EmulatorDetection '{EmulatorDetection}' is not defined.", nameof(EmulatorDetection));
        }

        if (Endpoint is { } endpoint && string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException("GcpTransportOptions.Endpoint must be null or a host[:port].", nameof(Endpoint));
        }

        if (MaxRpcRetries is < 0 or > 10)
        {
            throw new ArgumentException("GcpTransportOptions.MaxRpcRetries must be between 0 and 10.", nameof(MaxRpcRetries));
        }

        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentException("GcpTransportOptions.Timeout must be between 0 and 10 minutes.", nameof(Timeout));
        }

        if (PollInterval < TimeSpan.FromMilliseconds(10) || PollInterval > TimeSpan.FromSeconds(10))
        {
            throw new ArgumentException("GcpTransportOptions.PollInterval must be between 10 milliseconds and 10 seconds.", nameof(PollInterval));
        }

        if (MessageRetention < TimeSpan.FromMinutes(10) || MessageRetention > TimeSpan.FromDays(7))
        {
            throw new ArgumentException("GcpTransportOptions.MessageRetention must be between 10 minutes and 7 days (the Pub/Sub subscription limits).", nameof(MessageRetention));
        }

        ArgumentNullException.ThrowIfNull(NamePrefix, nameof(NamePrefix));
        if (NamePrefix.Length > 0 && GcpNaming.Sanitize(NamePrefix) != NamePrefix)
        {
            throw new ArgumentException("GcpTransportOptions.NamePrefix may contain only letters, digits, '-', '_' and '.'.", nameof(NamePrefix));
        }

        if (NamePrefix.Length > 100)
        {
            throw new ArgumentException("GcpTransportOptions.NamePrefix must be at most 100 characters (Pub/Sub ids are at most 255).", nameof(NamePrefix));
        }
    }

    /// <summary>The publisher client builder these options describe (endpoint, emulator detection, credentials).</summary>
    public PublisherServiceApiClientBuilder ToPublisherBuilder()
    {
        return new PublisherServiceApiClientBuilder { EmulatorDetection = EmulatorDetection, Endpoint = Endpoint, GoogleCredential = Credential };
    }

    /// <summary>The subscriber client builder these options describe (endpoint, emulator detection, credentials).</summary>
    public SubscriberServiceApiClientBuilder ToSubscriberBuilder()
    {
        return new SubscriberServiceApiClientBuilder { EmulatorDetection = EmulatorDetection, Endpoint = Endpoint, GoogleCredential = Credential };
    }

    /// <summary>
    /// The settings of every call: the <see cref="Timeout"/> deadline, and <see cref="MaxRpcRetries"/> retries with
    /// exponential backoff on unavailability, throttling, aborts and internal errors.
    /// </summary>
    public CallSettings ToCallSettings()
    {
        RetrySettings retry = RetrySettings.FromExponentialBackoff(
            maxAttempts: MaxRpcRetries + 1,
            initialBackoff: TimeSpan.FromMilliseconds(100),
            maxBackoff: TimeSpan.FromSeconds(10),
            backoffMultiplier: 2.0,
            retryFilter: RetrySettings.FilterForStatusCodes(StatusCode.Unavailable, StatusCode.ResourceExhausted, StatusCode.Aborted, StatusCode.Internal, StatusCode.DeadlineExceeded));
        return CallSettings.FromExpiration(Expiration.FromTimeout(Timeout)).WithRetry(retry);
    }

    // Project ids: 6-30 characters; legacy domain-scoped ids carry a "domain:" prefix.
    [GeneratedRegex("^([a-z0-9.-]+:)?[a-z][a-z0-9-]{4,28}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectIdPattern();
}

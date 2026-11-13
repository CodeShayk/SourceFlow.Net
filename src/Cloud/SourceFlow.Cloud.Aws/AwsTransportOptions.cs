using Amazon;
using Amazon.Extensions.NETCore.Setup;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.SimpleNotificationService;
using Amazon.SQS;

namespace SourceFlow.Cloud.Aws;

/// <summary>
/// Options of the AWS transport (doc 07 §6). Every option maps to an SDK setting; there are no options the transport
/// ignores (GAP-03). Credentials are not an option: the SDK credential chain is used (IAM role, IRSA, SSO, profile).
/// </summary>
public sealed class AwsTransportOptions
{
    /// <summary>The AWS region, for example <c>eu-west-1</c>. Null uses the SDK's region chain (<c>AWS_REGION</c>, profile).</summary>
    public string? Region { get; set; }

    /// <summary>A named profile in the shared credentials file. Null uses the default credential chain.</summary>
    public string? Profile { get; set; }

    /// <summary>
    /// A custom endpoint, for LocalStack (<c>http://localhost:4566</c>) or a VPC endpoint. Applies to SQS and SNS.
    /// </summary>
    public Uri? ServiceUrl { get; set; }

    /// <summary>How many times the SDK retries a failed call before the core's own retry policy takes over (<c>ClientConfig.MaxErrorRetry</c>).</summary>
    public int MaxSdkRetries { get; set; } = 3;

    /// <summary>The SDK's per-call HTTP timeout (<c>ClientConfig.Timeout</c>).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest long-poll a receive waits for the first message (<c>ReceiveMessageRequest.WaitTimeSeconds</c>, at most
    /// 20 s). The receive loop asks for shorter waits during shutdown; this caps longer ones.
    /// </summary>
    public TimeSpan ReceiveWaitTime { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A prefix for every physical queue and topic name, usually the environment (<c>prod-</c>), so that environments
    /// can share an account (doc 07 §3). Logical names stay unprefixed in routing.
    /// </summary>
    public string NamePrefix { get; set; } = string.Empty;

    /// <summary>How long messages wait on a queue before SQS drops them (<c>MessageRetentionPeriod</c>, 60 s to 14 days).</summary>
    public TimeSpan MessageRetention { get; set; } = TimeSpan.FromDays(14);

    /// <summary>The v2 wire bridge's allow-list, or null when the bridge is off (the default).</summary>
    public Migration.V2MessageMap? V2Messages { get; private set; }

    /// <summary>
    /// Turns on the v2 wire bridge (doc 07 §8): messages in the v2 SourceFlow.Cloud.AWS format (a <c>CommandType</c> or
    /// <c>EventType</c> attribute with an assembly-qualified name, directly on SQS or in an SNS notification) are
    /// translated through an explicit allow-list, with a deterministic id (UUIDv5 of the v2 message id). Unmapped v2
    /// messages are parked as unknown types. Off by default; planned for removal in 4.0.
    /// </summary>
    /// <param name="map">Maps v2 types: <c>map.Type("MyApp.Commands.CreateAccount, MyApp", "bank.account.open", 1)</c>.</param>
    public AwsTransportOptions AcceptV2Messages(Action<Migration.V2MessageMap> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        V2Messages ??= new Migration.V2MessageMap();
        map(V2Messages);
        return this;
    }

    /// <summary>Validates the options; the host fails at startup when one cannot be honoured.</summary>
    /// <exception cref="ArgumentException">An option is out of range.</exception>
    public void Validate()
    {
        if (Region is not null && RegionEndpoint.GetBySystemName(Region) is { DisplayName: "Unknown" } && ServiceUrl is null)
        {
            throw new ArgumentException($"AwsTransportOptions.Region '{Region}' is not a known AWS region.", nameof(Region));
        }

        if (MaxSdkRetries is < 0 or > 10)
        {
            throw new ArgumentException("AwsTransportOptions.MaxSdkRetries must be between 0 and 10.", nameof(MaxSdkRetries));
        }

        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentException("AwsTransportOptions.Timeout must be between 0 and 5 minutes.", nameof(Timeout));
        }

        if (ReceiveWaitTime < TimeSpan.Zero || ReceiveWaitTime > TimeSpan.FromSeconds(20))
        {
            throw new ArgumentException("AwsTransportOptions.ReceiveWaitTime must be between 0 and 20 seconds (the SQS long-poll limit).", nameof(ReceiveWaitTime));
        }

        if (MessageRetention < TimeSpan.FromMinutes(1) || MessageRetention > TimeSpan.FromDays(14))
        {
            throw new ArgumentException("AwsTransportOptions.MessageRetention must be between 1 minute and 14 days (the SQS limits).", nameof(MessageRetention));
        }

        ArgumentNullException.ThrowIfNull(NamePrefix, nameof(NamePrefix));
        if (NamePrefix.Length > 0 && AwsNaming.Sanitize(NamePrefix) != NamePrefix)
        {
            throw new ArgumentException("AwsTransportOptions.NamePrefix may contain only letters, digits, '-' and '_'.", nameof(NamePrefix));
        }

        if (NamePrefix.Length > 40)
        {
            throw new ArgumentException("AwsTransportOptions.NamePrefix must be at most 40 characters (SQS names are at most 80).", nameof(NamePrefix));
        }
    }

    /// <summary>
    /// The equivalent <see cref="AWSOptions"/> (region, profile, endpoint, retries, timeout), for applications that create
    /// further AWS clients with the same settings through <c>AWSOptions.CreateServiceClient</c>.
    /// </summary>
    public AWSOptions ToAwsOptions()
    {
        var options = new AWSOptions { Profile = Profile };
        if (Region is not null)
        {
            options.Region = RegionEndpoint.GetBySystemName(Region);
        }

        options.DefaultClientConfig.MaxErrorRetry = MaxSdkRetries;
        options.DefaultClientConfig.Timeout = Timeout;
        if (ServiceUrl is not null)
        {
            options.DefaultClientConfig.ServiceURL = ServiceUrl.ToString();
            options.DefaultClientConfig.AuthenticationRegion = Region;
        }

        return options;
    }

    /// <summary>The SQS client configuration these options describe.</summary>
    public AmazonSQSConfig ToSqsConfig()
    {
        return Apply(new AmazonSQSConfig());
    }

    /// <summary>The SNS client configuration these options describe.</summary>
    public AmazonSimpleNotificationServiceConfig ToSnsConfig()
    {
        return Apply(new AmazonSimpleNotificationServiceConfig());
    }

    /// <summary>
    /// The credentials of <see cref="Profile"/>, or null for the SDK's default chain (environment, web identity, SSO,
    /// container and instance roles).
    /// </summary>
    /// <exception cref="ArgumentException">The profile does not exist.</exception>
    public AWSCredentials? ResolveCredentials()
    {
        if (Profile is null)
        {
            return null;
        }

        return new CredentialProfileStoreChain().TryGetAWSCredentials(Profile, out AWSCredentials credentials)
            ? credentials
            : throw new ArgumentException($"The AWS profile '{Profile}' was not found in the shared credentials or config files.", nameof(Profile));
    }

    /// <summary>Applies the endpoint, region, retries and timeout to another AWS client configuration (KMS).</summary>
    internal T Apply<T>(T config)
        where T : ClientConfig
    {
        config.MaxErrorRetry = MaxSdkRetries;
        config.Timeout = Timeout;
        if (ServiceUrl is not null)
        {
            config.ServiceURL = ServiceUrl.ToString();
            config.AuthenticationRegion = Region;
        }
        else if (Region is not null)
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(Region);
        }

        return config;
    }
}

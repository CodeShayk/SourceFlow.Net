using System.Net;
using System.Net.Sockets;
using System.Text;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using SourceFlow.Cloud.Aws;
using SourceFlow.Testing.Conformance;
using SourceFlow.Transport;

namespace SourceFlow.Cloud.Tests.Aws;

/// <summary>
/// The AWS transport as a TCK fixture: on LocalStack (pull requests) with fault injection in the SDK's HTTP pipeline and
/// SDK credentials whose refreshes are counted, or on a real account (nightly, <c>SOURCEFLOW_REAL_CLOUD=aws</c>) without
/// faults. Each fixture uses its own name prefix and deletes its queues and topics when disposed.
/// </summary>
internal sealed class AwsTransportFixture : ITransportFixture
{
    private readonly AmazonSQSClient _sqs;
    private readonly AmazonSimpleNotificationServiceClient _sns;

    private AwsTransportFixture(AmazonSQSClient sqs, AmazonSimpleNotificationServiceClient sns, AwsFaults? faults, AwsTransportOptions options)
    {
        _sqs = sqs;
        _sns = sns;
        Faults = faults;
        Transport = new AwsTransport(sqs, sns, options);
    }

    public ITransport Transport { get; }

    public AwsTransport Aws => (AwsTransport)Transport;

    public string Prefix { get; } = "tck-" + Guid.CreateVersion7().ToString("N")[^10..];

    public bool SupportsClockControl => false;

    public TimeSpan ReceiveTimeout => TimeSpan.FromSeconds(20);

    public TimeSpan QuietPeriod => TimeSpan.FromSeconds(1.5);

    public TimeSpan DelayTolerance => TimeSpan.FromSeconds(3);

    public ITransportFaults? Faults { get; }

    /// <summary>A fixture on LocalStack, with fault injection.</summary>
    public static async Task<AwsTransportFixture> LocalStackAsync(LocalStackServer server)
    {
        await server.EnsureStartedAsync();
        var faults = new AwsFaults();
        AmazonSQSConfig sqsConfig = Configure(new AmazonSQSConfig(), server.Endpoint, faults);
        AmazonSimpleNotificationServiceConfig snsConfig = Configure(new AmazonSimpleNotificationServiceConfig(), server.Endpoint, faults);
        return new AwsTransportFixture(new AmazonSQSClient(faults.Credentials, sqsConfig), new AmazonSimpleNotificationServiceClient(faults.Credentials, snsConfig), faults, new AwsTransportOptions());
    }

    /// <summary>A fixture on the real account of the SDK credential chain (nightly), without fault injection.</summary>
    public static AwsTransportFixture RealCloud()
    {
        var options = new AwsTransportOptions { Region = Environment.GetEnvironmentVariable("AWS_REGION") };
        return new AwsTransportFixture(new AmazonSQSClient(options.ToSqsConfig()), new AmazonSimpleNotificationServiceClient(options.ToSnsConfig()), null, options);
    }

    public ValueTask AdvanceClockAsync(TimeSpan by)
    {
        throw new NotSupportedException("SQS uses the real clock.");
    }

    public async ValueTask<bool> InjectDefectAsync(TopologyProblemKind defect, TransportTopology topology, CancellationToken cancellationToken)
    {
        switch (defect)
        {
            case TopologyProblemKind.OrderingMismatch:
            {
                // FIFO-ness cannot change in place: replace the standard queue by a FIFO one with the same base name.
                string standard = (await _sqs.GetQueueUrlAsync(AwsNaming.Queue("", topology.Endpoint, ordered: false), cancellationToken)).QueueUrl;
                await _sqs.DeleteQueueAsync(standard, cancellationToken);
                await _sqs.CreateQueueAsync(new CreateQueueRequest
                {
                    QueueName = AwsNaming.Queue("", topology.Endpoint, ordered: true),
                    Attributes = new Dictionary<string, string> { ["FifoQueue"] = "true" },
                }, cancellationToken);
                return true;
            }

            case TopologyProblemKind.MissingDeadLetter:
            {
                string deadLetters = (await _sqs.GetQueueUrlAsync(AwsNaming.DeadLetterQueue("", topology.Endpoint, topology.OrderedEndpoint), cancellationToken)).QueueUrl;
                await _sqs.DeleteQueueAsync(deadLetters, cancellationToken);
                return true;
            }

            case TopologyProblemKind.MissingSubscription:
                await _sns.UnsubscribeAsync(await SubscriptionAsync(topology, cancellationToken), cancellationToken);
                return true;
            case TopologyProblemKind.FilterDrift:
                await _sns.SetSubscriptionAttributesAsync(await SubscriptionAsync(topology, cancellationToken), "FilterPolicy", "{\"ce_type\":[\"drifted.v1\"]}", cancellationToken);
                return true;
            case TopologyProblemKind.Misconfigured:
                await _sns.SetSubscriptionAttributesAsync(await SubscriptionAsync(topology, cancellationToken), "RawMessageDelivery", "false", cancellationToken);
                return true;
            default:
                return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            ListQueuesResponse queues = await _sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = Prefix });
            foreach (string url in queues.QueueUrls ?? [])
            {
                await _sqs.DeleteQueueAsync(url);
            }

            string? next = null;
            do
            {
                ListTopicsResponse page = await _sns.ListTopicsAsync(new ListTopicsRequest { NextToken = next });
                foreach (Topic topic in (page.Topics ?? []).Where(t => AwsNaming.NameOfArn(t.TopicArn).StartsWith(Prefix, StringComparison.Ordinal)))
                {
                    await _sns.DeleteTopicAsync(topic.TopicArn);
                }

                next = page.NextToken;
            }
            while (!string.IsNullOrEmpty(next));
        }
        catch (AmazonServiceException)
        {
            // Best effort: LocalStack is discarded with the run; real-cloud leftovers carry the tck- prefix.
        }
        finally
        {
            _sqs.Dispose();
            _sns.Dispose();
        }
    }

    private static T Configure<T>(T config, Uri endpoint, AwsFaults faults)
        where T : ClientConfig
    {
        config.ServiceURL = endpoint.ToString();
        config.AuthenticationRegion = LocalStackServer.Region;
        config.MaxErrorRetry = 0;   // the TCK checks the transport's own error reporting, not SDK retries
        config.Timeout = TimeSpan.FromSeconds(30);
        config.HttpClientFactory = new FaultInjectingHttpClientFactory(faults);
        return config;
    }

    private async Task<string> SubscriptionAsync(TransportTopology topology, CancellationToken cancellationToken)
    {
        string topic = topology.SubscribedTopics[0];
        string queueUrl = (await _sqs.GetQueueUrlAsync(AwsNaming.Queue("", topology.Endpoint, topology.OrderedEndpoint), cancellationToken)).QueueUrl;
        string queueArn = (await _sqs.GetQueueAttributesAsync(queueUrl, ["QueueArn"], cancellationToken)).Attributes["QueueArn"];
        string topicName = AwsNaming.Topic("", topic, topology.OrderedEndpoint);
        string topicArn = (await _sns.ListTopicsAsync(new ListTopicsRequest(), cancellationToken)).Topics.Single(t => AwsNaming.NameOfArn(t.TopicArn) == topicName).TopicArn;
        return (await _sns.ListSubscriptionsByTopicAsync(topicArn, cancellationToken)).Subscriptions.Single(s => s.Endpoint == queueArn).SubscriptionArn;
    }
}

/// <summary>Faults injected below the SDK, in its HTTP pipeline: outages, throttling, duplicates, credential expiry.</summary>
internal sealed class AwsFaults : ITransportFaults
{
    private int _throttled;
    private int _duplicates;

    public bool Unavailable { get; set; }

    public CountingCredentials Credentials { get; } = new();

    public int CredentialRefreshes => Credentials.Refreshes;

    public void ThrottleNext(int count, TimeSpan retryAfter)
    {
        Interlocked.Exchange(ref _throttled, count);
    }

    public void ExpireCredentials()
    {
        Credentials.ClearCredentials();
    }

    public void DuplicateNext(int count)
    {
        Interlocked.Exchange(ref _duplicates, count);
    }

    internal bool ConsumeThrottle()
    {
        return Volatile.Read(ref _throttled) > 0 && Interlocked.Decrement(ref _throttled) >= 0;
    }

    internal bool ConsumeDuplicate()
    {
        return Volatile.Read(ref _duplicates) > 0 && Interlocked.Decrement(ref _duplicates) >= 0;
    }
}

/// <summary>Static LocalStack credentials that expire on demand; the SDK refreshes them through its normal path.</summary>
internal sealed class CountingCredentials : RefreshingAWSCredentials
{
    private int _generated;

    /// <summary>Refreshes after the first resolution.</summary>
    public int Refreshes => Math.Max(0, Volatile.Read(ref _generated) - 1);

    protected override CredentialsRefreshState GenerateNewCredentials()
    {
        Interlocked.Increment(ref _generated);
        return new CredentialsRefreshState(new ImmutableCredentials("test", "test", null), TimeProvider.System.GetUtcNow().UtcDateTime.AddHours(1));
    }

    protected override Task<CredentialsRefreshState> GenerateNewCredentialsAsync()
    {
        return Task.FromResult(GenerateNewCredentials());
    }
}

/// <summary>Gives the SDK one shared <see cref="HttpClient"/> (pooled connections) whose handler injects the faults.</summary>
internal sealed class FaultInjectingHttpClientFactory(AwsFaults faults) : HttpClientFactory
{
    private readonly Lazy<HttpClient> _client = new(() => new HttpClient(new FaultHandler(faults, new SocketsHttpHandler())) { Timeout = Timeout.InfiniteTimeSpan });

    public override HttpClient CreateHttpClient(IClientConfig clientConfig)
    {
        return _client.Value;
    }

    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig)
    {
        return false;   // this factory caches
    }

    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig)
    {
        return false;
    }

    private sealed class FaultHandler(AwsFaults faults, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (faults.Unavailable)
            {
                throw new HttpRequestException("Injected outage: connection refused.", new SocketException((int)SocketError.ConnectionRefused));
            }

            bool sqs = request.Headers.TryGetValues("X-Amz-Target", out IEnumerable<string>? targets);
            string target = sqs ? targets!.First() : string.Empty;
            if (faults.ConsumeThrottle())
            {
                var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = sqs
                        ? new StringContent("{\"__type\":\"com.amazonaws.sqs#RequestThrottled\",\"message\":\"Injected throttling.\"}", Encoding.UTF8, "application/x-amz-json-1.0")
                        : new StringContent("<ErrorResponse><Error><Type>Sender</Type><Code>Throttling</Code><Message>Injected throttling.</Message></Error></ErrorResponse>", Encoding.UTF8, "text/xml"),
                    RequestMessage = request,
                };
                throttled.Headers.TryAddWithoutValidation("x-amzn-query-error", "RequestThrottled;Sender");
                return throttled;
            }

            if (target.EndsWith(".SendMessageBatch", StringComparison.Ordinal) || target.EndsWith(".SendMessage", StringComparison.Ordinal))
            {
                if (faults.ConsumeDuplicate())
                {
                    // The broker stores the message twice: the same signed request reaches SQS twice.
                    byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
                    using (HttpResponseMessage first = await base.SendAsync(Clone(request, body), cancellationToken))
                    {
                        first.EnsureSuccessStatusCode();
                    }

                    return await base.SendAsync(Clone(request, body), cancellationToken);
                }
            }

            return await base.SendAsync(request, cancellationToken);
        }

        private static HttpRequestMessage Clone(HttpRequestMessage request, byte[] body)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, Content = new ByteArrayContent(body) };
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content is not null)
            {
                foreach (KeyValuePair<string, IEnumerable<string>> header in request.Content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return clone;
        }
    }
}

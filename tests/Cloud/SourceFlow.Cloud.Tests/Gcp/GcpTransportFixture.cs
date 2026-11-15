using Google.Api.Gax;
using Google.Api.Gax.ResourceNames;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using SourceFlow.Cloud.Gcp;
using SourceFlow.Testing.Conformance;
using SourceFlow.Transport;
using Status = Grpc.Core.Status;

namespace SourceFlow.Cloud.Tests.Gcp;

/// <summary>
/// The Pub/Sub transport as a TCK fixture: on the emulator (pull requests) with fault injection in the gRPC call pipeline
/// and bearer tokens whose refreshes are counted, or on a real project (nightly, <c>SOURCEFLOW_REAL_CLOUD=gcp</c>) without
/// faults. Each fixture uses its own name prefix and deletes its topics and subscriptions when disposed.
/// </summary>
internal sealed class GcpTransportFixture : ITransportFixture
{
    private readonly PublisherServiceApiClient _publisher;
    private readonly SubscriberServiceApiClient _subscriber;
    private readonly GrpcChannel? _channel;
    private readonly string _projectId;

    private GcpTransportFixture(PublisherServiceApiClient publisher, SubscriberServiceApiClient subscriber, GcpFaults? faults, GcpTransportOptions options, GrpcChannel? channel)
    {
        _publisher = publisher;
        _subscriber = subscriber;
        _channel = channel;
        _projectId = options.ProjectId;
        Faults = faults;
        Transport = new GcpTransport(publisher, subscriber, options);
    }

    public ITransport Transport { get; }

    public GcpTransport Gcp => (GcpTransport)Transport;

    public string Prefix { get; } = "tck-" + Guid.CreateVersion7().ToString("N")[^10..];

    public bool SupportsClockControl => false;

    public TimeSpan ReceiveTimeout => TimeSpan.FromSeconds(20);

    public TimeSpan QuietPeriod => TimeSpan.FromSeconds(1.5);

    public TimeSpan DelayTolerance => TimeSpan.FromSeconds(3);

    public ITransportFaults? Faults { get; }

    /// <summary>A fixture on the emulator, with fault injection.</summary>
    public static async Task<GcpTransportFixture> EmulatorAsync(PubSubEmulatorServer server)
    {
        await server.EnsureStartedAsync();
        var faults = new GcpFaults();
        GrpcChannel channel = GrpcChannel.ForAddress("http://" + server.Host, new GrpcChannelOptions
        {
            Credentials = ChannelCredentials.Insecure,
            MaxReceiveMessageSize = null,
            MaxSendMessageSize = null,
        });
        CallInvoker invoker = channel.Intercept(new FaultInterceptor(faults));
        var options = new GcpTransportOptions { ProjectId = PubSubEmulatorServer.ProjectId, MaxRpcRetries = 0 };   // the TCK checks the transport's own error reporting
        return new GcpTransportFixture(
            new PublisherServiceApiClientBuilder { CallInvoker = invoker }.Build(),
            new SubscriberServiceApiClientBuilder { CallInvoker = invoker }.Build(),
            faults, options, channel);
    }

    /// <summary>A fixture on the real project in <c>GOOGLE_CLOUD_PROJECT</c> with Application Default Credentials (nightly).</summary>
    public static GcpTransportFixture RealCloud()
    {
        var options = new GcpTransportOptions
        {
            ProjectId = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT") ?? throw new InvalidOperationException("Set GOOGLE_CLOUD_PROJECT."),
            EmulatorDetection = EmulatorDetection.ProductionOnly,
        };
        return new GcpTransportFixture(options.ToPublisherBuilder().Build(), options.ToSubscriberBuilder().Build(), null, options, null);
    }

    public ValueTask AdvanceClockAsync(TimeSpan by)
    {
        throw new NotSupportedException("Pub/Sub uses the real clock.");
    }

    public async ValueTask<bool> InjectDefectAsync(TopologyProblemKind defect, TransportTopology topology, CancellationToken cancellationToken)
    {
        switch (defect)
        {
            case TopologyProblemKind.OrderingMismatch:
                // Ordering is immutable: recreate the endpoint subscription with the opposite setting.
                await RecreateAsync(GcpNaming.EndpointSubscription("", topology.Endpoint), s => s.EnableMessageOrdering = !s.EnableMessageOrdering, cancellationToken);
                return true;
            case TopologyProblemKind.MissingDeadLetter:
                await _subscriber.UpdateSubscriptionAsync(new UpdateSubscriptionRequest
                {
                    Subscription = new Subscription { SubscriptionName = Subscription(GcpNaming.EndpointSubscription("", topology.Endpoint)) },
                    UpdateMask = new FieldMask { Paths = { "dead_letter_policy" } },
                }, cancellationToken);
                return true;
            case TopologyProblemKind.MissingSubscription:
                await _subscriber.DeleteSubscriptionAsync(Subscription(GcpNaming.TopicSubscription("", topology.Endpoint, topology.SubscribedTopics[0])), cancellationToken);
                return true;
            case TopologyProblemKind.FilterDrift:
                // Filters are immutable: recreate the topic subscription with another filter.
                await RecreateAsync(GcpNaming.TopicSubscription("", topology.Endpoint, topology.SubscribedTopics[0]), s => s.Filter = "attributes.ce-type = \"drifted.v1\"", cancellationToken);
                return true;
            default:
                return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            var project = new ProjectName(_projectId);
            await foreach (Subscription subscription in _subscriber.ListSubscriptionsAsync(project))
            {
                if (subscription.SubscriptionName.SubscriptionId.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    await _subscriber.DeleteSubscriptionAsync(subscription.SubscriptionName);
                }
            }

            await foreach (Topic topic in _publisher.ListTopicsAsync(project))
            {
                if (topic.TopicName.TopicId.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    await _publisher.DeleteTopicAsync(topic.TopicName);
                }
            }
        }
        catch (RpcException)
        {
            // Best effort: the emulator is discarded with the run; real-cloud leftovers carry the tck- prefix.
        }
        finally
        {
            _channel?.Dispose();
        }
    }

    private SubscriptionName Subscription(string id)
    {
        return new SubscriptionName(_projectId, id);
    }

    private async Task RecreateAsync(string id, Action<Subscription> change, CancellationToken cancellationToken)
    {
        Subscription current = await _subscriber.GetSubscriptionAsync(Subscription(id), cancellationToken);
        var replacement = new Subscription
        {
            SubscriptionName = current.SubscriptionName,
            Topic = current.Topic,
            AckDeadlineSeconds = current.AckDeadlineSeconds,
            EnableMessageOrdering = current.EnableMessageOrdering,
            DeadLetterPolicy = current.DeadLetterPolicy,
            ExpirationPolicy = current.ExpirationPolicy,
            Filter = current.Filter,
        };
        replacement.Labels.Add(current.Labels);
        change(replacement);
        await _subscriber.DeleteSubscriptionAsync(current.SubscriptionName, cancellationToken);
        await _subscriber.CreateSubscriptionAsync(replacement, cancellationToken);
    }
}

/// <summary>Faults injected below the client, in its gRPC call pipeline: outages, throttling, duplicates, token expiry.</summary>
internal sealed class GcpFaults : ITransportFaults
{
    private readonly object _gate = new();
    private int _throttled;
    private long _retryAfterTicks;
    private int _duplicates;
    private string? _token;
    private int _issued;

    public bool Unavailable { get; set; }

    public int CredentialRefreshes => Math.Max(0, Volatile.Read(ref _issued) - 1);

    public TimeSpan RetryAfter => TimeSpan.FromTicks(Interlocked.Read(ref _retryAfterTicks));

    public void ThrottleNext(int count, TimeSpan retryAfter)
    {
        Interlocked.Exchange(ref _retryAfterTicks, retryAfter.Ticks);
        Interlocked.Exchange(ref _throttled, count);
    }

    public void ExpireCredentials()
    {
        lock (_gate)
        {
            _token = null;
        }
    }

    public void DuplicateNext(int count)
    {
        Interlocked.Exchange(ref _duplicates, count);
    }

    /// <summary>The current bearer token; an expired one is refreshed (and counted) on the next call.</summary>
    internal string Token()
    {
        lock (_gate)
        {
            return _token ??= "tck-token-" + Interlocked.Increment(ref _issued);
        }
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

/// <summary>Adds the bearer token to every call and injects the faults of <see cref="GcpFaults"/>.</summary>
internal sealed class FaultInterceptor(GcpFaults faults) : Interceptor
{
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        if (faults.Unavailable)
        {
            return Failed<TResponse>(new RpcException(new Status(StatusCode.Unavailable, "Injected outage: connection refused.")));
        }

        var headers = new Metadata();
        foreach (Metadata.Entry entry in context.Options.Headers ?? [])
        {
            headers.Add(entry);
        }

        headers.Add("authorization", "Bearer " + faults.Token());
        context = new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, context.Options.WithHeaders(headers));

        if (faults.ConsumeThrottle())
        {
            var status = new Google.Rpc.Status { Code = (int)Code.ResourceExhausted, Message = "Injected throttling." };
            status.Details.Add(Any.Pack(new RetryInfo { RetryDelay = Duration.FromTimeSpan(faults.RetryAfter) }));
            var trailers = new Metadata { { "grpc-status-details-bin", status.ToByteArray() } };
            return Failed<TResponse>(new RpcException(new Status(StatusCode.ResourceExhausted, "Injected throttling."), trailers));
        }

        // The broker stores a command twice: the same publish reaches an endpoint topic twice.
        if (request is PublishRequest publish && publish.Topic.EndsWith(GcpNaming.EndpointTopicSuffix, StringComparison.Ordinal) && faults.ConsumeDuplicate())
        {
            async Task<TResponse> TwiceAsync()
            {
                using (AsyncUnaryCall<TResponse> first = continuation(request, context))
                {
                    await first.ResponseAsync;
                }

                using AsyncUnaryCall<TResponse> second = continuation(request, context);
                return await second.ResponseAsync;
            }

            return new AsyncUnaryCall<TResponse>(TwiceAsync(), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
        }

        return continuation(request, context);
    }

    private static AsyncUnaryCall<TResponse> Failed<TResponse>(RpcException error)
    {
        return new AsyncUnaryCall<TResponse>(Task.FromException<TResponse>(error), Task.FromResult(new Metadata()), () => error.Status, () => error.Trailers, () => { });
    }
}

using System.Globalization;
using Google.Cloud.PubSub.V1;
using Google.Protobuf.WellKnownTypes;
using SourceFlow.Transport;

namespace SourceFlow.Cloud.Gcp;

/// <summary>
/// Creates and validates the Pub/Sub resources of a topology (ADR-017, doc 07 §5). Create makes what is missing: the
/// endpoint's dead-letter topic and subscription, its command topic and subscription, published topics, subscribed topics
/// that do not exist yet, and one subscription per subscribed topic named after the endpoint (GAP-13). Every endpoint
/// subscription has message ordering as the topology says (GAP-13), a dead-letter policy, the delivery limit label, the
/// translated filter, and no expiry. Existing resources are never changed; Validate then reports every difference at once.
/// </summary>
/// <remarks>
/// On Google Cloud, dead-lettering also needs the Pub/Sub service agent to publish to the dead-letter topic and subscribe
/// to the source subscriptions (IAM, which the reference Terraform module grants). Without it the receiver still enforces
/// the delivery limit itself.
/// </remarks>
internal sealed class GcpProvisioner
{
    /// <summary>The ack deadline of new subscriptions: the default lock duration, so receivers rarely need to change it.</summary>
    public const int AckDeadlineSeconds = 30;

    // Pub/Sub's own limits on max_delivery_attempts.
    private const int MinPolicyDeliveries = 5;
    private const int MaxPolicyDeliveries = 100;
    private const string DeletedTopic = "_deleted-topic_";

    private readonly GcpTransport _transport;

    public GcpProvisioner(GcpTransport transport)
    {
        _transport = transport;
    }

    private string Prefix => _transport.Prefix;

    /// <summary>The dead-letter policy's delivery limit for a topology limit (Pub/Sub accepts 5 to 100).</summary>
    public static int PolicyDeliveries(int maxDeliveries)
    {
        return Math.Clamp(maxDeliveries, MinPolicyDeliveries, MaxPolicyDeliveries);
    }

    public async ValueTask ProvisionAsync(TransportTopology topology, ProvisioningMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topology);
        foreach (string filter in topology.Filters.Values)
        {
            CloudEventFilter.Parse(filter);   // a malformed filter fails provisioning in every mode
        }

        if (mode == ProvisioningMode.None)
        {
            return;
        }

        if (mode == ProvisioningMode.CreateIfMissing)
        {
            await CreateAsync(topology, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<TopologyProblem> problems = await ValidateAsync(topology, cancellationToken).ConfigureAwait(false);
        if (problems.Count > 0)
        {
            throw new TopologyValidationException(problems);
        }
    }

    // ================================================================ create

    private async Task CreateAsync(TransportTopology topology, CancellationToken ct)
    {
        string endpoint = topology.Endpoint;
        string deadLetters = GcpNaming.DeadLetter(Prefix, endpoint);
        await EnsureTopicAsync(deadLetters, endpoint, ct).ConfigureAwait(false);
        await EnsureSubscriptionAsync(new Subscription
        {
            SubscriptionName = _transport.SubscriptionName(deadLetters),
            TopicAsTopicName = _transport.TopicName(deadLetters),
            AckDeadlineSeconds = AckDeadlineSeconds,
            MessageRetentionDuration = Duration.FromTimeSpan(TimeSpan.FromDays(7)),   // as long as Pub/Sub keeps messages
            ExpirationPolicy = new ExpirationPolicy(),                                 // never expires
        }, endpoint, ct).ConfigureAwait(false);

        string commands = GcpNaming.EndpointTopic(Prefix, endpoint);
        await EnsureTopicAsync(commands, endpoint, ct).ConfigureAwait(false);
        await EnsureSubscriptionAsync(EndpointSubscription(topology, GcpNaming.EndpointSubscription(Prefix, endpoint), commands, filter: null), endpoint, ct).ConfigureAwait(false);

        foreach (string topic in topology.PublishedTopics.Distinct(StringComparer.Ordinal))
        {
            await EnsureTopicAsync(GcpNaming.Topic(Prefix, topic), topic, ct).ConfigureAwait(false);
        }

        foreach (string topic in topology.SubscribedTopics.Distinct(StringComparer.Ordinal))
        {
            // A subscriber may start before the publisher: the topic is created now (topics have no ordering setting).
            string topicId = GcpNaming.Topic(Prefix, topic);
            await EnsureTopicAsync(topicId, topic, ct).ConfigureAwait(false);
            string? filter = GcpFilter.Translate(topology.Filters.GetValueOrDefault(topic));
            await EnsureSubscriptionAsync(EndpointSubscription(topology, GcpNaming.TopicSubscription(Prefix, endpoint, topic), topicId, filter), $"{topic} → {endpoint}", ct).ConfigureAwait(false);
        }
    }

    private Subscription EndpointSubscription(TransportTopology topology, string id, string topicId, string? filter)
    {
        var subscription = new Subscription
        {
            SubscriptionName = _transport.SubscriptionName(id),
            TopicAsTopicName = _transport.TopicName(topicId),
            AckDeadlineSeconds = AckDeadlineSeconds,
            EnableMessageOrdering = topology.OrderedEndpoint,
            MessageRetentionDuration = Duration.FromTimeSpan(_transport.Options.MessageRetention),
            ExpirationPolicy = new ExpirationPolicy(),
            DeadLetterPolicy = new DeadLetterPolicy
            {
                DeadLetterTopic = _transport.TopicName(GcpNaming.DeadLetter(Prefix, topology.Endpoint)).ToString(),
                MaxDeliveryAttempts = PolicyDeliveries(topology.MaxDeliveries),
            },
            Filter = filter ?? string.Empty,
        };
        subscription.Labels[GcpTransport.MaxDeliveriesLabel] = topology.MaxDeliveries.ToString(CultureInfo.InvariantCulture);
        return subscription;
    }

    private async Task EnsureTopicAsync(string id, string destination, CancellationToken ct)
    {
        if (await _transport.GetTopicAsync(id, destination, ct).ConfigureAwait(false) is not null)
        {
            return;
        }

        await GcpErrors.CallAsync(destination, async () =>
        {
            try
            {
                await _transport.Publisher.CreateTopicAsync(new Topic { TopicName = _transport.TopicName(id) }, _transport.Call(ct)).ConfigureAwait(false);
            }
            catch (Grpc.Core.RpcException e) when (GcpErrors.IsAlreadyExists(e))
            {
                // Created concurrently by another instance or service.
            }
        }, ct).ConfigureAwait(false);
    }

    private async Task EnsureSubscriptionAsync(Subscription subscription, string destination, CancellationToken ct)
    {
        if (await _transport.GetSubscriptionAsync(subscription.SubscriptionName.SubscriptionId, destination, ct).ConfigureAwait(false) is not null)
        {
            return;
        }

        await GcpErrors.CallAsync(destination, async () =>
        {
            try
            {
                await _transport.Subscriber.CreateSubscriptionAsync(subscription, _transport.Call(ct)).ConfigureAwait(false);
            }
            catch (Grpc.Core.RpcException e) when (GcpErrors.IsAlreadyExists(e))
            {
                // Created concurrently by another instance.
            }
        }, ct).ConfigureAwait(false);
    }

    // ================================================================ validate

    private async Task<IReadOnlyList<TopologyProblem>> ValidateAsync(TransportTopology topology, CancellationToken ct)
    {
        var problems = new List<TopologyProblem>();
        string endpoint = topology.Endpoint;
        string deadLetters = GcpNaming.DeadLetter(Prefix, endpoint);
        bool deadLetterTopic = await _transport.GetTopicAsync(deadLetters, endpoint, ct).ConfigureAwait(false) is not null;
        if (!deadLetterTopic)
        {
            problems.Add(new TopologyProblem(TopologyProblemKind.MissingDeadLetter, endpoint, $"the dead-letter topic '{deadLetters}' does not exist"));
        }
        else if (await _transport.GetSubscriptionAsync(deadLetters, endpoint, ct).ConfigureAwait(false) is null)
        {
            problems.Add(new TopologyProblem(TopologyProblemKind.MissingDeadLetter, endpoint, $"the dead-letter subscription '{deadLetters}' does not exist, so nothing ingests the dead letters"));
        }

        string commands = GcpNaming.EndpointTopic(Prefix, endpoint);
        string endpointId = GcpNaming.EndpointSubscription(Prefix, endpoint);
        if (await _transport.GetTopicAsync(commands, endpoint, ct).ConfigureAwait(false) is null)
        {
            problems.Add(new TopologyProblem(TopologyProblemKind.MissingEndpoint, endpoint, $"the command topic '{commands}' does not exist"));
        }

        Subscription? endpointSubscription = await _transport.GetSubscriptionAsync(endpointId, endpoint, ct).ConfigureAwait(false);
        if (endpointSubscription is null)
        {
            problems.Add(new TopologyProblem(TopologyProblemKind.MissingEndpoint, endpoint, $"the subscription '{endpointId}' does not exist"));
        }
        else
        {
            CheckSubscription(topology, endpointSubscription, endpoint, commands, expectedFilter: null, deadLetters, problems);
        }

        foreach (string topic in topology.PublishedTopics.Distinct(StringComparer.Ordinal))
        {
            string topicId = GcpNaming.Topic(Prefix, topic);
            if (await _transport.GetTopicAsync(topicId, topic, ct).ConfigureAwait(false) is null)
            {
                problems.Add(new TopologyProblem(TopologyProblemKind.MissingTopic, topic, $"the Pub/Sub topic '{topicId}' does not exist"));
            }
        }

        foreach (string topic in topology.SubscribedTopics.Distinct(StringComparer.Ordinal))
        {
            string resource = $"{topic} → {endpoint}";
            string topicId = GcpNaming.Topic(Prefix, topic);
            if (await _transport.GetTopicAsync(topicId, topic, ct).ConfigureAwait(false) is null)
            {
                problems.Add(new TopologyProblem(TopologyProblemKind.MissingTopic, topic, $"the subscribed Pub/Sub topic '{topicId}' does not exist"));
                continue;
            }

            string subscriptionId = GcpNaming.TopicSubscription(Prefix, endpoint, topic);
            Subscription? subscription = await _transport.GetSubscriptionAsync(subscriptionId, resource, ct).ConfigureAwait(false);
            if (subscription is null)
            {
                problems.Add(new TopologyProblem(TopologyProblemKind.MissingSubscription, resource, $"the subscription '{subscriptionId}' of the endpoint to the topic '{topicId}' does not exist"));
                continue;
            }

            string? expectedFilter = GcpFilter.Translate(topology.Filters.GetValueOrDefault(topic));
            CheckSubscription(topology, subscription, resource, topicId, expectedFilter, deadLetters, problems);
            if (!GcpFilter.Equivalent(expectedFilter, subscription.Filter))
            {
                problems.Add(new TopologyProblem(TopologyProblemKind.FilterDrift, resource,
                    $"expected filter {expectedFilter ?? "(none)"}, found {(string.IsNullOrWhiteSpace(subscription.Filter) ? "(none)" : subscription.Filter)} (filter '{topology.Filters.GetValueOrDefault(topic) ?? "(none)"}'; Pub/Sub filters are immutable: recreate the subscription)"));
            }
        }

        return problems;
    }

    private void CheckSubscription(TransportTopology topology, Subscription subscription, string resource, string topicId, string? expectedFilter, string deadLetters, List<TopologyProblem> problems)
    {
        string id = subscription.SubscriptionName.SubscriptionId;
        if (subscription.Topic == DeletedTopic || subscription.TopicAsTopicName?.TopicId != topicId)
        {
            problems.Add(new TopologyProblem(TopologyProblemKind.Misconfigured, resource, $"the subscription '{id}' is attached to '{subscription.Topic}', not to the topic '{topicId}'"));
        }

        if (subscription.EnableMessageOrdering != topology.OrderedEndpoint)
        {
            problems.Add(new TopologyProblem(TopologyProblemKind.OrderingMismatch, resource,
                $"expected ordered={topology.OrderedEndpoint}, found the subscription '{id}' with message ordering {(subscription.EnableMessageOrdering ? "enabled" : "disabled")} (ordering is immutable: recreate the subscription)"));
        }

        if (expectedFilter is null && subscription.TopicAsTopicName?.TopicId == GcpNaming.EndpointTopic(Prefix, topology.Endpoint) && !string.IsNullOrWhiteSpace(subscription.Filter))
        {
            problems.Add(new TopologyProblem(TopologyProblemKind.Misconfigured, resource, $"the command subscription '{id}' filters messages ({subscription.Filter})"));
        }

        string expectedTopic = _transport.TopicName(deadLetters).ToString();
        DeadLetterPolicy? policy = subscription.DeadLetterPolicy;
        if (policy is null || string.IsNullOrEmpty(policy.DeadLetterTopic))
        {
            problems.Add(new TopologyProblem(TopologyProblemKind.MissingDeadLetter, resource, $"the subscription '{id}' has no dead-letter policy"));
        }
        else if (policy.DeadLetterTopic != expectedTopic)
        {
            problems.Add(new TopologyProblem(TopologyProblemKind.MissingDeadLetter, resource, $"expected the dead-letter topic '{expectedTopic}', found '{policy.DeadLetterTopic}'"));
        }
        else if (policy.MaxDeliveryAttempts != PolicyDeliveries(topology.MaxDeliveries) || GcpTransport.MaxDeliveries(subscription) != topology.MaxDeliveries)
        {
            problems.Add(new TopologyProblem(TopologyProblemKind.MissingDeadLetter, resource,
                $"expected max deliveries {topology.MaxDeliveries}, found {GcpTransport.MaxDeliveries(subscription).ToString(CultureInfo.InvariantCulture)} (policy {policy.MaxDeliveryAttempts.ToString(CultureInfo.InvariantCulture)})"));
        }

        if (subscription.ExpirationPolicy?.Ttl is { } ttl && ttl.ToTimeSpan() > TimeSpan.Zero)
        {
            problems.Add(new TopologyProblem(TopologyProblemKind.Misconfigured, resource, $"the subscription '{id}' expires after {ttl.ToTimeSpan()} without receivers, and its messages with it"));
        }
    }
}

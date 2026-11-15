using Azure;
using Azure.Messaging.ServiceBus.Administration;
using SourceFlow.Transport;

namespace SourceFlow.Cloud.Azure;

/// <summary>
/// Creates and validates the Service Bus entities of a topology (ADR-017, doc 07 §5). Create makes what is missing: the
/// endpoint queue (sessions on when ordered, the delivery limit, lock, time to live, dead-lettering on expiry, duplicate
/// detection when configured), published topics, subscribed topics that do not exist yet, and on each subscribed topic a
/// subscription named after the endpoint that forwards to its queue with the filter's rules. Existing entities are never
/// changed; Validate then reports every difference at once.
/// </summary>
internal sealed class AzureProvisioner : TopologyProvisioner
{
    private readonly AzureTransport _transport;

    public AzureProvisioner(AzureTransport transport)
    {
        _transport = transport;
    }

    private ServiceBusAdministrationClient Admin => _transport.Administration;

    private string Prefix => _transport.Prefix;

    // ================================================================ create

    protected override async ValueTask CreateAsync(TransportTopology topology, CancellationToken ct)
    {
        string endpoint = topology.Endpoint;
        string queue = AzureNaming.Queue(Prefix, endpoint);
        if (!await ExistsAsync(endpoint, () => Admin.QueueExistsAsync(queue, ct)).ConfigureAwait(false))
        {
            AzureTransportOptions options = _transport.Options;
            var create = new CreateQueueOptions(queue)
            {
                RequiresSession = topology.OrderedEndpoint,
                MaxDeliveryCount = topology.MaxDeliveries,
                LockDuration = options.LockDuration,
                DefaultMessageTimeToLive = options.MessageTimeToLive,
                DeadLetteringOnMessageExpiration = true,
                RequiresDuplicateDetection = options.DuplicateDetectionWindow is not null,
            };
            if (options.DuplicateDetectionWindow is { } window)
            {
                create.DuplicateDetectionHistoryTimeWindow = window;
            }

            await CreateAsync(endpoint, () => Admin.CreateQueueAsync(create, ct)).ConfigureAwait(false);
        }

        foreach (string topic in topology.PublishedTopics.Concat(topology.SubscribedTopics).Distinct(StringComparer.Ordinal))
        {
            // A subscriber may start before the publisher: the topic is created now.
            string name = AzureNaming.Topic(Prefix, topic);
            if (!await ExistsAsync(topic, () => Admin.TopicExistsAsync(name, ct)).ConfigureAwait(false))
            {
                await CreateAsync(topic, () => Admin.CreateTopicAsync(new CreateTopicOptions(name) { DefaultMessageTimeToLive = _transport.Options.MessageTimeToLive }, ct)).ConfigureAwait(false);
            }
        }

        string subscription = AzureNaming.Subscription(Prefix, endpoint);
        foreach (string topic in topology.SubscribedTopics.Distinct(StringComparer.Ordinal))
        {
            string resource = $"{topic} → {endpoint}";
            string name = AzureNaming.Topic(Prefix, topic);
            if (await ExistsAsync(resource, () => Admin.SubscriptionExistsAsync(name, subscription, ct)).ConfigureAwait(false))
            {
                continue;
            }

            IReadOnlyList<CreateRuleOptions> rules = AzureFilter.Rules(topology.Filters.GetValueOrDefault(topic));
            await CreateAsync(resource, () => Admin.CreateSubscriptionAsync(new CreateSubscriptionOptions(name, subscription)
            {
                ForwardTo = queue,
                DefaultMessageTimeToLive = _transport.Options.MessageTimeToLive,
            }, rules[0], ct)).ConfigureAwait(false);
            foreach (CreateRuleOptions rule in rules.Skip(1))
            {
                await CreateAsync(resource, () => Admin.CreateRuleAsync(name, subscription, rule, ct)).ConfigureAwait(false);
            }
        }
    }

    private static Task<bool> ExistsAsync(string destination, Func<Task<Response<bool>>> exists)
    {
        return AzureErrors.CallAsync(destination, async () => (await exists().ConfigureAwait(false)).Value);
    }

    private static async Task CreateAsync(string destination, Func<Task> create)
    {
        try
        {
            await AzureErrors.CallAsync(destination, create).ConfigureAwait(false);
        }
        catch (TransportException e) when (e.InnerException is { } inner && AzureErrors.IsAlreadyExists(inner))
        {
            // Created concurrently by another instance or service.
        }
    }

    // ================================================================ validate

    protected override async ValueTask<IReadOnlyList<TopologyProblem>> ValidateAsync(TransportTopology topology, CancellationToken ct)
    {
        var problems = new List<TopologyProblem>();
        string endpoint = topology.Endpoint;
        string queueName = AzureNaming.Queue(Prefix, endpoint);
        QueueProperties? queue = await GetAsync(endpoint, () => Admin.GetQueueAsync(queueName, ct)).ConfigureAwait(false);
        if (queue is null)
        {
            problems.Add(new TopologyProblem(TopologyProblemKind.MissingEndpoint, endpoint, $"the queue '{queueName}' does not exist"));
        }
        else
        {
            _transport.Remember(endpoint, queue.RequiresSession);
            if (queue.RequiresSession != topology.OrderedEndpoint)
            {
                problems.Add(new TopologyProblem(TopologyProblemKind.OrderingMismatch, endpoint,
                    $"expected ordered={topology.OrderedEndpoint}, found the queue '{queueName}' with sessions {(queue.RequiresSession ? "on" : "off")} (sessions are immutable: recreate the queue)"));
            }

            if (queue.MaxDeliveryCount != topology.MaxDeliveries)
            {
                problems.Add(new TopologyProblem(TopologyProblemKind.MissingDeadLetter, endpoint, $"expected max deliveries {topology.MaxDeliveries} before the dead-letter queue, found MaxDeliveryCount {queue.MaxDeliveryCount}"));
            }

            if (!string.IsNullOrEmpty(queue.ForwardTo))
            {
                problems.Add(new TopologyProblem(TopologyProblemKind.Misconfigured, endpoint, $"the queue '{queueName}' forwards its messages to '{queue.ForwardTo}', so its receivers get nothing"));
            }

            if (_transport.Options.DuplicateDetectionWindow is not null && !queue.RequiresDuplicateDetection)
            {
                problems.Add(new TopologyProblem(TopologyProblemKind.Misconfigured, endpoint, $"duplicate detection is configured but the queue '{queueName}' has it off (immutable: recreate the queue)"));
            }
        }

        foreach (string topic in topology.PublishedTopics.Distinct(StringComparer.Ordinal))
        {
            string name = AzureNaming.Topic(Prefix, topic);
            if (!await ExistsAsync(topic, () => Admin.TopicExistsAsync(name, ct)).ConfigureAwait(false))
            {
                problems.Add(new TopologyProblem(TopologyProblemKind.MissingTopic, topic, $"the topic '{name}' does not exist{await QueueNamedAsync(name, ct).ConfigureAwait(false)}"));
            }
        }

        string subscription = AzureNaming.Subscription(Prefix, endpoint);
        foreach (string topic in topology.SubscribedTopics.Distinct(StringComparer.Ordinal))
        {
            string resource = $"{topic} → {endpoint}";
            string name = AzureNaming.Topic(Prefix, topic);
            if (!await ExistsAsync(topic, () => Admin.TopicExistsAsync(name, ct)).ConfigureAwait(false))
            {
                problems.Add(new TopologyProblem(TopologyProblemKind.MissingTopic, topic, $"the subscribed topic '{name}' does not exist{await QueueNamedAsync(name, ct).ConfigureAwait(false)}"));
                continue;
            }

            SubscriptionProperties? found = await GetAsync(resource, () => Admin.GetSubscriptionAsync(name, subscription, ct)).ConfigureAwait(false);
            if (found is null)
            {
                problems.Add(new TopologyProblem(TopologyProblemKind.MissingSubscription, resource, $"the topic '{name}' has no subscription '{subscription}' for the endpoint"));
                continue;
            }

            if (!ForwardsTo(found.ForwardTo, queueName))
            {
                problems.Add(new TopologyProblem(TopologyProblemKind.Misconfigured, resource, $"the subscription '{subscription}' forwards to '{(string.IsNullOrEmpty(found.ForwardTo) ? "(nothing)" : found.ForwardTo)}', not to the queue '{queueName}'"));
            }

            var actual = new List<RuleFilter>();
            await AzureErrors.CallAsync(resource, async () =>
            {
                await foreach (RuleProperties rule in Admin.GetRulesAsync(name, subscription, ct).ConfigureAwait(false))
                {
                    actual.Add(rule.Filter);
                }
            }).ConfigureAwait(false);
            string? filter = topology.Filters.GetValueOrDefault(topic);
            IReadOnlyList<CreateRuleOptions> expected = AzureFilter.Rules(filter);
            if (!AzureFilter.Equivalent(expected.Select(r => r.Filter), actual))
            {
                problems.Add(new TopologyProblem(TopologyProblemKind.FilterDrift, resource,
                    $"expected rules [{string.Join(", ", expected.Select(r => AzureFilter.Describe(r.Filter)))}], found [{string.Join(", ", actual.Select(AzureFilter.Describe))}] (filter '{filter ?? "(none)"}')"));
            }
        }

        return problems;
    }

    // Service Bus reports ForwardTo as a URI (http://{namespace}/{queue}); names are case-insensitive.
    private static bool ForwardsTo(string? forwardTo, string queue)
    {
        if (string.IsNullOrEmpty(forwardTo))
        {
            return false;
        }

        string target = Uri.TryCreate(forwardTo, UriKind.Absolute, out Uri? uri) ? uri.AbsolutePath.Trim('/') : forwardTo;
        return string.Equals(target, queue, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> QueueNamedAsync(string name, CancellationToken ct)
    {
        return await ExistsAsync(name, () => Admin.QueueExistsAsync(name, ct)).ConfigureAwait(false)
            ? $" (a queue has that name: queues and topics share names in a namespace)"
            : string.Empty;
    }

    private static async Task<T?> GetAsync<T>(string destination, Func<Task<Response<T>>> get)
        where T : class
    {
        try
        {
            return await AzureErrors.CallAsync(destination, async () => (await get().ConfigureAwait(false)).Value).ConfigureAwait(false);
        }
        catch (DestinationNotFoundException)
        {
            return null;
        }
    }
}

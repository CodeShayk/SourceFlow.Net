using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using SourceFlow.Transport;

namespace SourceFlow.Cloud.Aws;

/// <summary>
/// Creates and validates the SQS/SNS resources of a topology (ADR-017, doc 07 §5). Create makes what is missing: the
/// endpoint queue with a redrive policy to its dead-letter queue (FIFO when ordered), published topics, subscribed topics
/// that do not exist yet, raw-delivery subscriptions with the filter policy, and the queue policy that lets the topics
/// send. Existing resources are never changed; Validate then reports every difference at once.
/// </summary>
internal sealed class AwsProvisioner
{
    private const string RawDelivery = "RawMessageDelivery";
    private const string FilterPolicy = "FilterPolicy";
    private const string FilterPolicyScope = "FilterPolicyScope";
    private const string PolicySid = "sourceflow-sns-to-sqs";

    private readonly AwsTransport _transport;

    public AwsProvisioner(AwsTransport transport)
    {
        _transport = transport;
    }

    private AwsResources Resources => _transport.Resources;

    private string Prefix => Resources.Prefix;

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
        QueueRef? queue = await Resources.FindQueueAsync(topology.Endpoint, ct, fresh: true).ConfigureAwait(false)
            ?? await CreateEndpointAsync(topology, ct).ConfigureAwait(false);

        foreach (string topic in topology.PublishedTopics.Distinct(StringComparer.Ordinal))
        {
            await EnsureTopicAsync(topic, topology.OrderedTopics.Contains(topic, StringComparer.Ordinal), ct).ConfigureAwait(false);
        }

        if (topology.SubscribedTopics.Count == 0)
        {
            return;
        }

        string queueArn = await Resources.QueueArnAsync(queue.Url, topology.Endpoint, ct).ConfigureAwait(false);
        var topicArns = new List<string>();
        foreach (string topic in topology.SubscribedTopics.Distinct(StringComparer.Ordinal))
        {
            // A subscriber may start before the publisher: the topic is created with the subscriber's ordering.
            TopicRef reference = await EnsureTopicAsync(topic, topology.OrderedEndpoint, ct).ConfigureAwait(false);
            topicArns.Add(reference.Arn);
            if (reference.Ordered != queue.Ordered || await FindSubscriptionAsync(reference.Arn, queueArn, topic, ct).ConfigureAwait(false) is not null)
            {
                continue;   // an ordering mismatch cannot be subscribed (Validate reports it); an existing one is kept
            }

            var attributes = new Dictionary<string, string>(StringComparer.Ordinal) { [RawDelivery] = "true" };
            if (AwsFilterPolicy.Translate(topology.Filters.GetValueOrDefault(topic)) is { } policy)
            {
                attributes[FilterPolicy] = policy;
                attributes[FilterPolicyScope] = "MessageAttributes";
            }

            await AwsErrors.CallAsync(topic, () => _transport.Sns.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = reference.Arn,
                Protocol = "sqs",
                Endpoint = queueArn,
                Attributes = attributes,
                ReturnSubscriptionArn = true,
            }, ct)).ConfigureAwait(false);
        }

        await EnsureQueuePolicyAsync(topology.Endpoint, queue, queueArn, topicArns, ct).ConfigureAwait(false);
    }

    private async Task<QueueRef> CreateEndpointAsync(TransportTopology topology, CancellationToken ct)
    {
        bool ordered = topology.OrderedEndpoint;
        string retention = ((int)_transport.Options.MessageRetention.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        string deadLetterName = AwsNaming.DeadLetterQueue(Prefix, topology.Endpoint, ordered);
        string deadLetterUrl = await Resources.QueueUrlAsync(deadLetterName, topology.Endpoint, ct).ConfigureAwait(false)
            ?? await CreateQueueAsync(deadLetterName, topology.Endpoint, ordered, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // The dead-letter queue keeps messages as long as SQS allows, for the ingestor and for inspection.
                ["MessageRetentionPeriod"] = "1209600",
            }, ct).ConfigureAwait(false);
        string deadLetterArn = await Resources.QueueArnAsync(deadLetterUrl, topology.Endpoint, ct).ConfigureAwait(false);

        string name = AwsNaming.Queue(Prefix, topology.Endpoint, ordered);
        string url = await CreateQueueAsync(name, topology.Endpoint, ordered, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MessageRetentionPeriod"] = retention,
            ["RedrivePolicy"] = new JsonObject { ["deadLetterTargetArn"] = deadLetterArn, ["maxReceiveCount"] = topology.MaxDeliveries }.ToJsonString(),
        }, ct).ConfigureAwait(false);

        var queue = new QueueRef(url, name, ordered);
        Resources.Remember(topology.Endpoint, queue);
        return queue;
    }

    private Task<string> CreateQueueAsync(string name, string destination, bool ordered, Dictionary<string, string> attributes, CancellationToken ct)
    {
        if (ordered)
        {
            attributes["FifoQueue"] = "true";
        }

        return AwsErrors.CallAsync(destination, async () =>
            (await _transport.Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = attributes }, ct).ConfigureAwait(false)).QueueUrl);
    }

    private async Task<TopicRef> EnsureTopicAsync(string topic, bool ordered, CancellationToken ct)
    {
        if (await Resources.FindTopicAsync(topic, ct, fresh: true).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        string name = AwsNaming.Topic(Prefix, topic, ordered);
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ordered)
        {
            attributes["FifoTopic"] = "true";
        }

        string arn = await AwsErrors.CallAsync(topic, async () =>
            (await _transport.Sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attributes }, ct).ConfigureAwait(false)).TopicArn).ConfigureAwait(false);
        var reference = new TopicRef(arn, name, ordered);
        Resources.Remember(topic, reference);
        return reference;
    }

    // SNS may only send to the queue when the queue policy allows the topics (a no-op on LocalStack, required on AWS).
    private async Task EnsureQueuePolicyAsync(string endpoint, QueueRef queue, string queueArn, IReadOnlyList<string> topicArns, CancellationToken ct)
    {
        string? current = await QueueAttributeAsync(queue, endpoint, "Policy", ct).ConfigureAwait(false);
        if (current is not null && topicArns.All(arn => current.Contains(arn, StringComparison.Ordinal)))
        {
            return;
        }

        string policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = PolicySid,
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = queueArn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = new JsonArray([.. topicArns.Order(StringComparer.Ordinal).Select(a => (JsonNode)JsonValue.Create(a))]) } },
            }),
        }.ToJsonString();

        await AwsErrors.CallAsync(endpoint, () => _transport.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queue.Url,
            Attributes = new Dictionary<string, string>(StringComparer.Ordinal) { ["Policy"] = policy },
        }, ct)).ConfigureAwait(false);
    }

    // ================================================================ validate

    private async Task<IReadOnlyList<TopologyProblem>> ValidateAsync(TransportTopology topology, CancellationToken ct)
    {
        var problems = new List<TopologyProblem>();
        QueueRef? queue = await ValidateEndpointAsync(topology, problems, ct).ConfigureAwait(false);

        foreach (string topic in topology.PublishedTopics.Distinct(StringComparer.Ordinal))
        {
            bool ordered = topology.OrderedTopics.Contains(topic, StringComparer.Ordinal);
            TopicRef? found = await Resources.FindTopicAsync(topic, ct, fresh: true).ConfigureAwait(false);
            if (found is null)
            {
                problems.Add(new TopologyProblem(TopologyProblemKind.MissingTopic, topic, $"the SNS topic '{AwsNaming.Topic(Prefix, topic, ordered)}' does not exist"));
            }
            else if (found.Ordered != ordered)
            {
                problems.Add(new TopologyProblem(TopologyProblemKind.OrderingMismatch, topic, $"expected ordered={ordered}, found the {Kind(found.Ordered)} topic '{found.Name}'"));
            }
        }

        string? queueArn = queue is null || topology.SubscribedTopics.Count == 0 ? null : await Resources.QueueArnAsync(queue.Url, topology.Endpoint, ct).ConfigureAwait(false);
        string? queuePolicy = queue is null || topology.SubscribedTopics.Count == 0 ? null : await QueueAttributeAsync(queue, topology.Endpoint, "Policy", ct).ConfigureAwait(false);
        foreach (string topic in topology.SubscribedTopics.Distinct(StringComparer.Ordinal))
        {
            string resource = $"{topic} → {topology.Endpoint}";
            TopicRef? found = await Resources.FindTopicAsync(topic, ct, fresh: true).ConfigureAwait(false);
            if (found is null)
            {
                problems.Add(new TopologyProblem(TopologyProblemKind.MissingTopic, topic, $"the subscribed SNS topic '{AwsNaming.Topic(Prefix, topic, topology.OrderedEndpoint)}' does not exist"));
                continue;
            }

            if (queue is null || queueArn is null)
            {
                problems.Add(new TopologyProblem(TopologyProblemKind.MissingSubscription, resource, "the endpoint queue does not exist, so it cannot be subscribed"));
                continue;
            }

            if (found.Ordered != queue.Ordered)
            {
                problems.Add(new TopologyProblem(TopologyProblemKind.OrderingMismatch, resource, $"the {Kind(found.Ordered)} topic '{found.Name}' cannot deliver to the {Kind(queue.Ordered)} queue '{queue.Name}'"));
            }

            string? subscription = await FindSubscriptionAsync(found.Arn, queueArn, topic, ct).ConfigureAwait(false);
            if (subscription is null)
            {
                problems.Add(new TopologyProblem(TopologyProblemKind.MissingSubscription, resource, $"the queue '{queue.Name}' is not subscribed to the topic '{found.Name}'"));
                continue;
            }

            Dictionary<string, string> attributes = await AwsErrors.CallAsync(topic, async () =>
                (await _transport.Sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subscription }, ct).ConfigureAwait(false)).Attributes ?? []).ConfigureAwait(false);
            if (!string.Equals(attributes.GetValueOrDefault(RawDelivery), "true", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(new TopologyProblem(TopologyProblemKind.Misconfigured, resource, "raw message delivery is off: SQS would receive the SNS JSON wrapper instead of the CloudEvent (GAP-02)"));
            }

            if (attributes.GetValueOrDefault(FilterPolicyScope) is { } scope && scope != "MessageAttributes")
            {
                problems.Add(new TopologyProblem(TopologyProblemKind.Misconfigured, resource, $"expected filter policy scope MessageAttributes, found {scope}"));
            }

            string? expected = AwsFilterPolicy.Translate(topology.Filters.GetValueOrDefault(topic));
            string? actual = attributes.GetValueOrDefault(FilterPolicy);
            if (!AwsFilterPolicy.Equivalent(expected, actual))
            {
                problems.Add(new TopologyProblem(TopologyProblemKind.FilterDrift, resource,
                    $"expected filter policy {expected ?? "(none)"}, found {(string.IsNullOrWhiteSpace(actual) ? "(none)" : actual)} (filter '{topology.Filters.GetValueOrDefault(topic) ?? "(none)"}')"));
            }

            if (queuePolicy is null || !queuePolicy.Contains(found.Arn, StringComparison.Ordinal))
            {
                problems.Add(new TopologyProblem(TopologyProblemKind.Misconfigured, resource, $"the policy of queue '{queue.Name}' does not allow the topic '{found.Name}' to send"));
            }
        }

        return problems;
    }

    private async Task<QueueRef?> ValidateEndpointAsync(TransportTopology topology, List<TopologyProblem> problems, CancellationToken ct)
    {
        QueueRef? queue = await Resources.FindQueueAsync(topology.Endpoint, ct, fresh: true).ConfigureAwait(false);
        if (queue is null)
        {
            problems.Add(new TopologyProblem(TopologyProblemKind.MissingEndpoint, topology.Endpoint, $"the SQS queue '{AwsNaming.Queue(Prefix, topology.Endpoint, topology.OrderedEndpoint)}' does not exist"));
            return null;
        }

        if (queue.Ordered != topology.OrderedEndpoint)
        {
            problems.Add(new TopologyProblem(TopologyProblemKind.OrderingMismatch, topology.Endpoint, $"expected ordered={topology.OrderedEndpoint}, found the {Kind(queue.Ordered)} queue '{queue.Name}'"));
        }

        string? redrive = await QueueAttributeAsync(queue, topology.Endpoint, "RedrivePolicy", ct).ConfigureAwait(false);
        (string? target, int? maxReceiveCount) = redrive is null ? (null, null) : AwsResources.ParseRedrive(redrive);
        if (target is null)
        {
            problems.Add(new TopologyProblem(TopologyProblemKind.MissingDeadLetter, topology.Endpoint, $"the queue '{queue.Name}' has no redrive policy: no dead-letter queue is attached"));
        }
        else if (await Resources.QueueUrlAsync(AwsNaming.NameOfArn(target), topology.Endpoint, ct).ConfigureAwait(false) is null)
        {
            problems.Add(new TopologyProblem(TopologyProblemKind.MissingDeadLetter, topology.Endpoint, $"the dead-letter queue '{AwsNaming.NameOfArn(target)}' does not exist"));
        }
        else if (maxReceiveCount != topology.MaxDeliveries)
        {
            problems.Add(new TopologyProblem(TopologyProblemKind.MissingDeadLetter, topology.Endpoint, $"expected max deliveries {topology.MaxDeliveries}, found maxReceiveCount {maxReceiveCount?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}"));
        }

        return queue;
    }

    private Task<string?> QueueAttributeAsync(QueueRef queue, string destination, string name, CancellationToken ct)
    {
        return AwsErrors.CallAsync(destination, async () =>
        {
            GetQueueAttributesResponse response = await _transport.Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queue.Url, AttributeNames = [name] }, ct).ConfigureAwait(false);
            return response.Attributes?.GetValueOrDefault(name) is { Length: > 0 } value ? value : null;
        });
    }

    private Task<string?> FindSubscriptionAsync(string topicArn, string queueArn, string topic, CancellationToken ct)
    {
        return AwsErrors.CallAsync(topic, async () =>
        {
            string? next = null;
            do
            {
                ListSubscriptionsByTopicResponse page = await _transport.Sns.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topicArn, NextToken = next }, ct).ConfigureAwait(false);
                foreach (Subscription subscription in page.Subscriptions ?? [])
                {
                    if (subscription.Endpoint == queueArn && subscription.Protocol == "sqs" && subscription.SubscriptionArn is { } arn && arn.StartsWith("arn:", StringComparison.Ordinal))
                    {
                        return arn;
                    }
                }

                next = page.NextToken;
            }
            while (!string.IsNullOrEmpty(next));

            return (string?)null;
        });
    }

    private static string Kind(bool ordered)
    {
        return ordered ? "FIFO" : "standard";
    }
}

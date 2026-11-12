using System.Globalization;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using SourceFlow.Messaging;
using SourceFlow.Transport;
using SnsAttribute = Amazon.SimpleNotificationService.Model.MessageAttributeValue;
using SqsAttribute = Amazon.SQS.Model.MessageAttributeValue;

namespace SourceFlow.Cloud.Aws;

/// <summary>
/// The AWS transport (doc 07 §4): an endpoint is an SQS queue (FIFO when ordered) with a redrive dead-letter queue, a
/// topic is an SNS topic, and a subscription is an SNS→SQS subscription with raw message delivery and, where the filter
/// allows, an SNS filter policy. Each service has one endpoint and one receiver for commands and events alike (GAP-02).
/// </summary>
public sealed class AwsTransport : ITransport, ITopologyDescriber, IDisposable
{
    /// <summary>The <c>messaging.system</c> of SQS (OpenTelemetry semantic conventions).</summary>
    public const string System = "aws_sqs";

    private readonly bool _ownsClients;
    private readonly AwsProvisioner _provisioner;

    /// <summary>Creates the transport with clients built from <paramref name="options"/> (the SDK credential chain).</summary>
    public AwsTransport(AwsTransportOptions options)
        : this(Validated(options), CreateSqs(options), CreateSns(options), ownsClients: true)
    {
    }

    /// <summary>
    /// Creates the transport over existing clients (for example from <c>AWSOptions.CreateServiceClient</c>); the caller
    /// owns and disposes them. Only the naming, receive and retention options apply.
    /// </summary>
    public AwsTransport(IAmazonSQS sqs, IAmazonSimpleNotificationService sns, AwsTransportOptions options)
        : this(Validated(options), sqs, sns, ownsClients: false)
    {
    }

    private AwsTransport(AwsTransportOptions options, IAmazonSQS sqs, IAmazonSimpleNotificationService sns, bool ownsClients)
    {
        ArgumentNullException.ThrowIfNull(sqs);
        ArgumentNullException.ThrowIfNull(sns);
        Options = options;
        Sqs = sqs;
        Sns = sns;
        _ownsClients = ownsClients;
        Resources = new AwsResources(sqs, sns, options.NamePrefix);
        _provisioner = new AwsProvisioner(this);
    }

    /// <summary>
    /// SQS and SNS limits: 180 KiB bodies (any bytes; 256 KiB on the wire), 10 attributes, 14 days retention, native delay
    /// and lock extension up to 12 hours, FIFO ordering, redrive dead-letter queues, filter policies, batches of 10.
    /// Broker deduplication is not declared: standard queues do not deduplicate, and the inbox does (FIFO queues also
    /// drop a re-sent message id for 5 minutes).
    /// </summary>
    public static TransportCapabilities AwsCapabilities { get; } = new(AwsWire.MaxBodyBytes, AwsWire.MaxAttributes, TimeSpan.FromDays(14), NativeDelay: true, TimeSpan.FromHours(12))
    {
        OrderedDelivery = true,
        NativeDeadLetter = true,
        SubscriptionFilters = true,
        MaxBatchSize = 10,
        MaxLockDuration = TimeSpan.FromHours(12),
    };

    /// <summary>The options.</summary>
    public AwsTransportOptions Options { get; }

    /// <summary>The SQS client.</summary>
    public IAmazonSQS Sqs { get; }

    /// <summary>The SNS client.</summary>
    public IAmazonSimpleNotificationService Sns { get; }

    /// <inheritdoc />
    public string SystemName => System;

    /// <inheritdoc />
    public TransportCapabilities Capabilities => AwsCapabilities;

    internal AwsResources Resources { get; }

    /// <inheritdoc />
    public async ValueTask SendAsync(string endpoint, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        ArgumentNullException.ThrowIfNull(messages);
        (List<AwsOutgoingMessage> encoded, List<SendFailure> failures) = Prepare(messages, topic: null);
        QueueRef queue = await Resources.QueueAsync(endpoint, cancellationToken).ConfigureAwait(false);
        await DeliverAsync(endpoint, encoded, failures, async batch =>
        {
            var request = new SendMessageBatchRequest
            {
                QueueUrl = queue.Url,
                Entries = [.. batch.Select((m, i) => new SendMessageBatchRequestEntry
                {
                    Id = i.ToString(CultureInfo.InvariantCulture),
                    MessageBody = m.Body,
                    MessageAttributes = SqsAttributes(m.Attributes),
                    MessageGroupId = queue.Ordered ? m.GroupId : null,
                    MessageDeduplicationId = queue.Ordered ? m.DeduplicationId : null,
                })],
            };
            SendMessageBatchResponse response = await Sqs.SendMessageBatchAsync(request, cancellationToken).ConfigureAwait(false);
            return (response.Successful?.Count ?? 0, (response.Failed ?? []).Select(f => (f.Id, f.Code, f.Message, f.SenderFault ?? true)));
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask PublishAsync(string topic, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);
        ArgumentNullException.ThrowIfNull(messages);
        (List<AwsOutgoingMessage> encoded, List<SendFailure> failures) = Prepare(messages, topic);
        TopicRef target = await Resources.TopicAsync(topic, cancellationToken).ConfigureAwait(false);
        await DeliverAsync(topic, encoded, failures, async batch =>
        {
            var request = new PublishBatchRequest
            {
                TopicArn = target.Arn,
                PublishBatchRequestEntries = [.. batch.Select((m, i) => new PublishBatchRequestEntry
                {
                    Id = i.ToString(CultureInfo.InvariantCulture),
                    Message = m.Body,
                    MessageAttributes = SnsAttributes(m.Attributes),
                    MessageGroupId = target.Ordered ? m.GroupId : null,
                    MessageDeduplicationId = target.Ordered ? m.DeduplicationId : null,
                })],
            };
            PublishBatchResponse response = await Sns.PublishBatchAsync(request, cancellationToken).ConfigureAwait(false);
            return (response.Successful?.Count ?? 0, (response.Failed ?? []).Select(f => (f.Id, f.Code, f.Message, f.SenderFault ?? true)));
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ITransportReceiver> CreateReceiverAsync(ReceiverOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.LockDuration <= TimeSpan.Zero || options.LockDuration > Capabilities.MaxLockDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.LockDuration, "The lock duration must be between 0 and 12 hours (the SQS visibility timeout limit).");
        }

        QueueRef queue = await Resources.QueueAsync(options.Endpoint, cancellationToken).ConfigureAwait(false);

        // Filters SNS cannot express are evaluated here, per topic (published events carry sf_topic).
        var clientFilters = new Dictionary<string, CloudEventFilter>(StringComparer.Ordinal);
        foreach ((string topic, string expression) in options.Filters)
        {
            CloudEventFilter filter = CloudEventFilter.Parse(expression);
            if (AwsFilterPolicy.Translate(filter) is null)
            {
                clientFilters[topic] = filter;
            }
        }

        return new AwsReceiver(this, options.Endpoint, queue, deadLetters: false, options.LockDuration, clientFilters);
    }

    /// <inheritdoc />
    public async ValueTask<ITransportReceiver> CreateDeadLetterReceiverAsync(string endpoint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        QueueRef queue = await Resources.QueueAsync(endpoint, cancellationToken).ConfigureAwait(false);
        string url = await Resources.DeadLetterQueueUrlAsync(queue, endpoint, cancellationToken).ConfigureAwait(false)
            ?? throw new DestinationNotFoundException(endpoint + " (dead-letter queue)");
        var deadLetters = new QueueRef(url, AwsNaming.DeadLetterQueue(Resources.Prefix, endpoint, queue.Ordered), queue.Ordered);
        return new AwsReceiver(this, endpoint, deadLetters, deadLetters: true, TimeSpan.FromSeconds(30), new Dictionary<string, CloudEventFilter>());
    }

    /// <inheritdoc />
    public PhysicalTopology Describe(TransportTopology topology)
    {
        return AwsTopology.Describe(Options, topology);
    }

    /// <inheritdoc />
    public ValueTask ProvisionAsync(TransportTopology topology, ProvisioningMode mode, CancellationToken cancellationToken)
    {
        return _provisioner.ProvisionAsync(topology, mode, cancellationToken);
    }

    /// <summary>Checks that the endpoint's queue is reachable (the health check).</summary>
    public async Task<bool> IsReachableAsync(string endpoint, CancellationToken cancellationToken)
    {
        return await Resources.FindQueueAsync(endpoint, cancellationToken, fresh: true).ConfigureAwait(false) is not null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsClients)
        {
            Sqs.Dispose();
            Sns.Dispose();
        }
    }

    internal static Dictionary<string, SqsAttribute> SqsAttributes(IReadOnlyDictionary<string, string> attributes)
    {
        return attributes.ToDictionary(a => a.Key, a => new SqsAttribute { DataType = "String", StringValue = a.Value }, StringComparer.Ordinal);
    }

    private static Dictionary<string, SnsAttribute> SnsAttributes(IReadOnlyDictionary<string, string> attributes)
    {
        return attributes.ToDictionary(a => a.Key, a => new SnsAttribute { DataType = "String", StringValue = a.Value }, StringComparer.Ordinal);
    }

    // Refuses oversized messages before anything is sent for them (TCK-06, TCK-08).
    private static (List<AwsOutgoingMessage> Encoded, List<SendFailure> Failures) Prepare(IReadOnlyList<MessageEnvelope> messages, string? topic)
    {
        var encoded = new List<AwsOutgoingMessage>(messages.Count);
        var failures = new List<SendFailure>();
        foreach (MessageEnvelope message in messages)
        {
            try
            {
                EnvelopeLimits.ValidateBody(message, AwsWire.MaxBodyBytes);
                AwsOutgoingMessage wire = AwsWire.Encode(message, topic);
                if (wire.Size > AwsWire.MaxMessageBytes)
                {
                    throw new MessageTooLargeException(message.CloudEventType, wire.Size, AwsWire.MaxMessageBytes);
                }

                encoded.Add(wire);
            }
            catch (MessageTooLargeException e)
            {
                failures.Add(new SendFailure(message.MessageId, e));
            }
        }

        return (encoded, failures);
    }

    // Batches of at most 10 messages and 256 KiB, in order; per-entry failures and failed calls become SendFailures.
    private async Task DeliverAsync(string destination, List<AwsOutgoingMessage> encoded, List<SendFailure> failures,
        Func<IReadOnlyList<AwsOutgoingMessage>, Task<(int Sent, IEnumerable<(string Id, string Code, string Message, bool SenderFault)> Failed)>> send)
    {
        int sent = 0;
        List<List<AwsOutgoingMessage>> batches = Batches(encoded);
        for (int b = 0; b < batches.Count; b++)
        {
            List<AwsOutgoingMessage> batch = batches[b];
            try
            {
                (int ok, IEnumerable<(string Id, string Code, string Message, bool SenderFault)> failed) = await AwsErrors.CallAsync(destination, () => send(batch)).ConfigureAwait(false);
                sent += ok;
                foreach ((string id, string code, string message, bool senderFault) in failed)
                {
                    MessageEnvelope envelope = batch[int.Parse(id, CultureInfo.InvariantCulture)].Envelope;
                    failures.Add(new SendFailure(envelope.MessageId, new TransportException($"{code}: {message}", transient: !senderFault)));
                }
            }
            catch (TransportException e)
            {
                if (e is DestinationNotFoundException)
                {
                    Resources.Forget(destination);
                }

                if (sent == 0 && failures.Count == 0)
                {
                    throw;   // nothing was sent: the call's own error
                }

                failures.AddRange(batches.Skip(b).SelectMany(rest => rest).Select(m => new SendFailure(m.Envelope.MessageId, e)));
                break;
            }
        }

        if (failures.Count > 0)
        {
            throw new TransportSendException(destination, failures, sent);
        }
    }

    private static List<List<AwsOutgoingMessage>> Batches(List<AwsOutgoingMessage> messages)
    {
        var batches = new List<List<AwsOutgoingMessage>>();
        var current = new List<AwsOutgoingMessage>();
        int size = 0;
        foreach (AwsOutgoingMessage message in messages)
        {
            if (current.Count == AwsCapabilities.MaxBatchSize || (current.Count > 0 && size + message.Size > AwsWire.MaxMessageBytes))
            {
                batches.Add(current);
                current = [];
                size = 0;
            }

            current.Add(message);
            size += message.Size;
        }

        if (current.Count > 0)
        {
            batches.Add(current);
        }

        return batches;
    }

    private static AwsTransportOptions Validated(AwsTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return options;
    }

    private static AmazonSQSClient CreateSqs(AwsTransportOptions options)
    {
        Amazon.Runtime.AWSCredentials? credentials = options.ResolveCredentials();
        return credentials is null ? new AmazonSQSClient(options.ToSqsConfig()) : new AmazonSQSClient(credentials, options.ToSqsConfig());
    }

    private static AmazonSimpleNotificationServiceClient CreateSns(AwsTransportOptions options)
    {
        Amazon.Runtime.AWSCredentials? credentials = options.ResolveCredentials();
        return credentials is null
            ? new AmazonSimpleNotificationServiceClient(options.ToSnsConfig())
            : new AmazonSimpleNotificationServiceClient(credentials, options.ToSnsConfig());
    }
}

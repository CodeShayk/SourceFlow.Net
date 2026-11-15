using System.Globalization;
using System.Text;
using Encoding = System.Text.Encoding;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Grpc.Core;
using NSubstitute;
using SourceFlow.Cloud.Gcp;
using SourceFlow.Messaging;
using PubSubReceivedMessage = Google.Cloud.PubSub.V1.ReceivedMessage;

namespace SourceFlow.Cloud.Tests.Gcp;

/// <summary>Substitute Pub/Sub clients for the transport's unit tests (no broker).</summary>
internal sealed class GcpMocks
{
    public const string Project = "test-project";

    private readonly Dictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<PubSubReceivedMessage[]>> _deliveries = new(StringComparer.Ordinal);
    private int _ids;

    public GcpMocks(GcpTransportOptions? options = null)
    {
        Publisher.PublishAsync(Arg.Any<PublishRequest>(), Arg.Any<CallSettings>()).Returns(call =>
        {
            PublishRequest request = call.Arg<PublishRequest>();
            lock (Published)
            {
                Published.Add(request);
            }

            var response = new PublishResponse();
            response.MessageIds.AddRange(request.Messages.Select(_ => Interlocked.Increment(ref _ids).ToString(CultureInfo.InvariantCulture)));
            return Task.FromResult(response);
        });
        Subscriber.GetSubscriptionAsync(Arg.Any<GetSubscriptionRequest>(), Arg.Any<CallSettings>()).Returns(call =>
        {
            string id = call.Arg<GetSubscriptionRequest>().SubscriptionAsSubscriptionName.SubscriptionId;
            return _subscriptions.TryGetValue(id, out Subscription? subscription)
                ? Task.FromResult(subscription)
                : Task.FromException<Subscription>(new RpcException(new Status(StatusCode.NotFound, $"Subscription {id} not found")));
        });
        Subscriber.PullAsync(Arg.Any<PullRequest>(), Arg.Any<CallSettings>()).Returns(call =>
        {
            PullRequest request = call.Arg<PullRequest>();
            lock (Pulls)
            {
                Pulls.Add(request);
            }

            var response = new PullResponse();
            if (_deliveries.TryGetValue(request.SubscriptionAsSubscriptionName.SubscriptionId, out Queue<PubSubReceivedMessage[]>? queue) && queue.TryDequeue(out PubSubReceivedMessage[]? messages))
            {
                response.ReceivedMessages.AddRange(messages.Take(request.MaxMessages));
            }

            return Task.FromResult(response);
        });
        Subscriber.AcknowledgeAsync(Arg.Any<AcknowledgeRequest>(), Arg.Any<CallSettings>()).Returns(call =>
        {
            Acknowledged.AddRange(call.Arg<AcknowledgeRequest>().AckIds);
            return Task.CompletedTask;
        });
        Subscriber.ModifyAckDeadlineAsync(Arg.Any<ModifyAckDeadlineRequest>(), Arg.Any<CallSettings>()).Returns(call =>
        {
            ModifyAckDeadlineRequest request = call.Arg<ModifyAckDeadlineRequest>();
            Deadlines.AddRange(request.AckIds.Select(id => (request.SubscriptionAsSubscriptionName.SubscriptionId, id, request.AckDeadlineSeconds)));
            return Task.CompletedTask;
        });
        Transport = new GcpTransport(Publisher, Subscriber, options ?? new GcpTransportOptions { ProjectId = Project, PollInterval = TimeSpan.FromMilliseconds(10) });
    }

    public PublisherServiceApiClient Publisher { get; } = Substitute.For<PublisherServiceApiClient>();

    public SubscriberServiceApiClient Subscriber { get; } = Substitute.For<SubscriberServiceApiClient>();

    public GcpTransport Transport { get; }

    public List<PublishRequest> Published { get; } = [];

    public List<PullRequest> Pulls { get; } = [];

    public List<string> Acknowledged { get; } = [];

    public List<(string Subscription, string AckId, int Seconds)> Deadlines { get; } = [];

    public static MessageEnvelope Envelope(string? key = null, string body = "{\"n\":1}", string type = "orders.placed")
    {
        return new MessageEnvelope
        {
            MessageId = new MessageId(Guid.CreateVersion7()),
            Type = type,
            TypeVersion = 1,
            Source = "orders",
            Time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            PartitionKey = key,
            Body = Encoding.UTF8.GetBytes(body),
        };
    }

    /// <summary>A received Pub/Sub message carrying the envelope, as a pull returns it.</summary>
    public static PubSubReceivedMessage Message(MessageEnvelope envelope, int deliveryAttempt = 1, string? reason = null)
    {
        return new PubSubReceivedMessage { AckId = "ack-" + envelope.MessageId, DeliveryAttempt = deliveryAttempt, Message = GcpWire.Encode(envelope, reason).Message };
    }

    /// <summary>Declares a subscription as the provisioner creates it.</summary>
    public Subscription AddSubscription(string id, string topicId, bool ordered = false, int ackDeadline = 30, int? maxDeliveries = 10, string filter = "", string? deadLetterTopic = "orders+dlq")
    {
        var subscription = new Subscription
        {
            SubscriptionName = new SubscriptionName(Project, id),
            TopicAsTopicName = new TopicName(Project, topicId),
            EnableMessageOrdering = ordered,
            AckDeadlineSeconds = ackDeadline,
            Filter = filter,
        };
        if (deadLetterTopic is not null)
        {
            subscription.DeadLetterPolicy = new DeadLetterPolicy { DeadLetterTopic = new TopicName(Project, deadLetterTopic).ToString(), MaxDeliveryAttempts = Math.Max(5, maxDeliveries ?? 5) };
        }

        if (maxDeliveries is { } max)
        {
            subscription.Labels[GcpTransport.MaxDeliveriesLabel] = max.ToString(CultureInfo.InvariantCulture);
        }

        _subscriptions[id] = subscription;
        return subscription;
    }

    /// <summary>The standard endpoint <c>orders</c>: its command subscription, optionally ordered.</summary>
    public GcpMocks WithEndpoint(string endpoint = "orders", bool ordered = false, int ackDeadline = 30, int maxDeliveries = 10)
    {
        AddSubscription(GcpNaming.EndpointSubscription("", endpoint), GcpNaming.EndpointTopic("", endpoint), ordered, ackDeadline, maxDeliveries, deadLetterTopic: GcpNaming.DeadLetter("", endpoint));
        return this;
    }

    /// <summary>Makes the next pull of the subscription return these messages.</summary>
    public void Deliver(string subscriptionId, params PubSubReceivedMessage[] messages)
    {
        if (!_deliveries.TryGetValue(subscriptionId, out Queue<PubSubReceivedMessage[]>? queue))
        {
            _deliveries[subscriptionId] = queue = new Queue<PubSubReceivedMessage[]>();
        }

        queue.Enqueue(messages);
    }
}

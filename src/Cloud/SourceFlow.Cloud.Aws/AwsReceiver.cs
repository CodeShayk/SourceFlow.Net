using System.Collections.Concurrent;
using System.Globalization;
using Amazon.SQS.Model;
using SourceFlow.Messaging;
using SourceFlow.Transport;

namespace SourceFlow.Cloud.Aws;

/// <summary>
/// Receives from one SQS queue (an endpoint or its dead-letter queue) by long polling. A received message is locked
/// for the receiver's lock duration (the visibility timeout); settling deletes it, abandoning changes its visibility
/// (native delay), renewing extends it. Disposing makes what the receiver holds unsettled visible again at once.
/// </summary>
internal sealed class AwsReceiver : ITransportReceiver
{
    private const string ReceiveCount = "ApproximateReceiveCount";
    private const int MaxVisibilitySeconds = 12 * 60 * 60;

    private readonly AwsTransport _transport;
    private readonly string _endpoint;
    private readonly IReadOnlyDictionary<string, CloudEventFilter> _clientFilters;
    private readonly ConcurrentDictionary<string, byte> _unsettled = new(StringComparer.Ordinal);

    public AwsReceiver(AwsTransport transport, string endpoint, QueueRef queue, bool deadLetters, TimeSpan lockDuration, IReadOnlyDictionary<string, CloudEventFilter> clientFilters)
    {
        _transport = transport;
        _endpoint = endpoint;
        Queue = queue;
        DeadLetters = deadLetters;
        LockSeconds = Seconds(lockDuration);
        _clientFilters = clientFilters;
    }

    public QueueRef Queue { get; }

    public bool DeadLetters { get; }

    public int LockSeconds { get; }

    private string Destination => DeadLetters ? _endpoint + " (dead-letter queue)" : _endpoint;

    public async ValueTask<IReadOnlyList<ReceivedMessage>> ReceiveAsync(int maxMessages, TimeSpan wait, CancellationToken cancellationToken)
    {
        // Sub-second waits are short polls; longer ones long-poll in whole seconds, capped by the options (20 s at most).
        int waitSeconds = wait < TimeSpan.FromSeconds(1) ? 0 : (int)Math.Min(Math.Ceiling(wait.TotalSeconds), _transport.Options.ReceiveWaitTime.TotalSeconds);
        var request = new ReceiveMessageRequest
        {
            QueueUrl = Queue.Url,
            MaxNumberOfMessages = Math.Clamp(maxMessages, 1, 10),
            WaitTimeSeconds = waitSeconds,
            VisibilityTimeout = LockSeconds,
            MessageAttributeNames = ["All"],
            MessageSystemAttributeNames = [ReceiveCount],
        };
        ReceiveMessageResponse response = await AwsErrors.CallAsync(Destination, () => _transport.Sqs.ReceiveMessageAsync(request, cancellationToken)).ConfigureAwait(false);

        // A failure partway (hiding a dead letter, deleting a filtered event) releases what this receive took and has not
        // settled before it is rethrown, so those messages are redelivered at once, and none stays unsettled (CR-42).
        List<string> taken = [.. (response.Messages ?? []).Select(m => m.ReceiptHandle)];
        try
        {
            return await AcceptAsync(response.Messages ?? [], taken).ConfigureAwait(false);
        }
        catch (Exception) when (taken.Count > 0)
        {
            foreach (string handle in taken)
            {
                _unsettled.TryRemove(handle, out _);
            }

            await ReleaseQuietlyAsync(taken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<IReadOnlyList<ReceivedMessage>> AcceptAsync(List<Message> messages, List<string> taken)
    {
        var received = new List<ReceivedMessage>();
        foreach (Message message in messages)
        {
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach ((string name, MessageAttributeValue value) in message.MessageAttributes ?? [])
            {
                if (value.StringValue is not null)
                {
                    attributes[name] = value.StringValue;
                }
            }

            MessageEnvelope? envelope;
            try
            {
                envelope = AwsWire.Decode(attributes, message.Body ?? string.Empty);
            }
            catch (FormatException)
            {
                envelope = TranslateV2(attributes, message);
            }

            if (envelope is null)
            {
                // Not a SourceFlow CloudEvent. On an endpoint it reappears after the lock until the redrive policy moves
                // it to the dead-letter queue; there, it is hidden for the longest visibility so the ingestor moves on.
                if (DeadLetters)
                {
                    await SetVisibilityAsync(message.ReceiptHandle, MaxVisibilitySeconds, CancellationToken.None).ConfigureAwait(false);
                }

                taken.Remove(message.ReceiptHandle);   // left to its lock (endpoint) or hidden (dead-letter queue)
                continue;
            }

            if (attributes.TryGetValue(AwsWire.TopicAttribute, out string? topic) && _clientFilters.TryGetValue(topic, out CloudEventFilter? filter) && !filter.Matches(envelope))
            {
                await DeleteAsync(message.ReceiptHandle, CancellationToken.None).ConfigureAwait(false);   // filtered out: acknowledged unseen
                taken.Remove(message.ReceiptHandle);
                continue;
            }

            int deliveries = message.Attributes?.GetValueOrDefault(ReceiveCount) is { } count && int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ? parsed : 1;
            string? reason = DeadLetters ? attributes.GetValueOrDefault(AwsWire.ReasonAttribute) ?? "MaxReceiveCountExceeded" : null;
            _unsettled[message.ReceiptHandle] = 0;
            received.Add(new AwsDelivery(this, message.ReceiptHandle, envelope, deliveries, reason));
        }

        return received;
    }

    public async ValueTask DisposeAsync()
    {
        // Release what this receiver still holds, so a graceful stop redelivers at once instead of after the lock.
        string[] held = [.. _unsettled.Keys];
        _unsettled.Clear();
        await ReleaseQuietlyAsync(held).ConfigureAwait(false);
    }

    // Makes messages visible again at once; best effort, since their locks expire on their own.
    private async Task ReleaseQuietlyAsync(IReadOnlyList<string> handles)
    {
        foreach (string[] chunk in handles.Chunk(10))
        {
            try
            {
                await _transport.Sqs.ChangeMessageVisibilityBatchAsync(new ChangeMessageVisibilityBatchRequest
                {
                    QueueUrl = Queue.Url,
                    Entries = [.. chunk.Select((handle, i) => new ChangeMessageVisibilityBatchRequestEntry { Id = i.ToString(CultureInfo.InvariantCulture), ReceiptHandle = handle, VisibilityTimeout = 0 })],
                }).ConfigureAwait(false);
            }
            catch (Exception e) when (AwsErrors.Map(e, Destination) is not null)
            {
                // Best effort: the lock expires on its own.
            }
        }
    }

    // The v2 wire bridge (doc 07 §8), off unless AcceptV2Messages was called: a v2 message on an endpoint becomes a v3
    // envelope (mapped) or an envelope the pipeline parks (unmapped). A mapped message whose body isn't JSON is left for
    // the redrive policy, like any message that can't be decoded.
    private MessageEnvelope? TranslateV2(Dictionary<string, string> attributes, Message message)
    {
        if (DeadLetters || _transport.Options.V2Messages is not { } map || string.IsNullOrEmpty(message.MessageId)
            || !AwsV2Bridge.TryRead(attributes, message.Body ?? string.Empty, message.MessageId, out Migration.V2Message? v2))
        {
            return null;
        }

        try
        {
            return map.Translate(v2, TimeProvider.System);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    internal static int Seconds(TimeSpan duration)
    {
        return (int)Math.Clamp(Math.Ceiling(duration.TotalSeconds), 0, MaxVisibilitySeconds);
    }

    internal void Settled(string receiptHandle)
    {
        _unsettled.TryRemove(receiptHandle, out _);
    }

    internal async Task DeleteAsync(string receiptHandle, CancellationToken cancellationToken)
    {
        try
        {
            await AwsErrors.CallAsync(Destination, () => _transport.Sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = Queue.Url, ReceiptHandle = receiptHandle }, cancellationToken)).ConfigureAwait(false);
        }
        catch (TransportException e) when (e.InnerException is { } inner && AwsErrors.IsLockLost(inner))
        {
            // The lock expired and the message was redelivered (or already deleted): a late settlement is ignored.
        }
    }

    internal async Task<bool> SetVisibilityAsync(string receiptHandle, int seconds, CancellationToken cancellationToken)
    {
        try
        {
            await AwsErrors.CallAsync(Destination, () => _transport.Sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = Queue.Url, ReceiptHandle = receiptHandle, VisibilityTimeout = seconds }, cancellationToken)).ConfigureAwait(false);
            return true;
        }
        catch (TransportException e) when (e.InnerException is { } inner && AwsErrors.IsLockLost(inner))
        {
            return false;
        }
    }

    internal async Task DeadLetterAsync(string receiptHandle, MessageEnvelope envelope, string reason, CancellationToken cancellationToken)
    {
        if (DeadLetters)
        {
            throw new TransportException("A message on a dead-letter queue cannot be dead-lettered again.", transient: false);
        }

        string url = await _transport.Resources.DeadLetterQueueUrlAsync(Queue, _endpoint, cancellationToken).ConfigureAwait(false)
            ?? throw new TransportException($"The endpoint '{_endpoint}' has no dead-letter queue (no redrive policy).", transient: false);
        AwsOutgoingMessage wire = AwsWire.Encode(envelope, reason: reason);
        await AwsErrors.CallAsync(Destination, () => _transport.Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = wire.Body,
            MessageAttributes = AwsTransport.SqsAttributes(wire.Attributes),
            MessageGroupId = Queue.Ordered ? wire.GroupId : null,
            MessageDeduplicationId = Queue.Ordered ? wire.DeduplicationId : null,
        }, cancellationToken)).ConfigureAwait(false);
        await DeleteAsync(receiptHandle, cancellationToken).ConfigureAwait(false);
    }

    private sealed class AwsDelivery(AwsReceiver receiver, string receiptHandle, MessageEnvelope envelope, int deliveries, string? reason) : ReceivedMessage
    {
        public override MessageEnvelope Envelope => envelope;

        public override int DeliveryCount => deliveries;

        public override string? DeadLetterReason => reason;

        public override async ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            await receiver.DeleteAsync(receiptHandle, cancellationToken).ConfigureAwait(false);
            receiver.Settled(receiptHandle);
        }

        public override async ValueTask AbandonAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            await receiver.SetVisibilityAsync(receiptHandle, Seconds(delay < TimeSpan.Zero ? TimeSpan.Zero : delay), cancellationToken).ConfigureAwait(false);
            receiver.Settled(receiptHandle);
        }

        public override async ValueTask RenewAsync(CancellationToken cancellationToken)
        {
            if (!await receiver.SetVisibilityAsync(receiptHandle, receiver.LockSeconds, cancellationToken).ConfigureAwait(false))
            {
                throw new TransportException("The message lock was lost (it expired and the message was redelivered).", transient: false);
            }
        }

        public override async ValueTask DeadLetterAsync(string reason, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            await receiver.DeadLetterAsync(receiptHandle, envelope, reason, cancellationToken).ConfigureAwait(false);
            receiver.Settled(receiptHandle);
        }
    }
}

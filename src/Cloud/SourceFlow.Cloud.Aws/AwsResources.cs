using System.Collections.Concurrent;
using System.Text.Json;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace SourceFlow.Cloud.Aws;

/// <summary>A resolved queue.</summary>
/// <param name="Url">The queue URL.</param>
/// <param name="Name">The physical name.</param>
/// <param name="Ordered">True for a FIFO queue.</param>
internal sealed record QueueRef(string Url, string Name, bool Ordered);

/// <summary>A resolved topic.</summary>
/// <param name="Arn">The topic ARN.</param>
/// <param name="Name">The physical name.</param>
/// <param name="Ordered">True for a FIFO topic.</param>
internal sealed record TopicRef(string Arn, string Name, bool Ordered);

/// <summary>
/// Resolves logical endpoints and topics to SQS queues and SNS topics, caching what it found. An endpoint is the standard
/// queue <c>{base}</c> or the FIFO queue <c>{base}.fifo</c>, whichever exists, so senders need not know the owner's ordering.
/// </summary>
internal sealed class AwsResources
{
    private readonly IAmazonSQS _sqs;
    private readonly IAmazonSimpleNotificationService _sns;
    private readonly ConcurrentDictionary<string, QueueRef> _queues = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TopicRef> _topics = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string?> _deadLetterQueues = new(StringComparer.Ordinal);

    public AwsResources(IAmazonSQS sqs, IAmazonSimpleNotificationService sns, string prefix)
    {
        _sqs = sqs;
        _sns = sns;
        Prefix = prefix;
    }

    public string Prefix { get; }

    /// <summary>The endpoint's queue (either ordering), or null when it does not exist.</summary>
    public async Task<QueueRef?> FindQueueAsync(string endpoint, CancellationToken cancellationToken, bool fresh = false)
    {
        if (!fresh && _queues.TryGetValue(endpoint, out QueueRef? cached))
        {
            return cached;
        }

        foreach (bool ordered in new[] { false, true })
        {
            string name = AwsNaming.Queue(Prefix, endpoint, ordered);
            if (await QueueUrlAsync(name, endpoint, cancellationToken).ConfigureAwait(false) is { } url)
            {
                var queue = new QueueRef(url, name, ordered);
                _queues[endpoint] = queue;
                return queue;
            }
        }

        _queues.TryRemove(endpoint, out _);
        return null;
    }

    /// <summary>The endpoint's queue.</summary>
    /// <exception cref="Transport.DestinationNotFoundException">It does not exist.</exception>
    public async Task<QueueRef> QueueAsync(string endpoint, CancellationToken cancellationToken)
    {
        return await FindQueueAsync(endpoint, cancellationToken).ConfigureAwait(false) ?? throw new Transport.DestinationNotFoundException(endpoint);
    }

    /// <summary>The URL of a queue by physical name, or null.</summary>
    public Task<string?> QueueUrlAsync(string name, string destination, CancellationToken cancellationToken)
    {
        return AwsErrors.CallAsync(destination, async () =>
        {
            try
            {
                return (await _sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = name }, cancellationToken).ConfigureAwait(false)).QueueUrl;
            }
            catch (Exception e) when (AwsErrors.IsNotFound(e))
            {
                return (string?)null;
            }
        });
    }

    /// <summary>The topic (either ordering), or null when it does not exist.</summary>
    public async Task<TopicRef?> FindTopicAsync(string topic, CancellationToken cancellationToken, bool fresh = false)
    {
        if (!fresh && _topics.TryGetValue(topic, out TopicRef? cached))
        {
            return cached;
        }

        string standard = AwsNaming.Topic(Prefix, topic, ordered: false);
        string fifo = AwsNaming.Topic(Prefix, topic, ordered: true);
        TopicRef? found = null;
        await AwsErrors.CallAsync(topic, async () =>
        {
            // SNS has no lookup by name: list topics (describe permission only) and match the name part of the ARN.
            string? next = null;
            do
            {
                ListTopicsResponse page = await _sns.ListTopicsAsync(new ListTopicsRequest { NextToken = next }, cancellationToken).ConfigureAwait(false);
                foreach (Topic candidate in page.Topics ?? [])
                {
                    string name = AwsNaming.NameOfArn(candidate.TopicArn);
                    if (name == standard)
                    {
                        found ??= new TopicRef(candidate.TopicArn, name, false);
                    }
                    else if (name == fifo)
                    {
                        found = new TopicRef(candidate.TopicArn, name, true);
                    }
                }

                next = page.NextToken;
            }
            while (!string.IsNullOrEmpty(next));
        }).ConfigureAwait(false);

        if (found is null)
        {
            _topics.TryRemove(topic, out _);
            return null;
        }

        _topics[topic] = found;
        return found;
    }

    /// <summary>The topic.</summary>
    /// <exception cref="Transport.DestinationNotFoundException">It does not exist.</exception>
    public async Task<TopicRef> TopicAsync(string topic, CancellationToken cancellationToken)
    {
        return await FindTopicAsync(topic, cancellationToken).ConfigureAwait(false) ?? throw new Transport.DestinationNotFoundException(topic);
    }

    /// <summary>Remembers a resource just created.</summary>
    public void Remember(string endpoint, QueueRef queue)
    {
        _queues[endpoint] = queue;
    }

    /// <summary>Remembers a topic just created.</summary>
    public void Remember(string topic, TopicRef reference)
    {
        _topics[topic] = reference;
    }

    /// <summary>Forgets a resource that turned out to be gone.</summary>
    public void Forget(string name)
    {
        if (_queues.TryRemove(name, out QueueRef? queue))
        {
            _deadLetterQueues.TryRemove(queue.Url, out _);
        }

        _topics.TryRemove(name, out _);
    }

    /// <summary>The ARN of a queue.</summary>
    public Task<string> QueueArnAsync(string url, string destination, CancellationToken cancellationToken)
    {
        return AwsErrors.CallAsync(destination, async () =>
        {
            GetQueueAttributesResponse attributes = await _sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] }, cancellationToken).ConfigureAwait(false);
            return attributes.Attributes?.GetValueOrDefault("QueueArn") ?? throw new Transport.TransportException($"SQS returned no ARN for '{destination}'.", transient: true);
        });
    }

    /// <summary>The dead-letter queue the queue's redrive policy points at, or null without one.</summary>
    public async Task<string?> DeadLetterQueueUrlAsync(QueueRef queue, string destination, CancellationToken cancellationToken)
    {
        if (_deadLetterQueues.TryGetValue(queue.Url, out string? cached))
        {
            return cached;
        }

        string? url = await AwsErrors.CallAsync(destination, async () =>
        {
            GetQueueAttributesResponse attributes = await _sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queue.Url, AttributeNames = ["RedrivePolicy"] }, cancellationToken).ConfigureAwait(false);
            return attributes.Attributes?.GetValueOrDefault("RedrivePolicy") is { Length: > 0 } policy && ParseRedrive(policy) is { TargetArn: { } target }
                ? target
                : null;
        }).ConfigureAwait(false) is { } arn
            ? await QueueUrlAsync(AwsNaming.NameOfArn(arn), destination, cancellationToken).ConfigureAwait(false)
            : null;

        if (url is not null)
        {
            _deadLetterQueues[queue.Url] = url;
        }

        return url;
    }

    /// <summary>Parses an SQS redrive policy.</summary>
    public static (string? TargetArn, int? MaxReceiveCount) ParseRedrive(string policy)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(policy);
            JsonElement root = document.RootElement;
            string? target = root.TryGetProperty("deadLetterTargetArn", out JsonElement arn) ? arn.GetString() : null;
            int? max = root.TryGetProperty("maxReceiveCount", out JsonElement count)
                ? count.ValueKind == JsonValueKind.Number ? count.GetInt32() : int.TryParse(count.GetString(), out int parsed) ? parsed : null
                : null;
            return (target, max);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}

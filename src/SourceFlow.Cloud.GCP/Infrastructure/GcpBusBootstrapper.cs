using Google.Api.Gax.ResourceNames;
using Google.Cloud.PubSub.V1;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Configuration;
using SourceFlow.Cloud.GCP.Configuration;

namespace SourceFlow.Cloud.GCP.Infrastructure;

/// <summary>
/// Hosted service that runs once at application startup to ensure all configured Pub/Sub
/// topics and pull subscriptions exist, then resolves short names to full resource names
/// (<c>projects/{p}/topics/{name}</c>, <c>projects/{p}/subscriptions/{name}-sub</c>) and
/// injects them into <see cref="IBusBootstrapConfiguration"/> via <c>Resolve()</c>.
/// </summary>
/// <remarks>
/// Unlike AWS (SQS queues + SNS topics) and Azure (Service Bus queues + topics), Google Cloud
/// Pub/Sub has only topics and subscriptions. A command "queue" is modelled as a topic plus a
/// pull subscription; an event "topic" is a topic plus a pull subscription per subscriber.
/// Must be registered <b>before</b> the listeners so routing is resolved before any pull begins.
/// </remarks>
public sealed class GcpBusBootstrapper : IHostedService
{
    private readonly IBusBootstrapConfiguration _busConfiguration;
    private readonly PublisherServiceApiClient _publisher;
    private readonly SubscriberServiceApiClient _subscriber;
    private readonly GcpOptions _options;
    private readonly ILogger<GcpBusBootstrapper> _logger;

    public GcpBusBootstrapper(
        IBusBootstrapConfiguration busConfiguration,
        PublisherServiceApiClient publisher,
        SubscriberServiceApiClient subscriber,
        GcpOptions options,
        ILogger<GcpBusBootstrapper> logger)
    {
        _busConfiguration = busConfiguration;
        _publisher = publisher;
        _subscriber = subscriber;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ProjectId))
            throw new InvalidOperationException(
                "GcpOptions.ProjectId must be configured (e.g. options.ProjectId = \"my-project\").");

        _logger.LogInformation("GcpBusBootstrapper: resolving Pub/Sub topics and subscriptions for project '{Project}'.",
            _options.ProjectId);

        // ── 1. Command topics (publish targets + listening sources) ──────────
        var commandQueueNames = _busConfiguration.CommandTypeToQueueName.Values
            .Concat(_busConfiguration.CommandListeningQueueNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var commandTopicMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in commandQueueNames)
            commandTopicMap[name] = (await EnsureTopicAsync(name, cancellationToken)).ToString();

        // Pull subscriptions for the command queues this service listens to.
        var commandSubscriptionMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _busConfiguration.CommandListeningQueueNames.Distinct(StringComparer.OrdinalIgnoreCase))
            commandSubscriptionMap[name] = (await EnsureSubscriptionAsync(name, SubscriptionId(name), cancellationToken)).ToString();

        // ── 2. Event topics (publish targets + subscription sources) ─────────
        var eventTopicNames = _busConfiguration.EventTypeToTopicName.Values
            .Concat(_busConfiguration.SubscribedTopicNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var eventTopicMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in eventTopicNames)
            eventTopicMap[name] = (await EnsureTopicAsync(name, cancellationToken)).ToString();

        // Pull subscriptions for the event topics this service subscribes to.
        var eventSubscriptionMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _busConfiguration.SubscribedTopicNames.Distinct(StringComparer.OrdinalIgnoreCase))
            eventSubscriptionMap[name] = (await EnsureSubscriptionAsync(name, SubscriptionId(name), cancellationToken)).ToString();

        // ── 3. Build resolved maps ───────────────────────────────────────────
        var resolvedCommandRoutes = _busConfiguration.CommandTypeToQueueName
            .ToDictionary(kv => kv.Key, kv => commandTopicMap[kv.Value]);

        var resolvedEventRoutes = _busConfiguration.EventTypeToTopicName
            .ToDictionary(kv => kv.Key, kv => eventTopicMap[kv.Value]);

        var resolvedCommandListeningSubs = _busConfiguration.CommandListeningQueueNames
            .Select(name => commandSubscriptionMap[name])
            .ToList();

        var resolvedSubscribedTopics = _busConfiguration.SubscribedTopicNames
            .Select(name => eventTopicMap[name])
            .ToList();

        var resolvedEventListeningSubs = _busConfiguration.SubscribedTopicNames
            .Select(name => eventSubscriptionMap[name])
            .ToList();

        // ── 4. Inject resolved resource names ────────────────────────────────
        _busConfiguration.Resolve(
            resolvedCommandRoutes,
            resolvedEventRoutes,
            resolvedCommandListeningSubs,
            resolvedSubscribedTopics,
            resolvedEventListeningSubs);

        _logger.LogInformation(
            "GcpBusBootstrapper: resolved {CommandCount} command route(s), {EventCount} event route(s), " +
            "{ListenCount} command subscription(s), {SubscribeCount} event subscription(s).",
            resolvedCommandRoutes.Count, resolvedEventRoutes.Count,
            resolvedCommandListeningSubs.Count, resolvedEventListeningSubs.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string SubscriptionId(string queueOrTopicName) => $"{queueOrTopicName}{_options.SubscriptionSuffix}";

    private async Task<TopicName> EnsureTopicAsync(string topicId, CancellationToken ct)
    {
        var topicName = TopicName.FromProjectTopic(_options.ProjectId, topicId);
        try
        {
            await _publisher.CreateTopicAsync(topicName, ct);
            _logger.LogInformation("GcpBusBootstrapper: created topic '{Topic}'.", topicName);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
            _logger.LogDebug("GcpBusBootstrapper: topic '{Topic}' already exists.", topicName);
        }
        return topicName;
    }

    private async Task<SubscriptionName> EnsureSubscriptionAsync(string topicId, string subscriptionId, CancellationToken ct)
    {
        var topicName = TopicName.FromProjectTopic(_options.ProjectId, topicId);
        var subscriptionName = SubscriptionName.FromProjectSubscription(_options.ProjectId, subscriptionId);
        try
        {
            await _subscriber.CreateSubscriptionAsync(
                subscriptionName, topicName, pushConfig: null, ackDeadlineSeconds: _options.AckDeadlineSeconds, ct);
            _logger.LogInformation("GcpBusBootstrapper: created subscription '{Subscription}' on topic '{Topic}'.",
                subscriptionName, topicName);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
            _logger.LogDebug("GcpBusBootstrapper: subscription '{Subscription}' already exists.", subscriptionName);
        }
        return subscriptionName;
    }
}

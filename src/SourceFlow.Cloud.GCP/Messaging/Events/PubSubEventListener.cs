using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Configuration;
using SourceFlow.Cloud.GCP.Configuration;
using SourceFlow.Messaging.Events;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace SourceFlow.Cloud.GCP.Messaging.Events;

/// <summary>
/// Background service that pulls events from the resolved Pub/Sub event subscriptions,
/// deserializes them, and dispatches to all registered <see cref="IEventSubscriber"/> instances.
/// Unlike AWS (which unwraps an SNS-to-SQS envelope) Pub/Sub delivers the published message directly.
/// </summary>
public class PubSubEventListener : BackgroundService
{
    private static readonly ConcurrentDictionary<string, Type?> _typeCache = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo?> _methodInfoCache = new();

    private readonly SubscriberServiceApiClient _subscriber;
    private readonly IServiceProvider _serviceProvider;
    private readonly IEventRoutingConfiguration _routingConfig;
    private readonly ILogger<PubSubEventListener> _logger;
    private readonly GcpOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;

    public PubSubEventListener(
        SubscriberServiceApiClient subscriber,
        IServiceProvider serviceProvider,
        IEventRoutingConfiguration routingConfig,
        ILogger<PubSubEventListener> logger,
        GcpOptions options)
    {
        _subscriber = subscriber;
        _serviceProvider = serviceProvider;
        _routingConfig = routingConfig;
        _logger = logger;
        _options = options;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriptions = _routingConfig.GetListeningQueues().ToList();

        if (subscriptions.Count == 0)
        {
            _logger.LogWarning("No Pub/Sub subscriptions configured for event listening. GCP event listener will not start.");
            return;
        }

        var listeningTasks = subscriptions.Select(sub => ListenToSubscription(sub, stoppingToken));
        await Task.WhenAll(listeningTasks);
    }

    private async Task ListenToSubscription(string subscriptionResource, CancellationToken cancellationToken)
    {
        var subscriptionName = SubscriptionName.Parse(subscriptionResource);
        _logger.LogInformation("Starting to listen to Pub/Sub event subscription: {Subscription}", subscriptionName);
        int retryCount = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var response = await _subscriber.PullAsync(new PullRequest
                {
                    SubscriptionAsSubscriptionName = subscriptionName,
                    MaxMessages = _options.MaxMessagesPerPull
                }, cancellationToken);

                retryCount = 0;

                if (response.ReceivedMessages.Count == 0)
                {
                    await Task.Delay(_options.EmptyPullDelay, cancellationToken);
                    continue;
                }

                foreach (var received in response.ReceivedMessages)
                    await ProcessMessage(subscriptionName, received, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listening to Pub/Sub event subscription: {Subscription}, Retry: {RetryCount}",
                    subscriptionName, retryCount);

                var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, retryCount), 60));
                retryCount++;
                await Task.Delay(delay, cancellationToken);
            }
        }

        _logger.LogInformation("Stopped listening to Pub/Sub event subscription: {Subscription}", subscriptionName);
    }

    private async Task ProcessMessage(SubscriptionName subscriptionName, ReceivedMessage received, CancellationToken cancellationToken)
    {
        var message = received.Message;
        try
        {
            // 1. Resolve event type from attributes
            if (!message.Attributes.TryGetValue("EventType", out var eventTypeName) || string.IsNullOrEmpty(eventTypeName))
            {
                _logger.LogError("Pub/Sub message missing EventType attribute: {MessageId}", message.MessageId);
                await AcknowledgeAsync(subscriptionName, received.AckId, cancellationToken);
                return;
            }

            var eventType = _typeCache.GetOrAdd(eventTypeName, static name => Type.GetType(name));
            if (eventType == null)
            {
                _logger.LogError("Could not resolve event type: {EventType}", eventTypeName);
                await AcknowledgeAsync(subscriptionName, received.AckId, cancellationToken);
                return;
            }

            // 2. Deserialize event
            IEvent? @event;
            try
            {
                @event = JsonSerializer.Deserialize(message.Data.ToStringUtf8(), eventType, _jsonOptions) as IEvent;
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "Failed to deserialize event body for type {EventType}: {MessageId}", eventTypeName, message.MessageId);
                await AcknowledgeAsync(subscriptionName, received.AckId, cancellationToken);
                return;
            }

            if (@event == null)
            {
                _logger.LogError("Failed to deserialize event: {EventType}", eventTypeName);
                await AcknowledgeAsync(subscriptionName, received.AckId, cancellationToken);
                return;
            }

            // 3. Dispatch to all registered subscribers within a scope
            using var scope = _serviceProvider.CreateScope();
            var eventSubscribers = scope.ServiceProvider.GetServices<IEventSubscriber>();

            var subscribeMethod = _methodInfoCache.GetOrAdd(eventType, static t =>
                typeof(IEventSubscriber).GetMethod("Subscribe")?.MakeGenericMethod(t));

            if (subscribeMethod == null)
            {
                _logger.LogError("Could not find Subscribe method for event type: {EventType}", eventTypeName);
                return;
            }

            var tasks = eventSubscribers.Select(subscriber =>
            {
                try
                {
                    return (Task)subscribeMethod.Invoke(subscriber, new object[] { @event })!;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error invoking Subscribe method for event type: {EventType}", eventTypeName);
                    return Task.CompletedTask;
                }
            });

            await Task.WhenAll(tasks);

            // 4. Acknowledge
            await AcknowledgeAsync(subscriptionName, received.AckId, cancellationToken);

            _logger.LogInformation("Event processed from Pub/Sub: {EventType} (MessageId: {MessageId})",
                eventType.Name, message.MessageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Pub/Sub event message: {MessageId}", message.MessageId);
        }
    }

    private Task AcknowledgeAsync(SubscriptionName subscriptionName, string ackId, CancellationToken cancellationToken) =>
        _subscriber.AcknowledgeAsync(subscriptionName, new[] { ackId }, cancellationToken);
}

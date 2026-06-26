using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Configuration;
using SourceFlow.Cloud.GCP.Configuration;
using SourceFlow.Messaging.Commands;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace SourceFlow.Cloud.GCP.Messaging.Commands;

/// <summary>
/// Background service that pulls commands from the resolved Pub/Sub command subscriptions,
/// deserializes them, and dispatches to the local <see cref="ICommandSubscriber"/>.
/// </summary>
public class PubSubCommandListener : BackgroundService
{
    private static readonly ConcurrentDictionary<string, Type?> _typeCache = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo?> _methodInfoCache = new();

    private readonly SubscriberServiceApiClient _subscriber;
    private readonly IServiceProvider _serviceProvider;
    private readonly ICommandRoutingConfiguration _routingConfig;
    private readonly ILogger<PubSubCommandListener> _logger;
    private readonly GcpOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;

    public PubSubCommandListener(
        SubscriberServiceApiClient subscriber,
        IServiceProvider serviceProvider,
        ICommandRoutingConfiguration routingConfig,
        ILogger<PubSubCommandListener> logger,
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
            _logger.LogWarning("No Pub/Sub subscriptions configured for listening. GCP command listener will not start.");
            return;
        }

        var listeningTasks = subscriptions.Select(sub => ListenToSubscription(sub, stoppingToken));
        await Task.WhenAll(listeningTasks);
    }

    private async Task ListenToSubscription(string subscriptionResource, CancellationToken cancellationToken)
    {
        var subscriptionName = SubscriptionName.Parse(subscriptionResource);
        _logger.LogInformation("Starting to listen to Pub/Sub subscription: {Subscription}", subscriptionName);
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
                _logger.LogError(ex, "Error listening to Pub/Sub subscription: {Subscription}, Retry: {RetryCount}",
                    subscriptionName, retryCount);

                var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, retryCount), 60));
                retryCount++;
                await Task.Delay(delay, cancellationToken);
            }
        }

        _logger.LogInformation("Stopped listening to Pub/Sub subscription: {Subscription}", subscriptionName);
    }

    private async Task ProcessMessage(SubscriptionName subscriptionName, ReceivedMessage received, CancellationToken cancellationToken)
    {
        var message = received.Message;
        try
        {
            // 1. Resolve command type from attributes
            if (!message.Attributes.TryGetValue("CommandType", out var commandTypeName) || string.IsNullOrEmpty(commandTypeName))
            {
                _logger.LogError("Pub/Sub message missing CommandType attribute: {MessageId}", message.MessageId);
                await AcknowledgeAsync(subscriptionName, received.AckId, cancellationToken);
                return;
            }

            var commandType = _typeCache.GetOrAdd(commandTypeName, static name => Type.GetType(name));
            if (commandType == null)
            {
                _logger.LogError("Could not resolve command type: {CommandType}", commandTypeName);
                await AcknowledgeAsync(subscriptionName, received.AckId, cancellationToken);
                return;
            }

            // 2. Deserialize command
            ICommand? command;
            try
            {
                command = JsonSerializer.Deserialize(message.Data.ToStringUtf8(), commandType, _jsonOptions) as ICommand;
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "Failed to deserialize command body for type {CommandType}: {MessageId}", commandTypeName, message.MessageId);
                await AcknowledgeAsync(subscriptionName, received.AckId, cancellationToken);
                return;
            }

            if (command == null)
            {
                _logger.LogError("Failed to deserialize command: {CommandType}", commandTypeName);
                await AcknowledgeAsync(subscriptionName, received.AckId, cancellationToken);
                return;
            }

            // 3. Create a scope and dispatch to the local subscriber
            using var scope = _serviceProvider.CreateScope();
            var commandSubscriber = scope.ServiceProvider.GetRequiredService<ICommandSubscriber>();

            var subscribeMethod = _methodInfoCache.GetOrAdd(commandType, static t =>
                typeof(ICommandSubscriber).GetMethod("Subscribe")?.MakeGenericMethod(t));

            if (subscribeMethod == null)
            {
                _logger.LogError("Could not find Subscribe method for command type: {CommandType}", commandTypeName);
                return;
            }

            await (Task)subscribeMethod.Invoke(commandSubscriber, new object[] { command })!;

            // 4. Acknowledge successful processing
            await AcknowledgeAsync(subscriptionName, received.AckId, cancellationToken);

            _logger.LogInformation("Command processed from Pub/Sub: {CommandType} (MessageId: {MessageId})",
                commandType.Name, message.MessageId);
        }
        catch (Exception ex)
        {
            // Do not acknowledge — Pub/Sub redelivers after the ack deadline.
            _logger.LogError(ex, "Error processing Pub/Sub message: {MessageId}", message.MessageId);
        }
    }

    private Task AcknowledgeAsync(SubscriptionName subscriptionName, string ackId, CancellationToken cancellationToken) =>
        _subscriber.AcknowledgeAsync(subscriptionName, new[] { ackId }, cancellationToken);
}

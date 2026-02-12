using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Core.Configuration;

namespace SourceFlow.Cloud.Azure.Infrastructure;

/// <summary>
/// Hosted service that creates Azure Service Bus queues, topics, and subscriptions
/// at startup, then resolves short names into the <see cref="BusConfiguration"/>.
/// </summary>
public sealed class AzureBusBootstrapper : IHostedService
{
    private readonly IBusBootstrapConfiguration _busConfiguration;
    private readonly ServiceBusAdministrationClient _adminClient;
    private readonly ILogger<AzureBusBootstrapper> _logger;

    public AzureBusBootstrapper(
        IBusBootstrapConfiguration busConfiguration,
        ServiceBusAdministrationClient adminClient,
        ILogger<AzureBusBootstrapper> logger)
    {
        _busConfiguration = busConfiguration;
        _adminClient = adminClient;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("AzureBusBootstrapper starting...");

        // ── Step 0: Validate ──────────────────────────────────────────────
        if (_busConfiguration.SubscribedTopicNames.Count > 0 &&
            _busConfiguration.CommandListeningQueueNames.Count == 0)
        {
            throw new InvalidOperationException(
                "At least one command queue must be configured via .Listen.To.CommandQueue(...) " +
                "when subscribing to topics via .Subscribe.To.Topic(...). " +
                "Topic subscriptions require a queue to receive forwarded events.");
        }

        // ── Step 1: Collect all unique queue names ────────────────────────
        var allQueueNames = _busConfiguration.CommandListeningQueueNames
            .Concat(_busConfiguration.CommandTypeToQueueName.Values)
            .Distinct()
            .ToList();

        // ── Step 2: Create queues ─────────────────────────────────────────
        foreach (var queueName in allQueueNames)
        {
            await EnsureQueueExistsAsync(queueName, cancellationToken);
        }

        // ── Step 3: Collect all unique topic names ────────────────────────
        var allTopicNames = _busConfiguration.SubscribedTopicNames
            .Concat(_busConfiguration.EventTypeToTopicName.Values)
            .Distinct()
            .ToList();

        // ── Step 4: Create topics ─────────────────────────────────────────
        foreach (var topicName in allTopicNames)
        {
            await EnsureTopicExistsAsync(topicName, cancellationToken);
        }

        // ── Step 5: Subscribe topics to the first command queue ───────────
        var eventListeningQueues = new List<string>();

        if (_busConfiguration.SubscribedTopicNames.Count > 0)
        {
            var targetQueueName = _busConfiguration.CommandListeningQueueNames[0];

            foreach (var topicName in _busConfiguration.SubscribedTopicNames)
            {
                await EnsureSubscriptionExistsAsync(topicName, targetQueueName, cancellationToken);
            }

            eventListeningQueues.Add(targetQueueName);
        }

        // ── Step 6: Resolve ───────────────────────────────────────────────
        // Azure Service Bus uses names directly (no URL/ARN translation needed)
        var resolvedCommandRoutes = new Dictionary<Type, string>(
            _busConfiguration.CommandTypeToQueueName);

        var resolvedEventRoutes = new Dictionary<Type, string>(
            _busConfiguration.EventTypeToTopicName);

        var resolvedCommandListeningQueues = _busConfiguration.CommandListeningQueueNames.ToList();

        var resolvedSubscribedTopics = _busConfiguration.SubscribedTopicNames.ToList();

        _busConfiguration.Resolve(
            resolvedCommandRoutes,
            resolvedEventRoutes,
            resolvedCommandListeningQueues,
            resolvedSubscribedTopics,
            eventListeningQueues);

        _logger.LogInformation(
            "AzureBusBootstrapper completed: {Queues} queues, {Topics} topics, {Subscriptions} subscriptions",
            allQueueNames.Count, allTopicNames.Count, _busConfiguration.SubscribedTopicNames.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task EnsureQueueExistsAsync(string queueName, CancellationToken cancellationToken)
    {
        try
        {
            if (!await _adminClient.QueueExistsAsync(queueName, cancellationToken))
            {
                var options = new CreateQueueOptions(queueName)
                {
                    RequiresSession = queueName.EndsWith(".fifo", StringComparison.OrdinalIgnoreCase),
                    MaxDeliveryCount = 10,
                    LockDuration = TimeSpan.FromMinutes(5)
                };

                await _adminClient.CreateQueueAsync(options, cancellationToken);
                _logger.LogInformation("Created Azure Service Bus queue: {Queue}", queueName);
            }
            else
            {
                _logger.LogDebug("Azure Service Bus queue already exists: {Queue}", queueName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ensuring queue exists: {Queue}", queueName);
            throw;
        }
    }

    private async Task EnsureTopicExistsAsync(string topicName, CancellationToken cancellationToken)
    {
        try
        {
            if (!await _adminClient.TopicExistsAsync(topicName, cancellationToken))
            {
                await _adminClient.CreateTopicAsync(topicName, cancellationToken);
                _logger.LogInformation("Created Azure Service Bus topic: {Topic}", topicName);
            }
            else
            {
                _logger.LogDebug("Azure Service Bus topic already exists: {Topic}", topicName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ensuring topic exists: {Topic}", topicName);
            throw;
        }
    }

    private async Task EnsureSubscriptionExistsAsync(
        string topicName,
        string forwardToQueueName,
        CancellationToken cancellationToken)
    {
        var subscriptionName = $"fwd-to-{forwardToQueueName}";

        try
        {
            if (!await _adminClient.SubscriptionExistsAsync(topicName, subscriptionName, cancellationToken))
            {
                var options = new CreateSubscriptionOptions(topicName, subscriptionName)
                {
                    ForwardTo = forwardToQueueName,
                    MaxDeliveryCount = 10,
                    LockDuration = TimeSpan.FromMinutes(5)
                };

                await _adminClient.CreateSubscriptionAsync(options, cancellationToken);
                _logger.LogInformation(
                    "Created subscription: {Topic}/{Subscription} -> forwarding to {Queue}",
                    topicName, subscriptionName, forwardToQueueName);
            }
            else
            {
                _logger.LogDebug(
                    "Subscription already exists: {Topic}/{Subscription}",
                    topicName, subscriptionName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error ensuring subscription exists: {Topic}/{Subscription}",
                topicName, subscriptionName);
            throw;
        }
    }
}

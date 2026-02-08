using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Cloud.Azure.Configuration;

public interface IAzureCommandRoutingConfiguration
{
    /// <summary>
    /// Determines if a command type should be routed to Azure
    /// </summary>
    bool ShouldRouteToAzure<TCommand>() where TCommand : ICommand;

    /// <summary>
    /// Gets the Service Bus queue name for a command type
    /// </summary>
    string GetQueueName<TCommand>() where TCommand : ICommand;

    /// <summary>
    /// Gets all queue names this service should listen to
    /// </summary>
    IEnumerable<string> GetListeningQueues();
}

public interface IAzureEventRoutingConfiguration
{
    /// <summary>
    /// Determines if an event type should be routed to Azure
    /// </summary>
    bool ShouldRouteToAzure<TEvent>() where TEvent : IEvent;

    /// <summary>
    /// Gets the Service Bus topic name for an event type
    /// </summary>
    string GetTopicName<TEvent>() where TEvent : IEvent;

    /// <summary>
    /// Gets all topic/subscription pairs this service should listen to
    /// </summary>
    IEnumerable<(string TopicName, string SubscriptionName)> GetListeningSubscriptions();
}
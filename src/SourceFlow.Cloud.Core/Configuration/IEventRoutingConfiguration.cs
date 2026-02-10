using SourceFlow.Messaging.Events;

namespace SourceFlow.Cloud.Core.Configuration;

public interface IEventRoutingConfiguration
{
    /// <summary>
    /// Determines if an event type should be routed to a remote broker.
    /// </summary>
    bool ShouldRouteToAws<TEvent>() where TEvent : IEvent;

    /// <summary>
    /// Gets the topic ARN for an event type.
    /// </summary>
    string GetTopicArn<TEvent>() where TEvent : IEvent;

    /// <summary>
    /// Gets all queue URLs this service listens to for inbound events.
    /// </summary>
    IEnumerable<string> GetListeningQueues();

    /// <summary>
    /// Gets all topic ARNs this service subscribes to for inbound events.
    /// Configured via <c>.Subscribe.To.Topic(...)</c> in <see cref="BusConfigurationBuilder"/>.
    /// </summary>
    IEnumerable<string> GetSubscribedTopics();
}

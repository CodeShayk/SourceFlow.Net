using SourceFlow.Messaging.Commands;

namespace SourceFlow.Cloud.Core.Configuration;

public interface ICommandRoutingConfiguration
{
    /// <summary>
    /// Determines if a command type should be routed to a remote broker.
    /// </summary>
    bool ShouldRouteToAws<TCommand>() where TCommand : ICommand;

    /// <summary>
    /// Gets the queue URL for a command type.
    /// </summary>
    string GetQueueUrl<TCommand>() where TCommand : ICommand;

    /// <summary>
    /// Gets all queue URLs this service should listen to.
    /// </summary>
    IEnumerable<string> GetListeningQueues();
}

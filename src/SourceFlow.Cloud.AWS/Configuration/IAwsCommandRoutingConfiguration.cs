using SourceFlow.Messaging.Commands;

namespace SourceFlow.Cloud.AWS.Configuration;

public interface IAwsCommandRoutingConfiguration
{
    /// <summary>
    /// Determines if a command type should be routed to AWS
    /// </summary>
    bool ShouldRouteToAws<TCommand>() where TCommand : ICommand;

    /// <summary>
    /// Gets the SQS queue URL for a command type
    /// </summary>
    string GetQueueUrl<TCommand>() where TCommand : ICommand;

    /// <summary>
    /// Gets all queue URLs this service should listen to
    /// </summary>
    IEnumerable<string> GetListeningQueues();
}
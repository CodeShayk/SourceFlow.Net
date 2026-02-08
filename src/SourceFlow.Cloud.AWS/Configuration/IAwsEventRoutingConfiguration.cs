using SourceFlow.Messaging.Events;

namespace SourceFlow.Cloud.AWS.Configuration;

public interface IAwsEventRoutingConfiguration
{
    /// <summary>
    /// Determines if an event type should be routed to AWS
    /// </summary>
    bool ShouldRouteToAws<TEvent>() where TEvent : IEvent;

    /// <summary>
    /// Gets the SNS topic ARN for an event type
    /// </summary>
    string GetTopicArn<TEvent>() where TEvent : IEvent;

    /// <summary>
    /// Gets all SQS queue URLs subscribed to SNS topics for listening
    /// </summary>
    IEnumerable<string> GetListeningQueues();
}
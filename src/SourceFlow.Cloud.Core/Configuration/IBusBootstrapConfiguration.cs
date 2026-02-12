namespace SourceFlow.Cloud.Core.Configuration;

/// <summary>
/// Exposes the short-name data and resolution callback needed by the bus bootstrapper.
/// Implemented by <see cref="BusConfiguration"/>; injected into the bootstrapper so
/// the concrete type is never referenced directly from the cloud provider assembly.
/// </summary>
public interface IBusBootstrapConfiguration
{
    /// <summary>Command type → short queue name set at configuration time.</summary>
    IReadOnlyDictionary<Type, string> CommandTypeToQueueName { get; }

    /// <summary>Event type → short topic name set at configuration time.</summary>
    IReadOnlyDictionary<Type, string> EventTypeToTopicName { get; }

    /// <summary>Short queue names this service polls for inbound commands.</summary>
    IReadOnlyList<string> CommandListeningQueueNames { get; }

    /// <summary>Short topic names this service subscribes to for inbound events.</summary>
    IReadOnlyList<string> SubscribedTopicNames { get; }

    /// <summary>
    /// Called once by the bootstrapper after all queues and topics have been verified
    /// or created. Injects the resolved full URLs and ARNs used at runtime.
    /// </summary>
    void Resolve(
        Dictionary<Type, string> commandRoutes,
        Dictionary<Type, string> eventRoutes,
        List<string> commandListeningUrls,
        List<string> subscribedTopicArns,
        List<string> eventListeningUrls);
}

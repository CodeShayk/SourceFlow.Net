using SourceFlow.Observability;
using System.Diagnostics.Metrics;

namespace SourceFlow.Cloud.Azure.Observability;

public static class AzureTelemetryExtensions
{
    private static readonly Meter Meter = new Meter("SourceFlow.Cloud.Azure", "1.0.0");

    private static readonly Counter<long> CommandsDispatchedCounter =
        Meter.CreateCounter<long>("azure.servicebus.commands.dispatched",
            description: "Number of commands dispatched to Azure Service Bus");

    private static readonly Counter<long> EventsPublishedCounter =
        Meter.CreateCounter<long>("azure.servicebus.events.published",
            description: "Number of events published to Azure Service Bus");

    public static void RecordAzureCommandDispatched(
        this IDomainTelemetryService telemetry,
        string commandType,
        string queueName)
    {
        CommandsDispatchedCounter.Add(1,
            new KeyValuePair<string, object?>("command_type", commandType),
            new KeyValuePair<string, object?>("queue_name", queueName));
    }

    public static void RecordAzureEventPublished(
        this IDomainTelemetryService telemetry,
        string eventType,
        string topicName)
    {
        EventsPublishedCounter.Add(1,
            new KeyValuePair<string, object?>("event_type", eventType),
            new KeyValuePair<string, object?>("topic_name", topicName));
    }
}

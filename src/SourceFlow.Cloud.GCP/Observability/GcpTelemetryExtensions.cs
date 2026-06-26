using SourceFlow.Observability;
using System.Diagnostics.Metrics;

namespace SourceFlow.Cloud.GCP.Observability;

public static class GcpTelemetryExtensions
{
    private static readonly Meter Meter = new Meter("SourceFlow.Cloud.GCP", "1.0.0");

    private static readonly Counter<long> CommandsDispatchedCounter =
        Meter.CreateCounter<long>("gcp.pubsub.commands.dispatched",
            description: "Number of commands published to Google Cloud Pub/Sub");

    private static readonly Counter<long> EventsPublishedCounter =
        Meter.CreateCounter<long>("gcp.pubsub.events.published",
            description: "Number of events published to Google Cloud Pub/Sub");

    public static void RecordGcpCommandDispatched(
        this IDomainTelemetryService telemetry,
        string commandType,
        string topic)
    {
        CommandsDispatchedCounter.Add(1,
            new KeyValuePair<string, object?>("command_type", commandType),
            new KeyValuePair<string, object?>("topic", topic));
    }

    public static void RecordGcpEventPublished(
        this IDomainTelemetryService telemetry,
        string eventType,
        string topic)
    {
        EventsPublishedCounter.Add(1,
            new KeyValuePair<string, object?>("event_type", eventType),
            new KeyValuePair<string, object?>("topic", topic));
    }
}

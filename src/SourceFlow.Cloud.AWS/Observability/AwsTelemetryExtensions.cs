using SourceFlow.Observability;
using System.Diagnostics.Metrics;

namespace SourceFlow.Cloud.AWS.Observability;

public static class AwsTelemetryExtensions
{
    private static readonly Meter Meter = new Meter("SourceFlow.Cloud.AWS", "1.0.0");

    private static readonly Counter<long> CommandsDispatchedCounter =
        Meter.CreateCounter<long>("aws.sqs.commands.dispatched",
            description: "Number of commands dispatched to AWS SQS");

    private static readonly Counter<long> EventsPublishedCounter =
        Meter.CreateCounter<long>("aws.sns.events.published",
            description: "Number of events published to AWS SNS");

    public static void RecordAwsCommandDispatched(
        this IDomainTelemetryService telemetry,
        string commandType,
        string queueUrl)
    {
        CommandsDispatchedCounter.Add(1,
            new KeyValuePair<string, object?>("command_type", commandType),
            new KeyValuePair<string, object?>("queue_url", queueUrl));
    }

    public static void RecordAwsEventPublished(
        this IDomainTelemetryService telemetry,
        string eventType,
        string topicArn)
    {
        EventsPublishedCounter.Add(1,
            new KeyValuePair<string, object?>("event_type", eventType),
            new KeyValuePair<string, object?>("topic_arn", topicArn));
    }
}

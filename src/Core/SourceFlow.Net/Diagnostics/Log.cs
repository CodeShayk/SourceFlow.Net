using Microsoft.Extensions.Logging;

namespace SourceFlow.Diagnostics;

/// <summary>Source-generated log messages (doc 08 §4). Event ids: 1000-1099 commands, 1100 outbox, 1200 receive, 1300 subscriptions, 1400 hosting.</summary>
internal static partial class Log
{
    [LoggerMessage(1001, LogLevel.Debug, "Command {CommandType} on {StreamId}: {Outcome} at version {Version}")]
    public static partial void CommandHandled(ILogger logger, string commandType, string streamId, string outcome, long version);

    [LoggerMessage(1002, LogLevel.Information, "Concurrency conflict on {StreamId} (attempt {Attempt}); re-deciding")]
    public static partial void ConcurrencyRetry(ILogger logger, string streamId, int attempt);

    [LoggerMessage(1003, LogLevel.Warning, "Concurrency retries exhausted on {StreamId} after {Attempts} attempts")]
    public static partial void ConcurrencyExhausted(ILogger logger, string streamId, int attempts);

    [LoggerMessage(1101, LogLevel.Warning, "Outbox dispatch to {Destination} failed (attempt {Attempt}); retrying at {RetryAt}")]
    public static partial void OutboxRetry(ILogger logger, Exception exception, string destination, int attempt, DateTimeOffset retryAt);

    [LoggerMessage(1102, LogLevel.Error, "Outbox message {MessageId} to {Destination} parked after {Attempts} attempts")]
    public static partial void OutboxParked(ILogger logger, Exception exception, string messageId, string destination, int attempts);

    [LoggerMessage(1103, LogLevel.Error, "Outbox relay loop failed; continuing")]
    public static partial void OutboxLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(1104, LogLevel.Warning, "Retention cleanup failed; retrying at the next interval")]
    public static partial void RetentionFailed(ILogger logger, Exception exception);

    [LoggerMessage(1105, LogLevel.Information, "Retention cleanup deleted {Inbox} inbox rows and {DeadLetters} resolved dead letters")]
    public static partial void RetentionPurged(ILogger logger, int inbox, int deadLetters);

    [LoggerMessage(1108, LogLevel.Debug, "Payload of parked message {MessageId}: {Payload}")]
    public static partial void ParkedPayload(ILogger logger, string messageId, string payload);

    [LoggerMessage(1107, LogLevel.Warning, "Schema {Type} v{Version} changed without a new version (registered {Registered}, now {Current}); a published version is immutable")]
    public static partial void SchemaConflict(ILogger logger, string type, int version, string registered, string current);

    [LoggerMessage(1106, LogLevel.Warning, "Node heartbeat failed; the lease fair share uses the last known node count")]
    public static partial void HeartbeatFailed(ILogger logger, Exception exception);

    [LoggerMessage(1201, LogLevel.Warning, "Message {MessageId} ({MessageType}) failed transiently on attempt {Attempt}; redelivering after {Delay}")]
    public static partial void ReceiveRetry(ILogger logger, Exception exception, string messageId, string messageType, int attempt, TimeSpan delay);

    [LoggerMessage(1202, LogLevel.Error, "Message {MessageId} ({MessageType}) parked: {Reason}")]
    public static partial void ReceiveParked(ILogger logger, Exception? exception, string messageId, string messageType, string reason);

    [LoggerMessage(1203, LogLevel.Error, "Receive loop for {Endpoint} failed; continuing")]
    public static partial void ReceiveLoopFailed(ILogger logger, Exception exception, string endpoint);

    [LoggerMessage(1301, LogLevel.Error, "Subscription {Subscription} partition {Partition} failed on event {EventId}; {Policy}")]
    public static partial void SubscriptionFailed(ILogger logger, Exception exception, string subscription, int partition, string eventId, string policy);

    [LoggerMessage(1303, LogLevel.Warning, "Subscription {Subscription} partition {Partition} skipped event {EventId} at {Position}: {Reason}")]
    public static partial void SubscriptionSkipped(ILogger logger, string subscription, int partition, string eventId, string position, string reason);

    [LoggerMessage(1302, LogLevel.Warning, "Subscription {Subscription} partition {Partition} lost its lease")]
    public static partial void SubscriptionLeaseLost(ILogger logger, string subscription, int partition);

    [LoggerMessage(1304, LogLevel.Information, "Read model {Projection} switched from {Previous} to {Target}")]
    public static partial void ReadModelSwitched(ILogger logger, string projection, string previous, string target);

    [LoggerMessage(1305, LogLevel.Information, "Dropped retired read-model generation {Generation}")]
    public static partial void ReadModelDropped(ILogger logger, string generation);

    [LoggerMessage(1401, LogLevel.Information, "SourceFlow service {Service} started: {Modules} module(s), {Aggregates} aggregate(s), {Subscriptions} subscription(s)")]
    public static partial void Started(ILogger logger, string service, int modules, int aggregates, int subscriptions);
}

using System.Diagnostics;

namespace SourceFlow.Cloud.Core.Observability;

/// <summary>
/// Activity source for distributed tracing in cloud messaging
/// </summary>
public static class CloudActivitySource
{
    /// <summary>
    /// Name of the activity source
    /// </summary>
    public const string SourceName = "SourceFlow.Cloud";

    /// <summary>
    /// Version of the activity source
    /// </summary>
    public const string Version = "1.0.0";

    /// <summary>
    /// The activity source instance
    /// </summary>
    public static readonly ActivitySource Instance = new(SourceName, Version);

    /// <summary>
    /// Semantic conventions for messaging attributes
    /// </summary>
    public static class SemanticConventions
    {
        // System attributes
        public const string MessagingSystem = "messaging.system";
        public const string MessagingDestination = "messaging.destination";
        public const string MessagingDestinationKind = "messaging.destination_kind";
        public const string MessagingOperation = "messaging.operation";

        // Message attributes
        public const string MessagingMessageId = "messaging.message_id";
        public const string MessagingMessagePayloadSize = "messaging.message_payload_size_bytes";
        public const string MessagingConversationId = "messaging.conversation_id";

        // SourceFlow-specific attributes
        public const string SourceFlowCommandType = "sourceflow.command.type";
        public const string SourceFlowEventType = "sourceflow.event.type";
        public const string SourceFlowEntityId = "sourceflow.entity.id";
        public const string SourceFlowSequenceNo = "sourceflow.sequence_no";
        public const string SourceFlowIsReplay = "sourceflow.is_replay";

        // Cloud-specific attributes
        public const string CloudProvider = "cloud.provider";
        public const string CloudRegion = "cloud.region";
        public const string CloudQueue = "cloud.queue";
        public const string CloudTopic = "cloud.topic";

        // Performance attributes
        public const string ProcessingDuration = "sourceflow.processing.duration_ms";
        public const string QueueDepth = "sourceflow.queue.depth";
        public const string RetryCount = "sourceflow.retry.count";
    }

    /// <summary>
    /// Destination kinds
    /// </summary>
    public static class DestinationKind
    {
        public const string Queue = "queue";
        public const string Topic = "topic";
    }

    /// <summary>
    /// Operation types
    /// </summary>
    public static class Operation
    {
        public const string Send = "send";
        public const string Receive = "receive";
        public const string Process = "process";
        public const string Publish = "publish";
    }
}

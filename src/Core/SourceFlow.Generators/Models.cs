namespace SourceFlow.Generators
{
    // Value-equatable pipeline models. They hold only strings and primitives, never symbols, so the incremental
    // pipeline can cache every step (GEN-38).

    internal enum IdBackingKind
    {
        Guid = 0,
        String = 1,
        Int64 = 2,
    }

    internal sealed record IdModel(string Namespace, string Name, string FullName, string Keyword, IdBackingKind Backing, int MaxLength);

    internal sealed record MemberModel(string Name, string TypeFullName, bool IsAggregateId);

    internal sealed record MessageModel(
        string FullName,
        string LogicalName,
        int Version,
        string Kind,
        string? TargetAggregate,
        EquatableArray<MemberModel> Members);

    internal sealed record AggregateModel(
        string Namespace,
        string Name,
        string FullName,
        string Keyword,
        string AggregateName,
        int SnapshotEvery,
        int SnapshotVersion,
        string IdType,
        EquatableArray<string> DecidedCommands,
        EquatableArray<string> EvolvedEvents);

    internal sealed record ProcessManagerModel(
        string Namespace,
        string Name,
        string FullName,
        string Keyword,
        string ProcessName,
        string IdType,
        EquatableArray<string> ReactedMessages,
        EquatableArray<string> CorrelatedMessages,
        EquatableArray<string> TextCorrelatedMessages);

    internal enum KeySource
    {
        Fallback = 0,
        KeyOf = 1,
        Member = 2,
    }

    internal sealed record ProjectedEvent(string EventType, KeySource KeySource, string? Member);

    internal enum KeyKind
    {
        Other = 0,
        StronglyTypedId = 1,
        String = 2,
    }

    internal sealed record ProjectionModel(
        string Namespace,
        string Name,
        string FullName,
        string Keyword,
        string ProjectionName,
        int Version,
        string DocumentType,
        string KeyType,
        KeyKind KeyKind,
        EquatableArray<ProjectedEvent> Events);

    /// <summary>A session projection's <c>When</c> method: the event and whether it returns <c>Task</c> (else <c>ValueTask</c>).</summary>
    internal sealed record SessionProjectedEvent(string EventType, bool ReturnsTask);

    internal sealed record SessionProjectionModel(
        string Namespace,
        string Name,
        string FullName,
        string Keyword,
        string ProjectionName,
        int Version,
        string UnitOfWorkType,
        EquatableArray<SessionProjectedEvent> Events);

    internal enum HandlerKind
    {
        Command = 0,
        Event = 1,
        Upcaster = 2,
    }

    internal sealed record HandlerModel(HandlerKind Kind, string FullName, string First, string? Second);

    internal sealed record ModuleModel(string Namespace, string Name, string FullName, string? JsonContext);
}

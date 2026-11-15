using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SourceFlow.Workflows;

/// <summary>The status of a DSL workflow instance (doc 15 §5.3).</summary>
public enum WorkflowStatus
{
    /// <summary>Executing steps.</summary>
    Running = 0,

    /// <summary>Finished successfully.</summary>
    Completed = 1,

    /// <summary>Undoing completed steps in reverse order.</summary>
    Compensating = 2,

    /// <summary>Failed and fully rolled back.</summary>
    Compensated = 3,

    /// <summary>Waiting for an operator (<see cref="WorkflowControl"/>).</summary>
    ManualIntervention = 4,

    /// <summary>Stopped by an operator.</summary>
    Aborted = 5,
}

/// <summary>
/// The id of a workflow instance: the value of the start message's <c>correlate</c> path. Its stream segment is
/// <c>wf_</c> and the value's UTF-8 bytes in hex, so any value (a GUID with dashes, an order number) is a valid segment.
/// </summary>
public readonly record struct WorkflowInstanceId : IStronglyTypedId<WorkflowInstanceId>
{
    private const string Prefix = "wf_";

    /// <summary>Creates an id from the correlation value.</summary>
    public WorkflowInstanceId(string value)
    {
        Value = value ?? string.Empty;
    }

    /// <summary>The correlation value.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public bool IsDefault => string.IsNullOrEmpty(Value);

    /// <inheritdoc />
    /// <remarks>Any non-empty value is valid: the stream segment is hex, so it never contains a dash.</remarks>
    public bool IsValid => !IsDefault;

    /// <summary>The id of a correlation value.</summary>
    public static WorkflowInstanceId From(string value)
    {
        return new WorkflowInstanceId(value);
    }

    /// <inheritdoc />
    public string ToStreamSegment()
    {
        if (IsDefault)
        {
            throw new InvalidOperationException("The default workflow instance id has no stream segment.");
        }

        return Prefix + Convert.ToHexStringLower(Encoding.UTF8.GetBytes(Value));
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Value ?? string.Empty;
    }

    /// <inheritdoc />
    public static WorkflowInstanceId Parse(string value)
    {
        return TryParse(value, out WorkflowInstanceId id) ? id : throw new FormatException($"'{value}' is not a workflow instance id.");
    }

    /// <summary>Parses a stream segment (<c>wf_…</c>) or a raw correlation value.</summary>
    public static bool TryParse(string? value, out WorkflowInstanceId id)
    {
        if (string.IsNullOrEmpty(value))
        {
            id = default;
            return false;
        }

        if (value.StartsWith(Prefix, StringComparison.Ordinal) && value.Length % 2 == 1)
        {
            try
            {
                id = new WorkflowInstanceId(Encoding.UTF8.GetString(Convert.FromHexString(value.AsSpan(Prefix.Length))));
                return true;
            }
            catch (FormatException)
            {
                // not hex: a raw value that happens to start with the prefix
            }
        }

        id = new WorkflowInstanceId(value);
        return true;
    }
}

/// <summary>
/// A workflow timer (doc 15 §5.3): a step or compensation timeout, or the end of a retry delay, for one attempt.
/// Delivered by the scheduler to the owning service; a timer for an older attempt is ignored.
/// </summary>
/// <param name="Workflow">The process name, <c>name@version</c>.</param>
/// <param name="Instance">The instance (correlation value).</param>
/// <param name="Kind"><c>timeout</c>, <c>compensate-timeout</c> or <c>retry</c>.</param>
/// <param name="Step">The step, or <c>compensate:{step}</c> for a compensation's timers.</param>
/// <param name="Attempt">The attempt the timer belongs to.</param>
[MessageType(TypeName, 1)]
public sealed record WorkflowTimer(string Workflow, string Instance, string Kind, string Step, int Attempt) : ITimeout
{
    /// <summary>The message type name.</summary>
    public const string TypeName = "sourceflow.workflow.timer";

    /// <summary>
    /// When the timer was due. A delivered timer counts only if it is the instance's pending timer, identity and due time
    /// alike, so a timer cancelled and re-armed for the same attempt is ignored when the old one arrives (doc 15 §5.3,
    /// CR-19). Null on timers scheduled before it was recorded: those match by identity.
    /// </summary>
    public DateTimeOffset? Due { get; init; }
}

/// <summary>
/// An operator action on a workflow instance (doc 15 §5.3): resume, retry the current step, or abort. Sent by
/// <c>dotnet sourceflow workflow resume|retry-step|abort</c> through the scheduler to the owning service.
/// </summary>
/// <param name="Workflow">The process name, <c>name@version</c>.</param>
/// <param name="Instance">The instance (correlation value).</param>
/// <param name="Action"><see cref="ResumeAction"/>, <see cref="RetryStepAction"/> or <see cref="AbortAction"/>.</param>
/// <param name="Step">For resume: the step to resume at (default: the current one).</param>
/// <param name="Reason">For abort: why.</param>
/// <param name="Compensate">For abort: compensate the completed steps first.</param>
[MessageType(TypeName, 1)]
public sealed record WorkflowControl(string Workflow, string Instance, string Action, string? Step = null, string? Reason = null, bool Compensate = false) : ITimeout
{
    /// <summary>The message type name.</summary>
    public const string TypeName = "sourceflow.workflow.control";

    /// <summary>Resume a workflow in manual intervention (<c>ResumeWorkflow</c>).</summary>
    public const string ResumeAction = "resume";

    /// <summary>Re-send the current step or compensation (<c>RetryWorkflowStep</c>).</summary>
    public const string RetryStepAction = "retry-step";

    /// <summary>Stop the instance (<c>AbortWorkflow</c>).</summary>
    public const string AbortAction = "abort";
}

/// <summary>Why a workflow failed: <c>$failure</c> in <c>on.compensated</c>.</summary>
/// <param name="Code">The rejection code or failure event type, or <c>timeout</c>.</param>
/// <param name="Message">A description.</param>
/// <param name="Step">The step that failed.</param>
public sealed record WorkflowFailure(string Code, string Message, string Step);

/// <summary>A pending compensation: a completed step whose undo runs if the workflow fails.</summary>
/// <param name="Step">The step.</param>
public sealed record CompensationEntry(string Step);

/// <summary>Marks the <c>$hook</c> implementations of a generated workflow (pure and deterministic, as <c>React</c>).</summary>
public interface IWorkflowHooks;

/// <summary>
/// A transition of a workflow instance (doc 15 §7): what <c>sourceflow.workflow.transitions</c> counts and what each
/// <c>workflow {name} {step}</c> span describes. Generated workflows add them to their <see cref="Reaction"/>; the runtime
/// records them once the reaction has committed.
/// </summary>
/// <param name="Kind">One of <see cref="Kinds"/>.</param>
/// <param name="Step">The step (or compensated step), when there is one.</param>
/// <param name="Attempt">The attempt of the step or compensation.</param>
public sealed record WorkflowTransition(string Kind, string? Step, int Attempt)
{
    /// <summary>A step succeeded.</summary>
    public const string Success = "success";

    /// <summary>A step failed with no retry left.</summary>
    public const string Failure = "failure";

    /// <summary>A step or compensation is retried.</summary>
    public const string Retry = "retry";

    /// <summary>A step or compensation timed out.</summary>
    public const string Timeout = "timeout";

    /// <summary>A completed step is being compensated.</summary>
    public const string Compensate = "compensate";

    /// <summary>The instance needs an operator.</summary>
    public const string Manual = "manual";

    /// <summary>The instance completed.</summary>
    public const string Complete = "complete";

    /// <summary>Every kind (the <c>kind</c> tag's values).</summary>
    public static IReadOnlyList<string> Kinds { get; } = [Success, Failure, Retry, Timeout, Compensate, Manual, Complete];
}

/// <summary>What a workflow instance is doing (doc 15 §6.1 observe, §7).</summary>
/// <param name="Status">The status.</param>
/// <param name="Step">The current step, or the step being compensated; null when terminal.</param>
/// <param name="Attempt">The attempt of the current step or compensation.</param>
/// <param name="Failure">Why it failed, once it has.</param>
/// <param name="PendingCompensations">Completed steps that would be compensated on failure.</param>
/// <param name="StartedAt">When the instance started.</param>
/// <param name="UpdatedAt">When it last moved (the reaction time).</param>
public sealed record WorkflowInstanceInfo(
    WorkflowStatus Status,
    string? Step,
    int Attempt,
    WorkflowFailure? Failure,
    int PendingCompensations,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A generated workflow instance, seen by the runtime without its type.</summary>
public interface IWorkflowInstance
{
    /// <summary>The instance's status and position, or null before the start message.</summary>
    WorkflowInstanceInfo? Describe();
}

/// <summary>
/// A document of the <c>workflow-instances</c> read model: one per workflow instance, written in the same transaction as
/// the instance's state. The generated domain query <c>&lt;service&gt;.workflow-instances.v1</c>, <c>dotnet sourceflow
/// workflow list|show</c> and the <c>sourceflow.workflow.active</c> gauge read it (doc 15 §6.1, §7).
/// </summary>
/// <param name="Workflow">The workflow name (without the version).</param>
/// <param name="WorkflowVersion">The workflow version (not <c>version</c>: that is every read-model row's document version).</param>
/// <param name="Instance">The instance (the correlation value).</param>
/// <param name="Status">The <see cref="WorkflowStatus"/> name.</param>
/// <param name="Step">The current step; null when terminal.</param>
/// <param name="Attempt">The attempt of the current step or compensation.</param>
/// <param name="FailureCode">The failure code, once failed.</param>
/// <param name="FailureMessage">The failure description, once failed.</param>
/// <param name="FailureStep">The step that failed.</param>
/// <param name="PendingCompensations">Completed steps that would be compensated on failure.</param>
/// <param name="StartedAt">When the instance started.</param>
/// <param name="UpdatedAt">When it last moved.</param>
public sealed record WorkflowInstanceSummary(
    string Workflow,
    int WorkflowVersion,
    string Instance,
    string Status,
    string? Step,
    int Attempt,
    string? FailureCode,
    string? FailureMessage,
    string? FailureStep,
    int PendingCompensations,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The read model's name in queries (<c>from readmodel "workflow-instances"</c>).</summary>
    public const string ReadModel = "workflow-instances";

    /// <summary>The document collection.</summary>
    public const string Collection = "workflow-instances@1";

    /// <summary>The statuses of an instance that is not finished.</summary>
    public static IReadOnlyList<string> ActiveStatuses { get; } =
        [nameof(WorkflowStatus.Running), nameof(WorkflowStatus.Compensating), nameof(WorkflowStatus.ManualIntervention)];

    /// <summary>The process type, <c>name@version</c>.</summary>
    public string ProcessName => Workflow + "@" + WorkflowVersion.ToString(CultureInfo.InvariantCulture);

    /// <summary>True while the instance is not finished.</summary>
    public bool IsActive => ActiveStatuses.Contains(Status, StringComparer.Ordinal);

    /// <summary>The document id of an instance: <c>name@version/instance</c>.</summary>
    public static string DocumentId(string processName, string instance)
    {
        return processName + "/" + instance;
    }
}

/// <summary>The number of instances of a workflow version in a status (the <c>sourceflow.workflow.active</c> gauge).</summary>
/// <param name="Workflow">The workflow name.</param>
/// <param name="Version">The version.</param>
/// <param name="Status">The status name.</param>
/// <param name="Count">The instances.</param>
public sealed record WorkflowStatusCount(string Workflow, int Version, string Status, int Count);

/// <summary>A generated workflow's state: the engine state as JSON (WP-S.5).</summary>
/// <typeparam name="TSelf">The generated workflow.</typeparam>
public interface IWorkflowState<TSelf> : IWorkflowInstance
    where TSelf : class, IWorkflowState<TSelf>
{
    /// <summary>The engine state as JSON, or null before the start message.</summary>
    string? State { get; }

    /// <summary>Creates the state.</summary>
    static abstract TSelf FromState(string? state);
}

/// <summary>
/// A workflow version that still has active instances is no longer registered (doc 15 §5.6): startup fails, because
/// in-flight instances finish on the version they started on.
/// </summary>
public sealed class WorkflowVersionRemovedException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public WorkflowVersionRemovedException(string message)
        : base(message)
    {
    }
}

/// <summary>Runtime support for generated workflows: their message and state JSON metadata.</summary>
public static class WorkflowRuntime
{
    /// <summary>Registers <see cref="WorkflowTimer"/> and <see cref="WorkflowControl"/>; generated modules call it.</summary>
    public static void RegisterMessages(ISourceFlowModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddMessage<WorkflowTimer>(WorkflowTimer.TypeName, 1);
        builder.AddMessage<WorkflowControl>(WorkflowControl.TypeName, 1);
        builder.AddJsonTypeInfoResolver(WorkflowJsonContext.Default);
    }

    /// <summary>The JSON metadata of a generated workflow's state: <c>{ "state": { …engine state… } }</c>.</summary>
    public static IJsonTypeInfoResolver StateResolver<TState>()
        where TState : class, IWorkflowState<TState>
    {
        return new StateTypeInfoResolver<TState>();
    }

    private sealed class StateTypeInfoResolver<TState> : IJsonTypeInfoResolver
        where TState : class, IWorkflowState<TState>
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            return type == typeof(TState) ? JsonMetadataServices.CreateValueInfo<TState>(options, new StateConverter<TState>()) : null;
        }
    }

    private sealed class StateConverter<TState> : JsonConverter<TState>
        where TState : class, IWorkflowState<TState>
    {
        public override TState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            return TState.FromState(document.RootElement.TryGetProperty("state", out JsonElement state) && state.ValueKind == JsonValueKind.Object
                ? state.GetRawText()
                : null);
        }

        public override void Write(Utf8JsonWriter writer, TState value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("state");
            if (value.State is { } state)
            {
                writer.WriteRawValue(state, skipInputValidation: false);
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteEndObject();
        }
    }

    /// <summary>The text of the generated <c>&lt;service&gt;.workflow-instances</c> domain query (one workflow's instances, newest first).</summary>
    public const string InstancesQueryText =
        "from readmodel \"workflow-instances\"\n" +
        "| where workflow = @workflow\n" +
        "| project workflow, workflowVersion, instance, status, step, attempt, failureCode, failureMessage, failureStep, pendingCompensations, startedAt, updatedAt\n" +
        "| order by updatedAt desc";

    /// <summary>The name suffix of the generated domain query: <c>&lt;service&gt;.workflow-instances</c>.</summary>
    public const string InstancesQuerySuffix = ".workflow-instances";

    /// <summary>The parameters of the generated domain query.</summary>
    public static IReadOnlyList<DomainQueryParameter> InstancesQueryParameters { get; } = [new("workflow", typeof(string))];

    /// <summary>The result columns of the generated domain query.</summary>
    public static IReadOnlyList<string> InstancesQueryColumns { get; } =
        ["workflow", "workflowVersion", "instance", "status", "step", "attempt", "failureCode", "failureMessage", "failureStep", "pendingCompensations", "startedAt", "updatedAt"];

    /// <summary>Builds a result row of the generated domain query.</summary>
    public static WorkflowInstanceSummary MaterializeInstance(IQueryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new WorkflowInstanceSummary(
            DomainQueryValues.Get<string>(DomainQueryValues.Column(row, "workflow")),
            DomainQueryValues.Get<int>(DomainQueryValues.Column(row, "workflowVersion")),
            DomainQueryValues.Get<string>(DomainQueryValues.Column(row, "instance")),
            DomainQueryValues.Get<string>(DomainQueryValues.Column(row, "status")),
            DomainQueryValues.Get<string?>(DomainQueryValues.Column(row, "step")),
            DomainQueryValues.Get<int>(DomainQueryValues.Column(row, "attempt")),
            DomainQueryValues.Get<string?>(DomainQueryValues.Column(row, "failureCode")),
            DomainQueryValues.Get<string?>(DomainQueryValues.Column(row, "failureMessage")),
            DomainQueryValues.Get<string?>(DomainQueryValues.Column(row, "failureStep")),
            DomainQueryValues.Get<int>(DomainQueryValues.Column(row, "pendingCompensations")),
            DomainQueryValues.Get<DateTimeOffset>(DomainQueryValues.Column(row, "startedAt")),
            DomainQueryValues.Get<DateTimeOffset>(DomainQueryValues.Column(row, "updatedAt")));
    }

    /// <summary>Splits a workflow process name <c>name@version</c>.</summary>
    public static bool TryParseProcessName(string? processName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? name, out int version)
    {
        int at = processName?.LastIndexOf('@') ?? -1;
        if (at > 0 && int.TryParse(processName.AsSpan(at + 1), NumberStyles.None, CultureInfo.InvariantCulture, out version) && version > 0)
        {
            name = processName![..at];
            return true;
        }

        name = null;
        version = 0;
        return false;
    }

    /// <summary>The read-model document of an instance.</summary>
    /// <param name="processName">The process type, <c>name@version</c>.</param>
    /// <param name="instance">The instance (correlation value).</param>
    /// <param name="info">What it is doing.</param>
    public static WorkflowInstanceSummary Summarize(string processName, string instance, WorkflowInstanceInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!TryParseProcessName(processName, out string? name, out int version))
        {
            throw new ArgumentException($"'{processName}' is not a workflow process name (name@version).", nameof(processName));
        }

        return new WorkflowInstanceSummary(name, version, instance, info.Status.ToString(), info.Step, info.Attempt,
            info.Failure?.Code, info.Failure?.Message, info.Failure?.Step, info.PendingCompensations, info.StartedAt, info.UpdatedAt);
    }

    /// <summary>Serializes a summary document.</summary>
    public static byte[] SerializeSummary(WorkflowInstanceSummary summary)
    {
        return JsonSerializer.SerializeToUtf8Bytes(summary, WorkflowJsonContext.Default.WorkflowInstanceSummary);
    }

    /// <summary>Reads a summary document.</summary>
    public static WorkflowInstanceSummary DeserializeSummary(ReadOnlySpan<byte> json)
    {
        return JsonSerializer.Deserialize(json, WorkflowJsonContext.Default.WorkflowInstanceSummary)
            ?? throw new JsonException("A workflow instance document is null.");
    }

    /// <summary>The message body of an operator action (camelCase JSON, as the service reads it).</summary>
    public static byte[] SerializeControl(WorkflowControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return JsonSerializer.SerializeToUtf8Bytes(control, WorkflowJsonContext.Default.WorkflowControl);
    }

    /// <summary>Formats a correlation value read from a message member the way the engine does.</summary>
    public static string CorrelationValue(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string s => s,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }
}

[JsonSerializable(typeof(WorkflowTimer))]
[JsonSerializable(typeof(WorkflowControl))]
[JsonSerializable(typeof(WorkflowInstanceSummary))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class WorkflowJsonContext : JsonSerializerContext;

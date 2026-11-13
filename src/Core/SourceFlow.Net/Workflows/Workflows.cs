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
/// <param name="Kind"><c>timeout</c> or <c>retry</c>.</param>
/// <param name="Step">The step.</param>
/// <param name="Attempt">The attempt the timer belongs to.</param>
[MessageType(TypeName, 1)]
public sealed record WorkflowTimer(string Workflow, string Instance, string Kind, string Step, int Attempt) : ITimeout
{
    /// <summary>The message type name.</summary>
    public const string TypeName = "sourceflow.workflow.timer";
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

/// <summary>A generated workflow's state: the engine state as JSON (WP-S.5).</summary>
/// <typeparam name="TSelf">The generated workflow.</typeparam>
public interface IWorkflowState<TSelf>
    where TSelf : class, IWorkflowState<TSelf>
{
    /// <summary>The engine state as JSON, or null before the start message.</summary>
    string? State { get; }

    /// <summary>Creates the state.</summary>
    static abstract TSelf FromState(string? state);
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
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class WorkflowJsonContext : JsonSerializerContext;

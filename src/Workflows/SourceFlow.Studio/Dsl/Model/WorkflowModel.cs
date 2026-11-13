using System;
using System.Collections.Generic;

namespace SourceFlow.Studio.Dsl.Model
{
    /// <summary>A 1-based position in a workflow file.</summary>
    public readonly struct SourceLocation : IEquatable<SourceLocation>
    {
        /// <summary>Creates a location.</summary>
        public SourceLocation(int line, int column)
        {
            Line = line;
            Column = column;
        }

        /// <summary>No location (line 0).</summary>
        public static SourceLocation None => default;

        /// <summary>The 1-based line, or 0 when unknown.</summary>
        public int Line { get; }

        /// <summary>The 1-based column, or 0 when unknown.</summary>
        public int Column { get; }

        /// <inheritdoc />
        public bool Equals(SourceLocation other)
        {
            return Line == other.Line && Column == other.Column;
        }

        /// <inheritdoc />
        public override bool Equals(object? obj)
        {
            return obj is SourceLocation other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return (Line * 397) ^ Column;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return "(" + Line + "," + Column + ")";
        }
    }

    /// <summary>A model node: where it starts and where each of its keys is.</summary>
    public abstract class WorkflowNode
    {
        private readonly Dictionary<string, SourceLocation> _keys = new Dictionary<string, SourceLocation>(StringComparer.Ordinal);

        /// <summary>Where the node starts.</summary>
        public SourceLocation Location { get; internal set; }

        /// <summary>Where the value of <paramref name="key"/> starts, or the node's location.</summary>
        public SourceLocation At(string key)
        {
            return _keys.TryGetValue(key, out SourceLocation location) ? location : Location;
        }

        internal void Mark(string key, SourceLocation location)
        {
            _keys[key] = location;
        }
    }

    /// <summary>A workflow (<c>sourceflow.workflow: 1</c>, doc 15 §5).</summary>
    public sealed class WorkflowDefinition : WorkflowNode
    {
        /// <summary>The DSL version this reader understands.</summary>
        public const int DslVersion = 1;

        /// <summary>The <c>sourceflow.workflow</c> value.</summary>
        public int SchemaVersion { get; internal set; }

        /// <summary>The workflow name, for example <c>order-fulfilment</c>.</summary>
        public string Name { get; internal set; } = string.Empty;

        /// <summary>The workflow version (structural changes need a new one, WF-V15).</summary>
        public int Version { get; internal set; }

        /// <summary>The owning (orchestrating) service.</summary>
        public string Owner { get; internal set; } = string.Empty;

        /// <summary>The description, if any.</summary>
        public string? Description { get; internal set; }

        /// <summary>What starts an instance.</summary>
        public StartSpec Start { get; internal set; } = new StartSpec();

        /// <summary>The instance data, in file order.</summary>
        public IReadOnlyList<DataField> Data { get; internal set; } = Array.Empty<DataField>();

        /// <summary>The steps, in file order.</summary>
        public IReadOnlyList<WorkflowStep> Steps { get; internal set; } = Array.Empty<WorkflowStep>();

        /// <summary>The terminal-state actions.</summary>
        public TerminalActions On { get; internal set; } = new TerminalActions();

        /// <summary>The <c>name@version</c> process name.</summary>
        public string ProcessName => Name + "@" + Version;

        /// <summary>The step called <paramref name="id"/>, or null.</summary>
        public WorkflowStep? Step(string id)
        {
            foreach (WorkflowStep step in Steps)
            {
                if (step.Id == id)
                {
                    return step;
                }
            }

            return null;
        }

        /// <summary>The index of the pivot step, or -1.</summary>
        public int PivotIndex
        {
            get
            {
                for (int i = 0; i < Steps.Count; i++)
                {
                    if (Steps[i].Pivot)
                    {
                        return i;
                    }
                }

                return -1;
            }
        }
    }

    /// <summary><c>start</c>: the message that creates an instance and how it is correlated.</summary>
    public sealed class StartSpec : WorkflowNode
    {
        /// <summary>The message type, <c>{name}.v{version}</c>.</summary>
        public string On { get; internal set; } = string.Empty;

        /// <summary>The path of the instance id in the start message, for example <c>data.orderId</c>.</summary>
        public string Correlate { get; internal set; } = string.Empty;

        /// <summary>An optional CESQL guard on the start message.</summary>
        public string? When { get; internal set; }
    }

    /// <summary>An instance data field.</summary>
    public sealed class DataField : WorkflowNode
    {
        /// <summary>The field name.</summary>
        public string Name { get; internal set; } = string.Empty;

        /// <summary>The source path, for example <c>start.data.orderId</c>; null for a typed field with a default.</summary>
        public string? From { get; internal set; }

        /// <summary>The declared type when there is no <see cref="From"/>.</summary>
        public string? Type { get; internal set; }

        /// <summary>The default value when there is no <see cref="From"/>.</summary>
        public ScalarValue? Default { get; internal set; }

        /// <summary>The owner marks the field as personal data (crypto-shredded in state, WF-V14).</summary>
        public bool Personal { get; internal set; }
    }

    /// <summary>A step: send a command (optionally) and await its outcome.</summary>
    public sealed class WorkflowStep : WorkflowNode
    {
        /// <summary>The step id.</summary>
        public string Id { get; internal set; } = string.Empty;

        /// <summary>The command to send, or null for an await-only step.</summary>
        public SendSpec? Send { get; internal set; }

        /// <summary>What the step waits for.</summary>
        public AwaitSpec? Await { get; internal set; }

        /// <summary>The wait's timeout.</summary>
        public TimeoutSpec? Timeout { get; internal set; }

        /// <summary>How the step's effect is undone, or null.</summary>
        public CompensateSpec? Compensate { get; internal set; }

        /// <summary>The point of no return.</summary>
        public bool Pivot { get; internal set; }

        /// <summary>A CESQL guard; the step is skipped when it is false.</summary>
        public string? When { get; internal set; }

        /// <summary>The keyword for <see cref="Next"/> that completes the workflow; no step can be called this.</summary>
        public const string CompleteKeyword = "complete";

        /// <summary>The step after a success, instead of the following one, or <c>complete</c>.</summary>
        public string? Next { get; internal set; }
    }

    /// <summary>A command to send and how its fields are filled.</summary>
    public sealed class SendSpec : WorkflowNode
    {
        /// <summary>The command type, <c>{name}.v{version}</c>.</summary>
        public string Command { get; internal set; } = string.Empty;

        /// <summary>The command fields, in file order.</summary>
        public IReadOnlyList<MapEntry> Map { get; internal set; } = Array.Empty<MapEntry>();
    }

    /// <summary>One command field and its value.</summary>
    public sealed class MapEntry : WorkflowNode
    {
        /// <summary>The command field (its JSON name).</summary>
        public string Field { get; internal set; } = string.Empty;

        /// <summary>The value.</summary>
        public MapValue Value { get; internal set; } = MapValue.Null;
    }

    /// <summary>What a mapped value is.</summary>
    public enum MapValueKind
    {
        /// <summary>A path: <c>$data.x</c>, <c>event.data.x</c> or <c>$failure.x</c>.</summary>
        Path = 0,

        /// <summary>A literal.</summary>
        Literal = 1,

        /// <summary>A <c>{ $hook: Name }</c> partial method.</summary>
        Hook = 2,
    }

    /// <summary>A mapped value.</summary>
    public sealed class MapValue
    {
        internal static readonly MapValue Null = new MapValue(MapValueKind.Literal, null, ScalarValue.NullValue);

        internal MapValue(MapValueKind kind, string? text, ScalarValue? literal)
        {
            Kind = kind;
            Text = text;
            Literal = literal;
        }

        /// <summary>The value kind.</summary>
        public MapValueKind Kind { get; }

        /// <summary>The path or hook name.</summary>
        public string? Text { get; }

        /// <summary>The literal.</summary>
        public ScalarValue? Literal { get; }
    }

    /// <summary>The kind of a scalar.</summary>
    public enum ScalarKind
    {
        /// <summary><c>null</c>.</summary>
        Null = 0,

        /// <summary>A string.</summary>
        String = 1,

        /// <summary>An integer.</summary>
        Integer = 2,

        /// <summary>A decimal number.</summary>
        Number = 3,

        /// <summary><c>true</c> or <c>false</c>.</summary>
        Boolean = 4,
    }

    /// <summary>A YAML scalar with its resolved type (YAML 1.2 core schema; quoted values are strings).</summary>
    public sealed class ScalarValue : IEquatable<ScalarValue>
    {
        internal static readonly ScalarValue NullValue = new ScalarValue(ScalarKind.Null, null);

        internal ScalarValue(ScalarKind kind, string? text)
        {
            Kind = kind;
            Text = text;
        }

        /// <summary>The kind.</summary>
        public ScalarKind Kind { get; }

        /// <summary>The text (invariant culture), or null for <c>null</c>.</summary>
        public string? Text { get; }

        /// <inheritdoc />
        public bool Equals(ScalarValue? other)
        {
            return other is not null && Kind == other.Kind && Text == other.Text;
        }

        /// <inheritdoc />
        public override bool Equals(object? obj)
        {
            return Equals(obj as ScalarValue);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return ((int)Kind * 397) ^ (Text is null ? 0 : StringComparer.Ordinal.GetHashCode(Text));
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return Kind == ScalarKind.Null ? "null" : Kind == ScalarKind.String ? "'" + Text + "'" : Text ?? string.Empty;
        }
    }

    /// <summary><c>await</c>: the success and the failures.</summary>
    public sealed class AwaitSpec : WorkflowNode
    {
        /// <summary>The success outcome.</summary>
        public SuccessSpec Success { get; internal set; } = new SuccessSpec();

        /// <summary>The failure outcomes, in file order.</summary>
        public IReadOnlyList<FailureSpec> Failures { get; internal set; } = Array.Empty<FailureSpec>();
    }

    /// <summary>The success event, what it captures and an optional guard.</summary>
    public sealed class SuccessSpec : WorkflowNode
    {
        /// <summary>The event type.</summary>
        public string On { get; internal set; } = string.Empty;

        /// <summary>Data captured from the event, in file order.</summary>
        public IReadOnlyList<CaptureSpec> Capture { get; internal set; } = Array.Empty<CaptureSpec>();

        /// <summary>A CESQL guard on the event; a false guard means the event is ignored.</summary>
        public string? When { get; internal set; }
    }

    /// <summary>One captured value: <c>dataField: event.data.x</c>.</summary>
    public sealed class CaptureSpec : WorkflowNode
    {
        /// <summary>The data field.</summary>
        public string Field { get; internal set; } = string.Empty;

        /// <summary>The source path in the event.</summary>
        public string Path { get; internal set; } = string.Empty;
    }

    /// <summary>A failure outcome: a rejection code or a failure event.</summary>
    public sealed class FailureSpec : WorkflowNode
    {
        /// <summary>The rejection code (<c>CommandRejected</c> with this code), or null.</summary>
        public string? Rejected { get; internal set; }

        /// <summary>The failure event type, or null.</summary>
        public string? On { get; internal set; }

        /// <summary>A business retry before <see cref="Then"/>.</summary>
        public RetrySpec? Retry { get; internal set; }

        /// <summary>What happens when the failure is final.</summary>
        public ThenSpec Then { get; internal set; } = ThenSpec.Fail;
    }

    /// <summary>The kind of a <c>then</c>.</summary>
    public enum ThenKind
    {
        /// <summary>Compensate the completed steps (before the pivot only).</summary>
        Fail = 0,

        /// <summary>Stop in <c>ManualIntervention</c>.</summary>
        Manual = 1,

        /// <summary>Complete the workflow.</summary>
        Complete = 2,

        /// <summary>Go to a step.</summary>
        Goto = 3,
    }

    /// <summary>What happens after a final failure or timeout.</summary>
    public sealed class ThenSpec : WorkflowNode
    {
        internal static readonly ThenSpec Fail = new ThenSpec(ThenKind.Fail, null);

        internal ThenSpec(ThenKind kind, string? target)
        {
            Kind = kind;
            Target = target;
        }

        /// <summary>The kind.</summary>
        public ThenKind Kind { get; }

        /// <summary>The <c>goto</c> step.</summary>
        public string? Target { get; }

        /// <inheritdoc />
        public override string ToString()
        {
            return Kind == ThenKind.Goto ? "goto " + Target : Kind.ToString().ToLowerInvariant();
        }
    }

    /// <summary><c>timeout</c>: how long a wait lasts, retries on expiry, then what.</summary>
    public sealed class TimeoutSpec : WorkflowNode
    {
        /// <summary>The wait per attempt; null with <see cref="None"/>.</summary>
        public TimeSpan? After { get; internal set; }

        /// <summary><c>timeout: none</c> (needs <see cref="Reason"/>).</summary>
        public bool None { get; internal set; }

        /// <summary>Why the wait has no timeout.</summary>
        public string? Reason { get; internal set; }

        /// <summary>Re-sends on expiry before <see cref="Then"/>.</summary>
        public RetrySpec? Retry { get; internal set; }

        /// <summary>What happens when the last attempt expires.</summary>
        public ThenSpec Then { get; internal set; } = ThenSpec.Fail;
    }

    /// <summary>The backoff of a retry.</summary>
    public enum Backoff
    {
        /// <summary>The same delay each time.</summary>
        Fixed = 0,

        /// <summary>Doubling delays, up to the cap.</summary>
        Exponential = 1,
    }

    /// <summary><c>retry</c>: bounded re-attempts.</summary>
    public sealed class RetrySpec : WorkflowNode
    {
        /// <summary>The number of re-attempts (1–20, WF-V10).</summary>
        public int Max { get; internal set; }

        /// <summary>The backoff.</summary>
        public Backoff Backoff { get; internal set; } = Backoff.Fixed;

        /// <summary>The first delay (zero: re-send immediately).</summary>
        public TimeSpan Initial { get; internal set; }

        /// <summary>The longest delay, or null.</summary>
        public TimeSpan? Cap { get; internal set; }

        /// <summary>Whether delays are jittered at run time (default true; the simulator uses the nominal delay).</summary>
        public bool Jitter { get; internal set; } = true;

        /// <summary>The delay before re-attempt <paramref name="attempt"/> (1-based), without jitter.</summary>
        public TimeSpan Delay(int attempt)
        {
            long ticks = Initial.Ticks;
            if (Backoff == Backoff.Exponential)
            {
                for (int i = 1; i < attempt && ticks < TimeSpan.MaxValue.Ticks / 2; i++)
                {
                    ticks *= 2;
                }
            }

            if (Cap is { } cap && ticks > cap.Ticks)
            {
                ticks = cap.Ticks;
            }

            return TimeSpan.FromTicks(ticks);
        }
    }

    /// <summary><c>compensate</c>: the semantic undo of a completed step.</summary>
    public sealed class CompensateSpec : WorkflowNode
    {
        /// <summary><c>compensate: none</c> (needs <see cref="Reason"/>).</summary>
        public bool None { get; internal set; }

        /// <summary>Why the step needs no compensation.</summary>
        public string? Reason { get; internal set; }

        /// <summary>The compensating command.</summary>
        public SendSpec? Send { get; internal set; }

        /// <summary>Its success event (no failure branches: WF-V16).</summary>
        public AwaitSpec? Await { get; internal set; }

        /// <summary>Re-sends on failure or timeout.</summary>
        public RetrySpec? Retry { get; internal set; }

        /// <summary>The wait per attempt.</summary>
        public TimeoutSpec? Timeout { get; internal set; }
    }

    /// <summary><c>on</c>: what happens when the workflow reaches a terminal state.</summary>
    public sealed class TerminalActions : WorkflowNode
    {
        /// <summary>Sent on <c>Completed</c>.</summary>
        public SendSpec? Completed { get; internal set; }

        /// <summary>Sent on <c>Compensated</c>; may use <c>$failure.code</c>.</summary>
        public SendSpec? Compensated { get; internal set; }

        /// <summary>Raise the manual-intervention alert (default true).</summary>
        public bool ManualAlert { get; internal set; } = true;
    }
}

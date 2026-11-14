using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SourceFlow.Studio.Dsl.Expressions;
using SourceFlow.Studio.Dsl.Model;

namespace SourceFlow.Studio.Dsl.Simulation
{
    /// <summary>The status of a workflow instance (doc 15 §5.3).</summary>
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

        /// <summary>Waiting for an operator.</summary>
        ManualIntervention = 4,

        /// <summary>Stopped by an operator.</summary>
        Aborted = 5,
    }

    /// <summary>What the current step is doing.</summary>
    public enum WorkflowPhase
    {
        /// <summary>Waiting for the outcome of the current attempt.</summary>
        Awaiting = 0,

        /// <summary>Waiting for a retry delay before re-sending.</summary>
        Retrying = 1,
    }

    /// <summary>
    /// A timer: a step timeout, a compensation timeout or a retry delay, for one attempt. A compensation's timers name the
    /// step <c>compensate:{step}</c>, so they never share an identity with the step's own (doc 15 §5.3, CR-19).
    /// </summary>
    public sealed record SimulationTimer(string Kind, string Step, int Attempt, DateTimeOffset Due)
    {
        /// <summary>A step timeout.</summary>
        public const string Timeout = "timeout";

        /// <summary>A compensation timeout.</summary>
        public const string CompensateTimeout = "compensate-timeout";

        /// <summary>The step a compensation's timers name: <c>compensate:{step}</c>.</summary>
        public static string CompensationStep(string step) => "compensate:" + step;

        /// <summary>A retry delay.</summary>
        public const string Retry = "retry";
    }

    /// <summary>Why the workflow failed: available as <c>$failure</c> in <c>on.compensated</c>.</summary>
    public sealed record WorkflowFailure(string Code, string Message, string Step);

    /// <summary>One observable effect of a transition.</summary>
    public sealed record SimulationEffect(string Kind, string Text)
    {
        /// <summary>A command was sent (outbox).</summary>
        public const string Send = "send";

        /// <summary>A timer was scheduled.</summary>
        public const string Schedule = "schedule";

        /// <summary>A timer was cancelled.</summary>
        public const string Cancel = "cancel";

        /// <summary>The status changed.</summary>
        public const string Status = "status";

        /// <summary>A value was captured into data.</summary>
        public const string Capture = "capture";

        /// <summary>A guarded step was skipped.</summary>
        public const string Skip = "skip";

        /// <summary>The manual-intervention alert.</summary>
        public const string Alert = "alert";

        /// <summary>The input was ignored (stale timer, terminal instance, no match).</summary>
        public const string Ignored = "ignored";

        /// <summary>An operator overrode a rule, recorded for audit (a compensation after the pivot, CR-20).</summary>
        public const string Audit = "audit";

        /// <summary>The command, for <see cref="Send"/>.</summary>
        public SentCommand? Command { get; init; }

        /// <summary>The timer, for <see cref="Schedule"/> and <see cref="Cancel"/>.</summary>
        public SimulationTimer? Timer { get; init; }

        /// <inheritdoc />
        public override string ToString()
        {
            return Kind + " " + Text;
        }
    }

    /// <summary>
    /// A transition of an instance, for the <c>sourceflow.workflow.transitions</c> metric and the <c>workflow</c> span
    /// (doc 15 §7). Not an effect: it is how the run is counted, not what it does.
    /// </summary>
    public sealed record SimulationTransition(string Kind, string? Step, int Attempt)
    {
        /// <summary>A step succeeded.</summary>
        public const string Success = "success";

        /// <summary>A step failed (a rejection, a failure event or a timeout) with no retry left.</summary>
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

        /// <summary>Every kind, in the order of doc 15 §7.</summary>
        public static IReadOnlyList<string> Kinds { get; } = new[] { Success, Failure, Retry, Timeout, Compensate, Manual, Complete };

        /// <inheritdoc />
        public override string ToString()
        {
            return Kind + (Step is null ? string.Empty : " " + Step + "#" + Attempt.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>A sent command: its type, deterministic message id and field values.</summary>
    public sealed record SentCommand(string Command, Guid MessageId, IReadOnlyList<KeyValuePair<string, object?>> Fields)
    {
        /// <inheritdoc />
        public override string ToString()
        {
            return Command + "(" + string.Join(", ", Fields.Select(f => f.Key + ": " + Format(f.Value))) + ")";
        }

        internal static string Format(object? value)
        {
            switch (value)
            {
                case null: return "null";
                case string s: return "'" + s + "'";
                case bool b: return b ? "true" : "false";
                case decimal d: return d.ToString(CultureInfo.InvariantCulture);
                case IEnumerable<object?> list: return "[" + string.Join(", ", list.Select(Format)) + "]";
                default: return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }
    }

    /// <summary>An instance's state: everything the generated process manager keeps (doc 15 §5.3).</summary>
    public sealed class SimulationState
    {
        internal SimulationState(WorkflowDefinition workflow, string instance)
        {
            Workflow = workflow;
            Instance = instance;
        }

        /// <summary>The workflow.</summary>
        public WorkflowDefinition Workflow { get; }

        /// <summary>The instance id (the correlated start value).</summary>
        public string Instance { get; }

        /// <summary>The status.</summary>
        public WorkflowStatus Status { get; internal set; }

        /// <summary>The current step, or the compensated step while compensating; null when terminal.</summary>
        public string? Step { get; internal set; }

        /// <summary>Awaiting or retrying.</summary>
        public WorkflowPhase Phase { get; internal set; }

        /// <summary>The current attempt (1-based) of the step or compensation.</summary>
        public int Attempt { get; internal set; }

        /// <summary>Retries used per outcome of the current step or compensation (<c>failure:{index}</c>, <c>timeout</c>, <c>compensation</c>).</summary>
        public IReadOnlyDictionary<string, int> Retries => _retries;

        /// <summary>The steps to compensate, top last.</summary>
        public IReadOnlyList<string> CompensationStack => _stack;

        /// <summary>The instance data.</summary>
        public IReadOnlyDictionary<string, object?> Data => _data;

        /// <summary>The start message's data.</summary>
        public IReadOnlyDictionary<string, object?> StartData { get; internal set; } = new Dictionary<string, object?>();

        /// <summary>Why the workflow failed, once it has.</summary>
        public WorkflowFailure? Failure { get; internal set; }

        /// <summary>The pending timer, or null.</summary>
        public SimulationTimer? Timer { get; internal set; }

        /// <summary>The simulated clock.</summary>
        public DateTimeOffset Now { get; internal set; }

        /// <summary>When the instance started (the start message's reaction time).</summary>
        public DateTimeOffset StartedAt { get; internal set; }

        /// <summary>True while compensating for an operator abort (ends in Aborted, not Compensated).</summary>
        public bool Aborting { get; internal set; }

        /// <summary>True in ManualIntervention when a compensation failed: resuming continues the compensation.</summary>
        public bool CompensationPending { get; internal set; }

        /// <summary>
        /// True once the pivot step succeeded (doc 15 §2, CR-20): from then on only forward recovery is possible, so the
        /// engine starts no compensation (an operator <c>abort --compensate</c> excepted, audited) and resumes no step before it.
        /// </summary>
        public bool PivotPassed { get; internal set; }

        /// <summary>Whether the instance is finished.</summary>
        public bool IsTerminal => Status == WorkflowStatus.Completed || Status == WorkflowStatus.Compensated || Status == WorkflowStatus.Aborted;

        internal Dictionary<string, int> _retries = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// The last attempt number used per step (and per <c>compensate:{step}</c>), across re-entries: a step entered
        /// again (a <c>goto</c> loop, an operator resume) continues the numbering, so its send ids and timers never repeat
        /// an earlier visit's (an owner's inbox would drop the re-send; an old timer would look current).
        /// </summary>
        public IReadOnlyDictionary<string, int> Attempts => _attempts;

        internal Dictionary<string, int> _attempts = new Dictionary<string, int>(StringComparer.Ordinal);

        internal List<string> _stack = new List<string>();

        internal Dictionary<string, object?> _data = new Dictionary<string, object?>(StringComparer.Ordinal);

        internal SimulationOptions Options { get; set; } = SimulationOptions.Default;

        internal SimulationState Clone()
        {
            return new SimulationState(Workflow, Instance)
            {
                Status = Status,
                Step = Step,
                Phase = Phase,
                Attempt = Attempt,
                StartData = StartData,
                Failure = Failure,
                Timer = Timer,
                Now = Now,
                StartedAt = StartedAt,
                Aborting = Aborting,
                CompensationPending = CompensationPending,
                PivotPassed = PivotPassed,
                Options = Options,
                _retries = new Dictionary<string, int>(_retries, StringComparer.Ordinal),
                _attempts = new Dictionary<string, int>(_attempts, StringComparer.Ordinal),
                _stack = new List<string>(_stack),
                _data = new Dictionary<string, object?>(_data, StringComparer.Ordinal),
            };
        }
    }

    /// <summary>Options: guard overrides (path enumeration) and hook values (they are C# methods in the service).</summary>
    public sealed class SimulationOptions
    {
        /// <summary>No overrides; hooks evaluate to a placeholder.</summary>
        public static SimulationOptions Default { get; } = new SimulationOptions();

        /// <summary>The <see cref="Guard"/> key of <c>start.when</c>.</summary>
        public const string StartGuard = "$start";

        /// <summary>The <see cref="Guard"/> key prefix of a step's <c>success.when</c> (followed by the step id).</summary>
        public const string SuccessGuard = "$success:";

        /// <summary>Forces a step's guard (or <c>start.when</c>, key <see cref="StartGuard"/>): true runs it, false skips it, null evaluates the guard.</summary>
        public Func<string, bool?>? Guard { get; set; }

        /// <summary>The value of a <c>$hook</c> from its name and the instance data; null gives the placeholder <c>&lt;hook:Name&gt;</c>.</summary>
        public Func<string, IReadOnlyDictionary<string, object?>, object?>? Hook { get; set; }
    }

    /// <summary>An input to the simulator: an outcome, a timer firing, or an operator command.</summary>
    public abstract class SimulationInput
    {
        private protected SimulationInput()
        {
        }

        /// <summary>The awaited success event arrives (for the current step or compensation).</summary>
        public static SimulationInput Succeed(IReadOnlyDictionary<string, object?>? data = null) => new Outcome("success", null, data);

        /// <summary>The command is rejected with <paramref name="code"/>.</summary>
        public static SimulationInput Reject(string code, string? message = null) => new Outcome("rejected", code, null) { Message = message };

        /// <summary>A failure event arrives.</summary>
        public static SimulationInput FailWith(string eventType, IReadOnlyDictionary<string, object?>? data = null) => new Outcome("event", eventType, data);

        /// <summary>The pending timer fires (a timeout, or the end of a retry delay).</summary>
        public static SimulationInput FireTimer() => new TimerInput(null, null, null, null);

        /// <summary>A specific timer message arrives (stale ones are ignored).</summary>
        public static SimulationInput Timer(string kind, string step, int attempt) => new TimerInput(kind, step, attempt, null);

        /// <summary>
        /// A delivered timer with its due time: it counts only if it is the pending timer, identity and due time alike, so a
        /// timer cancelled and later re-armed for the same attempt is ignored when the old one arrives (CR-19).
        /// </summary>
        public static SimulationInput Timer(string kind, string step, int attempt, DateTimeOffset? due) => new TimerInput(kind, step, attempt, due);

        /// <summary>
        /// A message of type <paramref name="type"/> arrives (the runtime): the success or a failure event of what the
        /// instance waits for now, or ignored.
        /// </summary>
        public static SimulationInput Receive(string type, IReadOnlyDictionary<string, object?>? data = null) => new Received(type, data ?? new Dictionary<string, object?>());

        /// <summary>
        /// <paramref name="command"/> was rejected with <paramref name="code"/> (the runtime): a failure when it is the
        /// command the instance waits on, otherwise ignored (a reply to an older step).
        /// </summary>
        public static SimulationInput RejectedFor(string command, string code, string? message = null) => new Rejection(command, code, message);

        /// <summary>
        /// A rejection reply caused by the command with id <paramref name="cause"/>: it counts only for the attempt that sent
        /// it, so a late reply to an earlier attempt is ignored (doc 15 §5.5, CR-33).
        /// </summary>
        public static SimulationInput RejectedFor(string command, string code, string? message, Guid? cause) => new Rejection(command, code, message, cause);

        /// <summary>Operator: <c>ResumeWorkflow</c>.</summary>
        public static SimulationInput Resume(string? fromStep = null) => new Operator("resume", fromStep, false);

        /// <summary>Operator: <c>AbortWorkflow</c>.</summary>
        public static SimulationInput Abort(string reason, bool compensate = false) => new Operator("abort", reason, compensate);

        /// <summary>Operator: <c>RetryWorkflowStep</c>.</summary>
        public static SimulationInput RetryStep() => new Operator("retry-step", null, false);

        internal sealed class Outcome : SimulationInput
        {
            public Outcome(string kind, string? value, IReadOnlyDictionary<string, object?>? data)
            {
                Kind = kind;
                Value = value;
                Data = data ?? new Dictionary<string, object?>();
            }

            public string Kind { get; }

            public string? Value { get; }

            public string? Message { get; init; }

            public IReadOnlyDictionary<string, object?> Data { get; }

            public override string ToString() => Kind == "success" ? "success" : Kind + " " + Value;
        }

        internal sealed class Received : SimulationInput
        {
            public Received(string type, IReadOnlyDictionary<string, object?> data)
            {
                Type = type;
                Data = data;
            }

            public string Type { get; }

            public IReadOnlyDictionary<string, object?> Data { get; }

            public override string ToString() => "receive " + Type;
        }

        internal sealed class Rejection : SimulationInput
        {
            public Rejection(string command, string code, string? message, Guid? cause = null)
            {
                Command = command;
                Code = code;
                Message = message;
                Cause = cause;
            }

            public string Command { get; }

            /// <summary>The id of the rejected command, when the reply carries it (its causation).</summary>
            public Guid? Cause { get; }

            public string Code { get; }

            public string? Message { get; }

            public override string ToString() => "rejected " + Command + " " + Code;
        }

        internal sealed class TimerInput : SimulationInput
        {
            public TimerInput(string? kind, string? step, int? attempt, DateTimeOffset? due)
            {
                Kind = kind;
                Step = step;
                Attempt = attempt;
                Due = due;
            }

            public string? Kind { get; }

            public string? Step { get; }

            public int? Attempt { get; }

            public DateTimeOffset? Due { get; }

            public override string ToString() => Kind is null ? "timer" : Kind + " " + Step + "#" + Attempt;
        }

        internal sealed class Ignore : SimulationInput
        {
            public Ignore(string why)
            {
                Why = why;
            }

            public string Why { get; }

            public override string ToString() => "ignore";
        }

        internal sealed class Operator : SimulationInput
        {
            public Operator(string action, string? argument, bool compensate)
            {
                Action = action;
                Argument = argument;
                Compensate = compensate;
            }

            public string Action { get; }

            public string? Argument { get; }

            public bool Compensate { get; }

            public override string ToString() => Action + (Argument is null ? string.Empty : " " + Argument);
        }
    }

    /// <summary>The new state and the effects of one transition.</summary>
    public sealed class SimulationResult
    {
        internal SimulationResult(SimulationState state, IReadOnlyList<SimulationEffect> effects, IReadOnlyList<SimulationTransition> transitions)
        {
            State = state;
            Effects = effects;
            Transitions = transitions;
        }

        /// <summary>The state after the transition.</summary>
        public SimulationState State { get; }

        /// <summary>The effects, in order.</summary>
        public IReadOnlyList<SimulationEffect> Effects { get; }

        /// <summary>The transitions taken, in order (what the metrics count, doc 15 §7).</summary>
        public IReadOnlyList<SimulationTransition> Transitions { get; }

        /// <summary>The commands sent by the transition (the outbox).</summary>
        public IEnumerable<SentCommand> Sent => Effects.Where(e => e.Command is not null).Select(e => e.Command!);
    }

    /// <summary>
    /// A deterministic simulator of the transition table in doc 15 §5.3. The generated process manager implements the
    /// same table (WF-DIFF compares them). Pure: every call returns a new state.
    /// </summary>
    public static class WorkflowSimulator
    {
        /// <summary>Starts an instance from the start message.</summary>
        /// <param name="workflow">A valid workflow.</param>
        /// <param name="startData">The start message's data.</param>
        /// <param name="now">The simulated time of the start.</param>
        /// <param name="options">Guard overrides and hook values.</param>
        /// <returns>The state after the first step was entered; <see cref="SimulationEffect.Ignored"/> when <c>start.when</c> is false.</returns>
        public static SimulationResult Start(WorkflowDefinition workflow, IReadOnlyDictionary<string, object?> startData, DateTimeOffset now, SimulationOptions? options = null)
        {
            if (workflow is null)
            {
                throw new ArgumentNullException(nameof(workflow));
            }

            if (startData is null)
            {
                throw new ArgumentNullException(nameof(startData));
            }

            string correlate = workflow.Start.Correlate.StartsWith("data.", StringComparison.Ordinal) ? workflow.Start.Correlate.Substring(5) : workflow.Start.Correlate;
            string instance = SentCommand.Format(Read(startData, correlate)).Trim('\'');
            var run = new Run(new SimulationState(workflow, instance) { Now = now, StartedAt = now, StartData = startData, Options = options ?? SimulationOptions.Default });
            if (workflow.Start.When is { } when && GuardParser.Parse(when).Expression is { } guard
                && run.State.Options.Guard?.Invoke(SimulationOptions.StartGuard) != true
                && !GuardEvaluator.Evaluate(guard, path => path.StartsWith("event.data.", StringComparison.Ordinal) ? Read(startData, path.Substring(11)) : null))
            {
                run.Effect(SimulationEffect.Ignored, "start.when is false: no instance");
                run.State.Status = WorkflowStatus.Aborted;
                return run.Result();
            }

            foreach (DataField field in workflow.Data)
            {
                run.State._data[field.Name] = field.From is { } from
                    ? Read(startData, from.Substring("start.data.".Length))
                    : field.Default is { } value ? Literal(value) : null;
            }

            run.Effect(SimulationEffect.Status, "Running");
            run.Enter(0);
            return run.Result();
        }

        /// <summary>Applies <paramref name="input"/> to <paramref name="state"/>.</summary>
        public static SimulationResult Step(SimulationState state, SimulationInput input)
        {
            if (state is null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (input is null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            var run = new Run(state.Clone());
            run.Apply(input);
            return run.Result();
        }

        internal static object? Read(IReadOnlyDictionary<string, object?> data, string path)
        {
            object? current = data;
            foreach (string segment in path.Split('.'))
            {
                if (current is IReadOnlyDictionary<string, object?> map && map.TryGetValue(segment, out object? next))
                {
                    current = next;
                }
                else if (current is IDictionary<string, object?> mutable && mutable.TryGetValue(segment, out object? next2))
                {
                    current = next2;
                }
                else
                {
                    return null;
                }
            }

            return current;
        }

        internal static object? Literal(ScalarValue value)
        {
            switch (value.Kind)
            {
                case ScalarKind.String: return value.Text;
                case ScalarKind.Integer:
                case ScalarKind.Number: return decimal.Parse(value.Text!, NumberStyles.Float, CultureInfo.InvariantCulture);
                case ScalarKind.Boolean: return value.Text == "true";
                default: return null;
            }
        }

        private sealed class Run
        {
            private readonly List<SimulationEffect> _effects = new List<SimulationEffect>();
            private readonly List<SimulationTransition> _transitions = new List<SimulationTransition>();

            public Run(SimulationState state)
            {
                State = state;
            }

            public SimulationState State { get; }

            private WorkflowDefinition Workflow => State.Workflow;

            public SimulationResult Result()
            {
                return new SimulationResult(State, _effects, _transitions);
            }

            // The next attempt of a step or compensation: one after the last used, across re-entries.
            private void NextAttempt(string key)
            {
                State.Attempt = (State._attempts.TryGetValue(key, out int last) ? last : 0) + 1;
                State._attempts[key] = State.Attempt;
            }

            private void Transition(string kind, string? step)
            {
                _transitions.Add(new SimulationTransition(kind, step, State.Attempt));
            }

            public void Effect(string kind, string text, SentCommand? command = null, SimulationTimer? timer = null)
            {
                _effects.Add(new SimulationEffect(kind, text) { Command = command, Timer = timer });
            }

            public void Apply(SimulationInput input)
            {
                if (State.IsTerminal)
                {
                    Effect(SimulationEffect.Ignored, "the instance is " + State.Status);
                    return;
                }

                switch (input)
                {
                    case SimulationInput.Received received:
                        Apply(Classify(received) ?? (SimulationInput)new SimulationInput.Ignore(received + ": not awaited now"));
                        return;
                    case SimulationInput.Rejection rejection:
                        Apply(Classify(rejection) ?? (SimulationInput)new SimulationInput.Ignore(rejection + ": not the command awaited now"));
                        return;
                    case SimulationInput.Ignore ignore:
                        Effect(SimulationEffect.Ignored, ignore.Why);
                        return;
                    case SimulationInput.Operator op:
                        Operator(op);
                        return;
                    case SimulationInput.TimerInput timer:
                        Timer(timer);
                        return;
                    case SimulationInput.Outcome outcome when State.Status == WorkflowStatus.Compensating:
                        CompensationOutcome(outcome);
                        return;
                    case SimulationInput.Outcome outcome when State.Status == WorkflowStatus.Running && State.Phase == WorkflowPhase.Awaiting:
                        StepOutcome(outcome);
                        return;
                    default:
                        Effect(SimulationEffect.Ignored, input + " while " + State.Status + (State.Status == WorkflowStatus.Running ? " (" + State.Phase + ")" : string.Empty));
                        return;
                }
            }

            // A received message as the outcome it is now: the current step's success or failure event, or the compensation's success.
            private SimulationInput? Classify(SimulationInput.Received received)
            {
                if (State.Phase != WorkflowPhase.Awaiting || State.Step is null)
                {
                    return null;
                }

                WorkflowStep step = Current;
                if (State.Status == WorkflowStatus.Compensating)
                {
                    return step.Compensate?.Await?.Success.On == received.Type ? SimulationInput.Succeed(received.Data) : null;
                }

                if (State.Status != WorkflowStatus.Running || step.Await is null)
                {
                    return null;
                }

                if (step.Await.Success.On == received.Type)
                {
                    return SimulationInput.Succeed(received.Data);
                }

                return step.Await.Failures.Any(f => f.On == received.Type) ? SimulationInput.FailWith(received.Type, received.Data) : null;
            }

            private SimulationInput? Classify(SimulationInput.Rejection rejection)
            {
                if (State.Phase != WorkflowPhase.Awaiting || State.Step is null)
                {
                    return null;
                }

                WorkflowStep step = Current;
                bool compensating = State.Status == WorkflowStatus.Compensating;
                string? awaited = compensating ? step.Compensate?.Send?.Command
                    : State.Status == WorkflowStatus.Running ? step.Send?.Command : null;
                if (awaited != rejection.Command)
                {
                    return null;
                }

                // The current attempt's command has a deterministic id; a rejection of any other attempt is late (CR-33).
                if (rejection.Cause is { } cause)
                {
                    Guid current = compensating
                        ? WorkflowIds.Compensation(Workflow.ProcessName, State.Instance, step.Id, State.Attempt)
                        : WorkflowIds.Send(Workflow.ProcessName, State.Instance, step.Id, State.Attempt);
                    if (cause != current)
                    {
                        return new SimulationInput.Ignore(rejection + ": a late reply to an earlier attempt");
                    }
                }

                return SimulationInput.Reject(rejection.Code, rejection.Message);
            }

            // ------------------------------------------------------------ steps

            public void Enter(int index)
            {
                while (true)
                {
                    if (index < 0 || index >= Workflow.Steps.Count)
                    {
                        Complete();
                        return;
                    }

                    WorkflowStep step = Workflow.Steps[index];
                    if (step.When is { } when && !Guard(step, when))
                    {
                        Effect(SimulationEffect.Skip, step.Id);
                        index++;
                        continue;
                    }

                    State.Step = step.Id;
                    NextAttempt(step.Id);
                    State._retries.Clear();
                    Attempt(step);
                    return;
                }
            }

            private bool Guard(WorkflowStep step, string when)
            {
                if (State.Options.Guard?.Invoke(step.Id) is { } forced)
                {
                    return forced;
                }

                return GuardParser.Parse(when).Expression is { } guard && GuardEvaluator.Evaluate(guard, DataValue);
            }

            private object? DataValue(string path)
            {
                return path.StartsWith("$data.", StringComparison.Ordinal) ? Read(State.Data, path.Substring(6)) : null;
            }

            private void Attempt(WorkflowStep step)
            {
                State.Phase = WorkflowPhase.Awaiting;
                if (step.Send is { } send)
                {
                    Guid id = WorkflowIds.Send(Workflow.ProcessName, State.Instance, step.Id, State.Attempt);
                    SentCommand command = Command(send, id, first: Workflow.Steps.Count > 0 && Workflow.Steps[0] == step);
                    Effect(SimulationEffect.Send, command.ToString(), command);
                }

                Arm(step.Timeout, step.Id);
            }

            private void Arm(TimeoutSpec? timeout, string step)
            {
                State.Timer = null;
                if (timeout is { None: false, After: { } after })
                {
                    Schedule(new SimulationTimer(SimulationTimer.Timeout, step, State.Attempt, State.Now + after));
                }
            }

            private void Schedule(SimulationTimer timer)
            {
                State.Timer = timer;
                Effect(SimulationEffect.Schedule, timer.Kind + " " + timer.Step + "#" + timer.Attempt + " at +" + IsoDuration.Format(timer.Due - State.Now), timer: timer);
            }

            private void CancelTimer()
            {
                if (State.Timer is { } timer)
                {
                    Effect(SimulationEffect.Cancel, timer.Kind + " " + timer.Step + "#" + timer.Attempt, timer: timer);
                    State.Timer = null;
                }
            }

            private WorkflowStep Current => Workflow.Step(State.Step!)!;

            private void StepOutcome(SimulationInput.Outcome outcome)
            {
                WorkflowStep step = Current;
                AwaitSpec wait = step.Await!;
                if (outcome.Kind == "success")
                {
                    if (wait.Success.When is { } when && GuardParser.Parse(when).Expression is { } guard
                        && State.Options.Guard?.Invoke(SimulationOptions.SuccessGuard + step.Id) != true
                        && !GuardEvaluator.Evaluate(guard, path => path.StartsWith("event.data.", StringComparison.Ordinal) ? Read(outcome.Data, path.Substring(11)) : DataValue(path)))
                    {
                        Effect(SimulationEffect.Ignored, wait.Success.On + ": its 'when' is false");
                        return;
                    }

                    foreach (CaptureSpec capture in wait.Success.Capture)
                    {
                        object? value = Read(outcome.Data, capture.Path.Substring("event.data.".Length));
                        State._data[capture.Field] = value;
                        Effect(SimulationEffect.Capture, capture.Field + " = " + SentCommand.Format(value));
                    }

                    CancelTimer();
                    Transition(SimulationTransition.Success, step.Id);
                    if (step.Pivot)
                    {
                        State.PivotPassed = true;
                    }

                    if (step.Compensate is { None: false })
                    {
                        State._stack.Add(step.Id);
                    }

                    Next(step);
                    return;
                }

                int index = -1;
                for (int i = 0; i < wait.Failures.Count; i++)
                {
                    FailureSpec candidate = wait.Failures[i];
                    if ((outcome.Kind == "rejected" && candidate.Rejected == outcome.Value) || (outcome.Kind == "event" && candidate.On == outcome.Value))
                    {
                        index = i;
                        break;
                    }
                }

                CancelTimer();
                if (index < 0)
                {
                    // Not declared: an unexpected business outcome needs an operator.
                    State.Failure = new WorkflowFailure(outcome.Value ?? outcome.Kind, outcome.Message ?? "unhandled " + outcome.Kind, step.Id);
                    Transition(SimulationTransition.Failure, step.Id);
                    Manual("unhandled " + outcome);
                    return;
                }

                FailureSpec failure = wait.Failures[index];
                string key = "failure:" + index.ToString(CultureInfo.InvariantCulture);
                if (failure.Retry is { } retry && Used(key) < retry.Max)
                {
                    Retry(step, key, retry);
                    return;
                }

                Transition(SimulationTransition.Failure, step.Id);
                Then(failure.Then, new WorkflowFailure(outcome.Value ?? outcome.Kind, outcome.Message ?? outcome.ToString(), step.Id));
            }

            private int Used(string key)
            {
                return State._retries.TryGetValue(key, out int used) ? used : 0;
            }

            private void Retry(WorkflowStep step, string key, RetrySpec retry)
            {
                int used = Used(key) + 1;
                State._retries[key] = used;
                TimeSpan delay = retry.Delay(used);
                NextAttempt(step.Id);
                Transition(SimulationTransition.Retry, step.Id);
                if (delay > TimeSpan.Zero)
                {
                    State.Phase = WorkflowPhase.Retrying;
                    Schedule(new SimulationTimer(SimulationTimer.Retry, step.Id, State.Attempt, State.Now + delay));
                }
                else
                {
                    Attempt(step);
                }
            }

            private void Next(WorkflowStep step)
            {
                if (step.Next == WorkflowStep.CompleteKeyword)
                {
                    Complete();
                    return;
                }

                int index = step.Next is { } next ? IndexOf(next) : IndexOf(step.Id) + 1;
                Enter(index);
            }

            private int IndexOf(string id)
            {
                for (int i = 0; i < Workflow.Steps.Count; i++)
                {
                    if (Workflow.Steps[i].Id == id)
                    {
                        return i;
                    }
                }

                return -1;
            }

            private void Then(ThenSpec then, WorkflowFailure failure)
            {
                switch (then.Kind)
                {
                    case ThenKind.Fail:
                        State.Failure = failure;
                        BeginCompensation();
                        return;
                    case ThenKind.Manual:
                        State.Failure = failure;
                        Manual(failure.Code + " at " + failure.Step);
                        return;
                    case ThenKind.Complete:
                        Complete();
                        return;
                    default:
                        Enter(IndexOf(then.Target!));
                        return;
                }
            }

            private void Timer(SimulationInput.TimerInput input)
            {
                SimulationTimer? pending = State.Timer;
                if (pending is null
                    || (input.Kind is { } kind && (kind != pending.Kind || input.Step != pending.Step || input.Attempt != pending.Attempt))
                    || (input.Due is { } due && due != pending.Due))
                {
                    Effect(SimulationEffect.Ignored, "stale " + (input.Kind is null ? "timer" : input.Kind + " " + input.Step + "#" + input.Attempt));
                    return;
                }

                State.Timer = null;
                if (pending.Due > State.Now)
                {
                    State.Now = pending.Due;
                }

                if (State.Status == WorkflowStatus.Compensating)
                {
                    CompensationTimer(pending);
                    return;
                }

                WorkflowStep step = Current;
                if (pending.Kind == SimulationTimer.Retry)
                {
                    Attempt(step);
                    return;
                }

                // A timeout of the current attempt.
                Transition(SimulationTransition.Timeout, step.Id);
                TimeoutSpec timeout = step.Timeout!;
                if (timeout.Retry is { } retry && Used("timeout") < retry.Max)
                {
                    Retry(step, "timeout", retry);
                    return;
                }

                Transition(SimulationTransition.Failure, step.Id);
                Then(timeout.Then, new WorkflowFailure("timeout", step.Id + " timed out", step.Id));
            }

            // ------------------------------------------------------------ compensation

            private void BeginCompensation()
            {
                if (State.PivotPassed)
                {
                    // After the point of no return the completed work stays: an operator recovers forward (CR-20).
                    Manual("compensation after the pivot is refused (forward recovery only): " + (State.Failure?.Code ?? "failure") + " at " + State.Step);
                    return;
                }

                Status(WorkflowStatus.Compensating);
                CompensateTop();
            }

            private void CompensateTop()
            {
                State.Timer = null;
                if (State._stack.Count == 0)
                {
                    State.Step = null;
                    if (State.Aborting)
                    {
                        Status(WorkflowStatus.Aborted);
                    }
                    else
                    {
                        Status(WorkflowStatus.Compensated);
                        if (Workflow.On.Compensated is { } send)
                        {
                            SentCommand command = Command(send, WorkflowIds.Terminal(Workflow.ProcessName, State.Instance, "compensated"), first: false);
                            Effect(SimulationEffect.Send, command.ToString(), command);
                        }
                    }

                    return;
                }

                State.Step = State._stack[State._stack.Count - 1];
                NextAttempt("compensate:" + State.Step);
                State._retries.Clear();
                Transition(SimulationTransition.Compensate, State.Step);
                SendCompensation();
            }

            private void SendCompensation()
            {
                CompensateSpec compensate = Workflow.Step(State.Step!)!.Compensate!;
                State.Phase = WorkflowPhase.Awaiting;
                Guid id = WorkflowIds.Compensation(Workflow.ProcessName, State.Instance, State.Step!, State.Attempt);
                SentCommand command = Command(compensate.Send!, id, first: false);
                Effect(SimulationEffect.Send, command.ToString(), command);
                State.Timer = null;
                if (compensate.Timeout is { None: false, After: { } after })
                {
                    Schedule(new SimulationTimer(SimulationTimer.CompensateTimeout, SimulationTimer.CompensationStep(State.Step!), State.Attempt, State.Now + after));
                }
            }

            private void CompensationOutcome(SimulationInput.Outcome outcome)
            {
                if (State.Phase != WorkflowPhase.Awaiting)
                {
                    Effect(SimulationEffect.Ignored, outcome + " while waiting to retry the compensation");
                    return;
                }

                CancelTimer();
                if (outcome.Kind == "success")
                {
                    State._stack.RemoveAt(State._stack.Count - 1);
                    CompensateTop();
                    return;
                }

                CompensationFailed(outcome.ToString());
            }

            private void CompensationTimer(SimulationTimer timer)
            {
                if (timer.Kind == SimulationTimer.Retry)
                {
                    SendCompensation();
                    return;
                }

                Transition(SimulationTransition.Timeout, State.Step);
                CompensationFailed("timeout");
            }

            private void CompensationFailed(string why)
            {
                CompensateSpec compensate = Workflow.Step(State.Step!)!.Compensate!;
                if (compensate.Retry is { } retry && Used("compensation") < retry.Max)
                {
                    int used = Used("compensation") + 1;
                    State._retries["compensation"] = used;
                    NextAttempt("compensate:" + State.Step);
                    Transition(SimulationTransition.Retry, State.Step);
                    TimeSpan delay = retry.Delay(used);
                    if (delay > TimeSpan.Zero)
                    {
                        State.Phase = WorkflowPhase.Retrying;
                        Schedule(new SimulationTimer(SimulationTimer.Retry, SimulationTimer.CompensationStep(State.Step!), State.Attempt, State.Now + delay));
                    }
                    else
                    {
                        SendCompensation();
                    }

                    return;
                }

                // The stack is kept: an operator can resume the compensation.
                Manual("the compensation of " + State.Step + " failed (" + why + ")");
            }

            // ------------------------------------------------------------ terminal and operators

            private void Complete()
            {
                State.Timer = null;
                Transition(SimulationTransition.Complete, State.Step);
                State.Step = null;
                Status(WorkflowStatus.Completed);
                if (Workflow.On.Completed is { } send)
                {
                    SentCommand command = Command(send, WorkflowIds.Terminal(Workflow.ProcessName, State.Instance, "completed"), first: false);
                    Effect(SimulationEffect.Send, command.ToString(), command);
                }
            }

            private void Manual(string why)
            {
                bool compensating = State.Status == WorkflowStatus.Compensating;
                Transition(SimulationTransition.Manual, State.Step);
                Status(WorkflowStatus.ManualIntervention, why);
                State.Aborting &= compensating;
                State.Phase = WorkflowPhase.Awaiting;
                State.Timer = null;
                State.CompensationPending = compensating;
                if (Workflow.On.ManualAlert)
                {
                    Effect(SimulationEffect.Alert, why);
                }
            }

            private void Operator(SimulationInput.Operator op)
            {
                switch (op.Action)
                {
                    case "abort":
                        CancelTimer();
                        State.Failure ??= new WorkflowFailure("aborted", op.Argument ?? "aborted by an operator", State.Step ?? string.Empty);
                        State.CompensationPending = false;
                        if (op.Compensate)
                        {
                            if (State.PivotPassed)
                            {
                                Effect(SimulationEffect.Audit, "an operator abort compensates after the pivot: " + (op.Argument ?? "no reason given"));
                            }

                            State.Aborting = true;
                            Status(WorkflowStatus.Compensating);
                            CompensateTop();
                        }
                        else
                        {
                            State.Step = null;
                            Status(WorkflowStatus.Aborted);
                        }

                        return;
                    case "resume":
                    case "retry-step":
                        if (State.Status != WorkflowStatus.ManualIntervention)
                        {
                            Effect(SimulationEffect.Ignored, op.Action + " needs ManualIntervention (the instance is " + State.Status + ")");
                            return;
                        }

                        bool compensating = State.CompensationPending;
                        State.CompensationPending = false;
                        if (compensating)
                        {
                            Status(WorkflowStatus.Compensating);
                            if (op.Action == "retry-step" && State.Step is not null)
                            {
                                NextAttempt("compensate:" + State.Step);
                                SendCompensation();
                            }
                            else
                            {
                                CompensateTop();
                            }

                            return;
                        }

                        Status(WorkflowStatus.Running);
                        if (op.Action == "retry-step" && State.Step is not null)
                        {
                            NextAttempt(State.Step);
                            Attempt(Current);
                        }
                        else
                        {
                            int index = IndexOf(op.Argument ?? State.Step ?? string.Empty);
                            if (index < 0)
                            {
                                Effect(SimulationEffect.Ignored, "resume: no step '" + op.Argument + "'");
                                Status(WorkflowStatus.ManualIntervention);
                                return;
                            }

                            if (State.PivotPassed && Workflow.PivotIndex >= 0 && index < Workflow.PivotIndex)
                            {
                                Effect(SimulationEffect.Ignored, "resume: '" + Workflow.Steps[index].Id + "' is before the pivot, which has passed (forward recovery only)");
                                Status(WorkflowStatus.ManualIntervention);
                                return;
                            }

                            State.Step = Workflow.Steps[index].Id;
                            NextAttempt(State.Step);
                            State._retries.Clear();
                            Attempt(Workflow.Steps[index]);
                        }

                        return;
                }
            }

            private void Status(WorkflowStatus status, string? why = null)
            {
                if (State.Status != status)
                {
                    Effect(SimulationEffect.Status, status + (why is null ? string.Empty : ": " + why));
                    State.Status = status;
                }
            }

            private SentCommand Command(SendSpec send, Guid id, bool first)
            {
                var fields = new List<KeyValuePair<string, object?>>();
                foreach (MapEntry entry in send.Map)
                {
                    fields.Add(new KeyValuePair<string, object?>(entry.Field, Value(entry.Value, first)));
                }

                return new SentCommand(send.Command, id, fields);
            }

            private object? Value(MapValue value, bool first)
            {
                switch (value.Kind)
                {
                    case MapValueKind.Literal:
                        return Literal(value.Literal!);
                    case MapValueKind.Hook:
                        return State.Options.Hook?.Invoke(value.Text!, State.Data) ?? "<hook:" + value.Text + ">";
                }

                string path = value.Text!;
                if (path.StartsWith("$data.", StringComparison.Ordinal))
                {
                    return Read(State.Data, path.Substring(6));
                }

                if (path.StartsWith("$failure.", StringComparison.Ordinal))
                {
                    WorkflowFailure? failure = State.Failure;
                    switch (path.Substring(9))
                    {
                        case "code": return failure?.Code;
                        case "message": return failure?.Message;
                        default: return failure?.Step;
                    }
                }

                return first ? Read(State.StartData, path.Substring("event.data.".Length)) : null;
            }
        }
    }
}

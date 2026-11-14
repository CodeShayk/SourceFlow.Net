using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SourceFlow.Diagnostics;
using SourceFlow.Messaging;
using SourceFlow.Storage;
using SourceFlow.Workflows;

namespace SourceFlow.Runtime;

/// <summary>
/// Runs a process manager reaction inside a storage session (ADR-020, doc 03 §6.1): load or start the instance, react,
/// save the new state with optimistic concurrency, and queue its commands and timers in the outbox. Completed
/// instances ignore later messages. A DSL workflow also writes its <see cref="WorkflowInstanceSummary"/> document in the
/// same transaction, and its transitions are recorded once the transaction commits (doc 15 §7).
/// </summary>
internal sealed class ProcessManagerRunner(SourceFlowRuntime runtime)
{
    // doc 15 §5.6: both versions of a workflow stay registered; new instances start on the latest, in-flight instances
    // finish on the version they started on. These are the workflow versions with a later version registered.
    private readonly HashSet<string> _superseded = Superseded(runtime.Model.ProcessManagers);

    // The other registered versions of each workflow: an instance belongs to the version it started on.
    private readonly Dictionary<string, string[]> _siblings = Siblings(runtime.Model.ProcessManagers);

    // A workflow version starts an instance only when it is the latest registered version and no other version has
    // that instance already (a newly deployed version's subscription also sees the start events of earlier instances).
    // Starting claims the instance under a key shared by every version (CR-34): a v1 node and a v2 node that both see the
    // start event race on that one row, so only one of them starts it; the other retries and finds the claim.
    private async ValueTask<bool> MayStartAsync(IStorageSession session, ProcessManagerDescriptor process, string instanceId, CancellationToken cancellationToken)
    {
        if (_superseded.Contains(process.Name))
        {
            return false;
        }

        if (ClaimOf(process) is { } claim
            && await session.LoadProcessStateAsync(claim, instanceId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return false;
        }

        foreach (string sibling in _siblings.GetValueOrDefault(process.Name, []))
        {
            if (await session.LoadProcessStateAsync(sibling, instanceId, cancellationToken).ConfigureAwait(false) is not null)
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, string[]> Siblings(IEnumerable<ProcessManagerDescriptor> processes)
    {
        (string Process, string Name)[] workflows =
        [
            .. processes.Where(p => p.IsWorkflow)
                .Select(p => (p.Name, WorkflowRuntime.TryParseProcessName(p.Name, out string? name, out _) ? name : p.Name)),
        ];
        return workflows.ToDictionary(w => w.Process, w => workflows.Where(o => o.Name == w.Name && o.Process != w.Process).Select(o => o.Process).ToArray(), StringComparer.Ordinal);
    }

    public static bool Handles(ProcessManagerDescriptor process, Type messageType)
    {
        foreach (Type handled in process.HandledMessages)
        {
            if (handled == messageType)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Applies one message; returns false when the message does not correlate to an instance.</summary>
    public async ValueTask<bool> ReactAsync(IStorageSession session, ProcessManagerDescriptor process, IMessage message, MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        return (await ReactCoreAsync(session, process, message, envelope, cancellationToken).ConfigureAwait(false)).Correlated;
    }

    /// <summary>
    /// Applies one message and says what happened: whether it correlated, to which instance, and the reaction applied
    /// (null when the instance ignored it, is completed, or may not start). The specs run this path (doc 11 §2, CR-44).
    /// </summary>
    internal async ValueTask<ReactOutcome> ReactCoreAsync(IStorageSession session, ProcessManagerDescriptor process, IMessage message, MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!process.TryCorrelate(message, envelope.CorrelationId, out string? instanceId))
        {
            return new ReactOutcome(false, null, null);
        }

        using Activity? activity = SourceFlowTelemetry.ActivitySource.StartActivity("react " + process.Name, ActivityKind.Internal);
        activity?.SetTag(SourceFlowTelemetry.Tags.ProcessId, instanceId);
        JsonTypeInfo typeInfo = runtime.Model.JsonTypeInfoFor(process.StateType);
        ProcessState? stored = await session.LoadProcessStateAsync(process.Name, instanceId, cancellationToken).ConfigureAwait(false);
        if (stored is { Completed: true } || (stored is null && process.IsWorkflow && !await MayStartAsync(session, process, instanceId, cancellationToken).ConfigureAwait(false)))
        {
            return new ReactOutcome(true, instanceId, null);
        }

        object state = stored is null ? process.Initial : JsonSerializer.Deserialize(stored.State.Span, typeInfo)!;
        Reaction reaction = process.React(state, message, instanceId, runtime.Time.GetUtcNow(), envelope.MessageId, isNew: stored is null, runtime.Model.Registry.Options, envelope.CausationId);
        if (reaction.IsIgnored)
        {
            return new ReactOutcome(true, instanceId, null);
        }

        var correlation = new CorrelationId(instanceId);
        object next = reaction.NewState ?? state;
        if (next.GetType() != process.StateType)
        {
            throw new InvalidOperationException($"Process manager {process.Name} became {next.GetType().Name}; it must stay {process.StateType.Name}.");
        }

        long version = stored?.Version ?? 0;
        session.SaveProcessState(process.Name, instanceId, version, JsonSerializer.SerializeToUtf8Bytes(next, typeInfo), reaction.Completes);
        if (stored is null && ClaimOf(process) is { } claim)
        {
            // A new instance: claimed for every version of the workflow, in the same transaction (expected version 0).
            session.SaveProcessState(claim, instanceId, 0, System.Text.Encoding.UTF8.GetBytes("{\"process\":\"" + process.Name + "\"}"), completed: true);
        }
        for (int i = 0; i < reaction.Commands.Count; i++)
        {
            runtime.EnqueueCommand(session, reaction.Commands[i], envelope, correlation, replyOnReject: true, messageId: reaction.CommandIds[i]);
        }

        foreach (ScheduledMessage scheduled in reaction.Scheduled)
        {
            runtime.EnqueueScheduled(session, scheduled.Message, scheduled.DeliverAt, envelope, correlation);
        }

        if (next is IWorkflowInstance workflow)
        {
            Workflow(session, process, instanceId, version, workflow, reaction);
        }

        activity?.SetTag("sourceflow.process.completed", reaction.Completes);
        return new ReactOutcome(true, instanceId, reaction);
    }

    /// <summary>The process type under which every version of a workflow claims its instances (CR-34).</summary>
    internal const string ClaimPrefix = "workflow-claim:";

    // The shared claim key of a DSL workflow version, or null for other process managers.
    private static string? ClaimOf(ProcessManagerDescriptor process)
    {
        return process.IsWorkflow && WorkflowRuntime.TryParseProcessName(process.Name, out string? name, out _) ? ClaimPrefix + name : null;
    }

    private static HashSet<string> Superseded(IEnumerable<ProcessManagerDescriptor> processes)
    {
        var latest = new Dictionary<string, int>(StringComparer.Ordinal);
        var versions = new List<(string Process, string Name, int Version)>();
        foreach (ProcessManagerDescriptor process in processes)
        {
            if (process.IsWorkflow && WorkflowRuntime.TryParseProcessName(process.Name, out string? name, out int version))
            {
                versions.Add((process.Name, name, version));
                latest[name] = Math.Max(version, latest.GetValueOrDefault(name));
            }
        }

        return [.. versions.Where(v => v.Version < latest[v.Name]).Select(v => v.Process)];
    }

    // The instance's read-model document, and its transitions as spans now and metrics after the commit.
    private void Workflow(IStorageSession session, ProcessManagerDescriptor process, string instanceId, long version, IWorkflowInstance workflow, Reaction reaction)
    {
        if (!WorkflowRuntime.TryParseProcessName(process.Name, out string? name, out int workflowVersion))
        {
            return;
        }

        string instance = WorkflowInstanceId.TryParse(instanceId, out WorkflowInstanceId id) ? id.Value : instanceId;
        if (workflow.Describe() is { } info)
        {
            WorkflowInstanceSummary summary = WorkflowRuntime.Summarize(process.Name, instance, info);
            session.Documents.Upsert(WorkflowInstanceSummary.Collection, WorkflowInstanceSummary.DocumentId(process.Name, instance), version + 1, WorkflowRuntime.SerializeSummary(summary));
        }

        if (reaction.Transitions.Count == 0)
        {
            return;
        }

        foreach (WorkflowTransition transition in reaction.Transitions)
        {
            using Activity? span = SourceFlowTelemetry.ActivitySource.StartActivity(
                "workflow " + name + (transition.Step is null ? string.Empty : " " + transition.Step), ActivityKind.Internal);
            span?.SetTag(SourceFlowTelemetry.Tags.WorkflowStep, transition.Step);
            span?.SetTag(SourceFlowTelemetry.Tags.WorkflowAttempt, transition.Attempt);
            span?.SetTag(SourceFlowTelemetry.Tags.WorkflowTransition, transition.Kind);
        }

        string versionTag = workflowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        IReadOnlyList<WorkflowTransition> transitions = reaction.Transitions;
        SourceFlowMetrics metrics = runtime.Metrics;
        runtime.AfterCommit(session, () =>
        {
            foreach (WorkflowTransition transition in transitions)
            {
                metrics.WorkflowTransitions.Add(1,
                    new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.Workflow, name),
                    new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.WorkflowVersion, versionTag),
                    new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.TransitionKind, transition.Kind));
                if (transition.Kind == WorkflowTransition.Manual)
                {
                    metrics.WorkflowManualInterventions.Add(1,
                        new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.Workflow, name),
                        new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.WorkflowVersion, versionTag));
                }
            }
        });
    }
}

/// <summary>What one process-manager reaction did: correlated or not, the instance, and the applied reaction (or null).</summary>
internal readonly record struct ReactOutcome(bool Correlated, string? InstanceId, Reaction? Reaction);

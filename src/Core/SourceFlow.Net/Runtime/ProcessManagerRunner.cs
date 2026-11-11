using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SourceFlow.Diagnostics;
using SourceFlow.Messaging;
using SourceFlow.Storage;

namespace SourceFlow.Runtime;

/// <summary>
/// Runs a process manager reaction inside a storage session (ADR-020, doc 03 §6.1): load or start the instance, react,
/// save the new state with optimistic concurrency, and queue its commands and timers in the outbox. Completed
/// instances ignore later messages.
/// </summary>
internal sealed class ProcessManagerRunner(SourceFlowRuntime runtime)
{
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
        if (!process.TryCorrelate(message, envelope.CorrelationId, out string? instanceId))
        {
            return false;
        }

        using Activity? activity = SourceFlowTelemetry.ActivitySource.StartActivity("react " + process.Name, ActivityKind.Internal);
        activity?.SetTag(SourceFlowTelemetry.Tags.ProcessId, instanceId);
        JsonTypeInfo typeInfo = runtime.Model.JsonTypeInfoFor(process.StateType);
        ProcessState? stored = await session.LoadProcessStateAsync(process.Name, instanceId, cancellationToken).ConfigureAwait(false);
        if (stored is { Completed: true })
        {
            return true;
        }

        object state = stored is null ? process.Initial : JsonSerializer.Deserialize(stored.State.Span, typeInfo)!;
        Reaction reaction = process.React(state, message, instanceId, runtime.Time.GetUtcNow(), envelope.MessageId, isNew: stored is null);
        if (reaction.IsIgnored)
        {
            return true;
        }

        var correlation = new CorrelationId(instanceId);
        object next = reaction.NewState ?? state;
        if (next.GetType() != process.StateType)
        {
            throw new InvalidOperationException($"Process manager {process.Name} became {next.GetType().Name}; it must stay {process.StateType.Name}.");
        }

        session.SaveProcessState(process.Name, instanceId, stored?.Version ?? 0, JsonSerializer.SerializeToUtf8Bytes(next, typeInfo), reaction.Completes);
        foreach (ICommand command in reaction.Commands)
        {
            runtime.EnqueueCommand(session, command, envelope, correlation, replyOnReject: true);
        }

        foreach (ScheduledMessage scheduled in reaction.Scheduled)
        {
            runtime.EnqueueScheduled(session, scheduled.Message, scheduled.DeliverAt, envelope, correlation);
        }

        activity?.SetTag("sourceflow.process.completed", reaction.Completes);
        return true;
    }
}

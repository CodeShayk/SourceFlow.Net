using SourceFlow.Messaging;
using SourceFlow.Pipeline;
using SourceFlow.Storage;

namespace SourceFlow.Runtime;

/// <summary>
/// Routes a received message to its consumers (doc 04 §5): commands to the command processor; events, integration
/// events and timeouts to the process managers and event handlers that react to them. Each consumer runs in its own
/// transaction with its own inbox entry (<c>{service}:pm:{name}</c>, <c>{service}:handler:{name}</c>), so a redelivery
/// only re-runs the consumers that failed.
/// </summary>
internal sealed class MessageDispatcher(SourceFlowRuntime runtime, CommandProcessor commands)
{
    private readonly ProcessManagerRunner _processes = new(runtime);

    /// <summary>The terminal step of the receive pipeline.</summary>
    public async ValueTask DispatchAsync(MessageContext context)
    {
        MessageEnvelope envelope = context.Envelope;
        MessageTypeInfo type = runtime.Model.Registry.TryGet(envelope.Type, envelope.TypeVersion, out MessageTypeInfo? info)
            ? info
            : throw new UnknownMessageTypeException(envelope.Type, envelope.TypeVersion);
        context.TypeInfo = type;
        object message = runtime.Serializer.Deserialize(envelope.Body, type); // wire messages are never upcast
        context.Message = message;

        if (message is ICommand command)
        {
            CommandResult result = await commands.ProcessAsync(command, envelope, keyed: true, expectedVersion: null, context.Services, context.CancellationToken).ConfigureAwait(false);
            context.Result = result;
            context.Outcome = result.Status switch
            {
                CommandStatus.Rejected => HandlingOutcome.Rejected,
                CommandStatus.Duplicate => HandlingOutcome.Duplicate,
                CommandStatus.Conflict => throw new ConcurrencyException($"Command {envelope.MessageId} still conflicts after {runtime.Options.MaxConcurrencyRetries} re-decides."),
                _ => HandlingOutcome.Completed,
            };
            return;
        }

        var imessage = (IMessage)message;
        foreach (ProcessManagerDescriptor process in runtime.Model.ProcessManagers)
        {
            if (ProcessManagerRunner.Handles(process, message.GetType()))
            {
                await ConsumeAsync($"{runtime.ServiceName}:pm:{process.Name}", envelope, context.CancellationToken,
                    session => _processes.ReactAsync(session, process, imessage, envelope, context.CancellationToken)).ConfigureAwait(false);
            }
        }

        foreach (EventHandlerDescriptor handler in runtime.Model.EventHandlers)
        {
            if (handler.EventType == message.GetType())
            {
                EventContext eventContext = ContextOf(envelope);
                await ConsumeAsync($"{runtime.ServiceName}:handler:{handler.Name}", envelope, context.CancellationToken, async _ =>
                {
                    await handler.HandleAsync(context.Services, imessage, eventContext, context.CancellationToken).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
            }
        }

        context.Outcome = HandlingOutcome.Completed;
    }

    private async ValueTask ConsumeAsync(string consumer, MessageEnvelope envelope, CancellationToken cancellationToken, Func<IStorageSession, ValueTask<bool>> consume)
    {
        await using IStorageSession session = await runtime.Storage.Sessions.BeginAsync(envelope.TenantId, cancellationToken).ConfigureAwait(false);
        if (await session.InboxSeenAsync(consumer, envelope.MessageId, cancellationToken).ConfigureAwait(false) is not null)
        {
            runtime.Metrics.InboxDuplicates.Add(1, new KeyValuePair<string, object?>(Diagnostics.SourceFlowTelemetry.Tags.ConsumerGroup, consumer));
            return;
        }

        await consume(session).ConfigureAwait(false);
        session.RecordInbox(consumer, envelope.MessageId, new InboxResult(CommandStatus.Accepted, StreamVersion.None, null));
        try
        {
            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DuplicateMessageException)
        {
            // A concurrent redelivery committed first; this one is a duplicate.
        }
    }

    private static EventContext ContextOf(MessageEnvelope envelope)
    {
        StreamId stream = envelope.Subject is { Length: > 0 } subject ? new StreamId(subject) : new StreamId(envelope.Source + "-" + envelope.MessageId.Value.ToString("N"));
        return new EventContext(envelope.MessageId, stream, new StreamVersion(envelope.StreamVersion ?? 0), GlobalPosition.Start, envelope.Time,
            envelope.CorrelationId, envelope.CausationId, envelope.TenantId == MessageEnvelope.DefaultTenant ? null : envelope.TenantId);
    }
}

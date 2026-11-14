using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Messaging;
using SourceFlow.Pipeline;
using SourceFlow.Storage;

namespace SourceFlow.Runtime;

/// <summary>
/// Sends commands to their single owner (ADR-007). Every outbound command goes through the outbox (ADR-006, doc 03
/// §5.2), so it is protected, size-checked and retried by the relay like any other outbound message:
/// <list type="bullet">
/// <item>Inside a handler (an event handler, a process manager, a command handler) the command joins the handler's own
/// transaction (<see cref="AmbientSend"/>): it is sent only if the handler commits, and a redelivery sends it again
/// under the same id.</item>
/// <item>Outside one, a command owned here runs through the local command processor and returns its result; a routed
/// command is written to the outbox in a session of its own and returns <see cref="CommandStatus.Dispatched"/>.</item>
/// </list>
/// </summary>
internal sealed class CommandSender(SourceFlowRuntime runtime, CommandProcessor processor, IServiceScopeFactory scopes) : ICommandSender
{
    public async ValueTask<CommandResult> SendAsync<TCommand>(TCommand command, SendOptions? options = null, CancellationToken cancellationToken = default)
        where TCommand : class, ICommand
    {
        ArgumentNullException.ThrowIfNull(command);
        options ??= new SendOptions();
        EnvelopeLimits.ValidateHeaders(options.Headers);
        Type type = command.GetType();
        bool keyed = options.IdempotencyKey is not null;
        MessageId? id = keyed ? MessageIds.FromKey(runtime.ServiceName, options.IdempotencyKey!) : null;

        if (AmbientSend.Current is { } ambient)
        {
            if (options.ExpectedVersion is not null)
            {
                throw new InvalidOperationException("A command sent from a handler goes through the outbox and is decided later: SendOptions.ExpectedVersion can't be honoured there.");
            }

            (MessageEnvelope envelope, string destination) = runtime.CommandEnvelope(command, ambient.Cause, options.CorrelationId, ambient.Session.TenantId,
                replyOnReject: false, id ?? ambient.NextId(), options.Headers);
            await EnqueueAsync(ambient.Session, command, envelope, destination, cancellationToken).ConfigureAwait(false);
            return new CommandResult(CommandStatus.Dispatched, StreamVersion.None, null, ConsistencyToken.None);
        }

        if (runtime.Model.CommandOwners.ContainsKey(type))
        {
            MessageEnvelope envelope = runtime.CreateEnvelope(command, messageId: id, correlation: options.CorrelationId,
                subject: runtime.TrySubjectOf(command), headers: options.Headers);
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            var context = new MessageContext(PipelineStage.Send | PipelineStage.Handle, envelope, scope.ServiceProvider, cancellationToken)
            {
                Message = command,
                TypeInfo = runtime.Model.Registry.Get(type),
            };
            await runtime.Pipeline.InvokeAsync(context, async ctx =>
            {
                ctx.Result = await processor.ProcessAsync(command, ctx.Envelope, keyed, options.ExpectedVersion, ctx.Services, ctx.CancellationToken).ConfigureAwait(false);
                ctx.Outcome = HandlingOutcome.Completed;
            }).ConfigureAwait(false);
            return context.Result ?? throw new InvalidOperationException("A middleware short-circuited the send without setting a result.");
        }

        if (runtime.Routing.Commands.ContainsKey(type))
        {
            // A session of its own: the relay sends it, with payload protection, the size check and retries (CR-07).
            (MessageEnvelope envelope, string destination) = runtime.CommandEnvelope(command, cause: null, options.CorrelationId, runtime.CurrentTenant,
                replyOnReject: false, id, options.Headers);
            await using IStorageSession session = await runtime.Storage.Sessions.BeginAsync(envelope.TenantId, cancellationToken).ConfigureAwait(false);
            await EnqueueAsync(session, command, envelope, destination, cancellationToken).ConfigureAwait(false);
            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            runtime.Committed(session);
            return new CommandResult(CommandStatus.Dispatched, StreamVersion.None, null, ConsistencyToken.None);
        }

        throw new NoRouteException(type);
    }

    // The Send stage of the pipeline wraps the outbox write: middleware sees (and may enrich) the envelope that is stored.
    private async ValueTask EnqueueAsync(IStorageSession session, ICommand command, MessageEnvelope envelope, string destination, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        var context = new MessageContext(PipelineStage.Send, envelope, scope.ServiceProvider, cancellationToken)
        {
            Message = command,
            TypeInfo = runtime.Model.Registry.Get(command.GetType()),
        };
        await runtime.Pipeline.InvokeAsync(context, ctx =>
        {
            session.EnqueueOutbox(runtime.ToOutbox(ctx.Envelope, destination, OutboxKind.Send));
            ctx.Outcome = HandlingOutcome.Completed;
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
    }
}

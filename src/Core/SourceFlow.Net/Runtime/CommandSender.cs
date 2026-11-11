using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Messaging;
using SourceFlow.Pipeline;

namespace SourceFlow.Runtime;

/// <summary>
/// Sends commands to their single owner (ADR-007): owned commands run through the local command processor; routed
/// commands are dispatched to the remote owner's endpoint (doc 03 §5.1).
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

        if (runtime.Routing.Commands.TryGetValue(type, out CommandRoute? route))
        {
            MessageEnvelope envelope = runtime.CreateEnvelope(command, messageId: id, correlation: options.CorrelationId, headers: options.Headers);
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            var context = new MessageContext(PipelineStage.Send, envelope, scope.ServiceProvider, cancellationToken) { Message = command };
            await runtime.Pipeline.InvokeAsync(context, async ctx =>
            {
                await runtime.Transport.SendAsync(route.Endpoint, [ctx.Envelope], ctx.CancellationToken).ConfigureAwait(false);
                runtime.Metrics.SentMessages.Add(1, new KeyValuePair<string, object?>(Diagnostics.SourceFlowTelemetry.Tags.Destination, route.Endpoint));
                ctx.Outcome = HandlingOutcome.Completed;
            }).ConfigureAwait(false);
            return new CommandResult(CommandStatus.Dispatched, StreamVersion.None, null, ConsistencyToken.None);
        }

        throw new NoRouteException(type);
    }
}

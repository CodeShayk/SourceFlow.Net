using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SourceFlow.Diagnostics;
using SourceFlow.Messaging;
using SourceFlow.Runtime;
using SourceFlow.Storage;

namespace SourceFlow.Pipeline;

/// <summary>Where in the message flow a middleware runs (ADR-012).</summary>
[Flags]
public enum PipelineStage
{
    /// <summary>No stage.</summary>
    None = 0,

    /// <summary>A command is sent (locally or to a remote owner).</summary>
    Send = 1,

    /// <summary>A command is handled by the command processor.</summary>
    Handle = 2,

    /// <summary>A message is received from the transport.</summary>
    Receive = 4,

    /// <summary>A message is published from the outbox.</summary>
    Publish = 8,

    /// <summary>A subscription applies an event (projection, process manager or event handler).</summary>
    Project = 16,

    /// <summary>Every stage.</summary>
    All = Send | Handle | Receive | Publish | Project,
}

/// <summary>The outcome a terminal step reports.</summary>
public enum HandlingOutcome
{
    /// <summary>Not handled yet.</summary>
    None = 0,

    /// <summary>Handled successfully.</summary>
    Completed = 1,

    /// <summary>A business rejection (not retried).</summary>
    Rejected = 2,

    /// <summary>Already processed (inbox).</summary>
    Duplicate = 3,

    /// <summary>A transient failure; retry.</summary>
    Transient = 4,

    /// <summary>A permanent failure; park.</summary>
    Permanent = 5,
}

/// <summary>The per-message context passed through the middleware chain (doc 04 §4).</summary>
public sealed class MessageContext
{
    /// <summary>Creates a context.</summary>
    public MessageContext(PipelineStage stage, MessageEnvelope envelope, IServiceProvider services, CancellationToken cancellationToken)
    {
        Stage = stage;
        Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
        Services = services ?? throw new ArgumentNullException(nameof(services));
        CancellationToken = cancellationToken;
    }

    /// <summary>The stage.</summary>
    public PipelineStage Stage { get; }

    /// <summary>The envelope.</summary>
    public MessageEnvelope Envelope { get; set; }

    /// <summary>The deserialized payload, once known.</summary>
    public object? Message { get; set; }

    /// <summary>The registered type, once resolved.</summary>
    public MessageTypeInfo? TypeInfo { get; set; }

    /// <summary>The per-message DI scope.</summary>
    public IServiceProvider Services { get; }

    /// <summary>The storage session in transactional stages.</summary>
    public IStorageSession? Session { get; set; }

    /// <summary>The current span.</summary>
    public Activity? Activity { get; set; }

    /// <summary>Cancellation.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Set by the terminal step.</summary>
    public HandlingOutcome Outcome { get; set; }

    /// <summary>The exception behind a failed outcome.</summary>
    public Exception? Exception { get; set; }

    /// <summary>The command result, for <see cref="PipelineStage.Send"/> and <see cref="PipelineStage.Handle"/>.</summary>
    public CommandResult? Result { get; set; }

    /// <summary>A per-message bag for middleware.</summary>
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
}

/// <summary>The next step in the chain.</summary>
public delegate ValueTask MessageDelegate(MessageContext context);

/// <summary>One middleware abstraction for every stage (ADR-012).</summary>
public interface IMessageMiddleware
{
    /// <summary>Runs the middleware; call <paramref name="next"/> to continue, or return to short-circuit.</summary>
    ValueTask InvokeAsync(MessageContext context, MessageDelegate next);
}

/// <summary>A middleware registration.</summary>
/// <param name="Factory">Creates the middleware from the per-message scope.</param>
/// <param name="Stages">The stages it applies to.</param>
/// <param name="Filter">An optional predicate on the message context.</param>
public sealed record MiddlewareRegistration(Func<IServiceProvider, IMessageMiddleware> Factory, PipelineStage Stages, Func<MessageContext, bool>? Filter = null);

/// <summary>Composes the built-in and user middleware for a stage, once, and caches the chain (doc 04 §4).</summary>
public sealed class MessagePipeline
{
    private readonly IReadOnlyList<MiddlewareRegistration> _registrations;
    private readonly ConcurrentDictionary<PipelineStage, MiddlewareRegistration[]> _byStage = new();

    /// <summary>Creates the pipeline from the ordered registrations (outermost first).</summary>
    public MessagePipeline(IEnumerable<MiddlewareRegistration> registrations)
    {
        _registrations = [.. registrations];
    }

    /// <summary>Runs <paramref name="terminal"/> wrapped by every middleware registered for the context's stage.</summary>
    public ValueTask InvokeAsync(MessageContext context, MessageDelegate terminal)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(terminal);
        MiddlewareRegistration[] chain = _byStage.GetOrAdd(context.Stage, stage => [.. _registrations.Where(r => (r.Stages & stage) != 0)]);
        return Invoke(chain, 0, context, terminal);
    }

    private static ValueTask Invoke(MiddlewareRegistration[] chain, int index, MessageContext context, MessageDelegate terminal)
    {
        while (index < chain.Length && chain[index].Filter is { } filter && !filter(context))
        {
            index++;
        }

        if (index == chain.Length)
        {
            return terminal(context);
        }

        IMessageMiddleware middleware = chain[index].Factory(context.Services);
        int next = index + 1;
        return middleware.InvokeAsync(context, ctx => Invoke(chain, next, ctx, terminal));
    }
}

/// <summary>Maps exceptions to failure classes (doc 04 §9.1). Configurable through <c>sf.Errors(…)</c>.</summary>
public sealed class ErrorClassifier
{
    private readonly List<(Func<Exception, bool> Match, HandlingOutcome Outcome)> _rules = [];

    /// <summary>Creates the classifier with the default rules.</summary>
    public ErrorClassifier()
    {
        Treat<UnknownMessageTypeException>(HandlingOutcome.Permanent);
        Treat<JsonException>(HandlingOutcome.Permanent);
        Treat<InvalidCommandException>(HandlingOutcome.Permanent);
        Treat<NotSupportedException>(HandlingOutcome.Permanent);
        Treat<FormatException>(HandlingOutcome.Permanent);
        Treat<ArgumentException>(HandlingOutcome.Permanent);
        Treat<InvalidCastException>(HandlingOutcome.Permanent);
        Treat<PermanentException>(HandlingOutcome.Permanent);
        Treat<Security.DecryptionFailedException>(HandlingOutcome.Permanent);
        Treat<Security.ProtectionRequiredException>(HandlingOutcome.Permanent);

        // Broker errors say whether they are transient (outage, throttling) or not (missing destination, oversized body).
        _rules.Insert(0, (e => e is Security.KeyProviderException { IsTransient: false }, HandlingOutcome.Permanent));
        _rules.Insert(0, (e => e is Security.KeyProviderException { IsTransient: true }, HandlingOutcome.Transient));
        _rules.Insert(0, (e => e is Transport.TransportException { IsTransient: false }, HandlingOutcome.Permanent));
        _rules.Insert(0, (e => e is Transport.TransportException { IsTransient: true }, HandlingOutcome.Transient));
    }

    /// <summary>Classifies <typeparamref name="TException"/> (and subclasses); later rules win.</summary>
    public ErrorClassifier Treat<TException>(HandlingOutcome outcome)
        where TException : Exception
    {
        if (outcome is not (HandlingOutcome.Transient or HandlingOutcome.Permanent))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Exceptions are either transient or permanent.");
        }

        _rules.Insert(0, (e => e is TException, outcome));
        return this;
    }

    /// <summary>Classifies an exception; unknown exceptions are transient (retried, then parked).</summary>
    public HandlingOutcome Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Exception root = exception is AggregateException { InnerExceptions.Count: 1 } aggregate ? aggregate.InnerExceptions[0] : exception;
        foreach ((Func<Exception, bool> match, HandlingOutcome outcome) in _rules)
        {
            if (match(root))
            {
                return outcome;
            }
        }

        return HandlingOutcome.Transient;
    }
}

/// <summary>Throw from user code to park a message without retries.</summary>
public sealed class PermanentException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PermanentException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>Built-in middleware 1: span, duration and outcome metrics (doc 08).</summary>
internal sealed class TelemetryMiddleware(SourceFlowMetrics metrics, string consumer, bool parentBasedOnMessage = false) : IMessageMiddleware
{
    public async ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
    {
        if (context.Stage != PipelineStage.Receive)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        ActivityContext parent = default;
        if (context.Envelope.TraceParent is { } traceParent)
        {
            ActivityContext.TryParse(traceParent, context.Envelope.TraceState, isRemote: true, out parent);
        }

        using Activity? activity = SourceFlowTelemetry.ActivitySource.StartActivity(
            ActivityKind.Consumer,
            parentContext: parentBasedOnMessage ? parent : default,
            tags: null,
            links: parent == default ? null : [new ActivityLink(parent)],
            name: "process " + consumer);
        SemanticConventions.SetOperationType(activity, "process");
        activity?.SetTag(SourceFlowTelemetry.Tags.ConsumerGroup, consumer);
        activity?.SetTag(SourceFlowTelemetry.Tags.MessageId, context.Envelope.MessageId.ToString());
        activity?.SetTag(SourceFlowTelemetry.Tags.MessageType, context.Envelope.Type);
        context.Activity = activity;
        long start = Stopwatch.GetTimestamp();
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            string? error = context.Outcome is HandlingOutcome.Transient or HandlingOutcome.Permanent ? context.Exception?.GetType().Name ?? context.Outcome.ToString() : null;
            metrics.ProcessDuration.Record(
                Stopwatch.GetElapsedTime(start).TotalSeconds,
                new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.ConsumerGroup, consumer),
                new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.ErrorType, error));
            activity?.SetTag(SourceFlowTelemetry.Tags.Outcome, context.Outcome.ToString().ToLowerInvariant());
            if (error is not null)
            {
                activity?.SetStatus(ActivityStatusCode.Error, error);
            }
        }
    }
}

/// <summary>Built-in middleware 2: a log scope with message id, type, correlation and tenant.</summary>
internal sealed class LoggingScopeMiddleware(ILogger logger) : IMessageMiddleware
{
    public async ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
    {
        using IDisposable? scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["MessageId"] = context.Envelope.MessageId.ToString(),
            ["MessageType"] = context.Envelope.Type,
            ["CorrelationId"] = context.Envelope.CorrelationId.Value,
            ["TenantId"] = context.Envelope.TenantId,
        });
        await next(context).ConfigureAwait(false);
    }
}

/// <summary>Built-in middleware 3: classifies exceptions into outcomes for the receive and project stages.</summary>
internal sealed class ErrorClassificationMiddleware(ErrorClassifier classifier) : IMessageMiddleware
{
    public async ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !context.CancellationToken.IsCancellationRequested)
        {
            context.Exception = exception;
            context.Outcome = classifier.Classify(exception);
        }
    }
}

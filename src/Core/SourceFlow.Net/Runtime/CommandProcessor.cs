using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SourceFlow.Diagnostics;
using SourceFlow.Messaging;
using SourceFlow.Storage;

namespace SourceFlow.Runtime;

/// <summary>
/// Processes commands owned by this service (doc 03 §5.2): one storage session per attempt; inbox check, load, decide,
/// append (first write, ES-R1), publications to the outbox, inbox record, inline projections, commit. A concurrency
/// conflict re-decides on fresh state with jittered backoff (ADR-008); no side effect happens before commit.
/// </summary>
public sealed class CommandProcessor
{
    private readonly SourceFlowRuntime _runtime;
    private readonly AggregateStore _aggregates;
    private readonly ILogger _logger;

    /// <summary>Creates the processor.</summary>
    public CommandProcessor(SourceFlowRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _aggregates = new AggregateStore(runtime);
        _logger = runtime.Logger<CommandProcessor>();
    }

    /// <summary>The inbox consumer name of command handling: the service name (doc 04 §6).</summary>
    public string Consumer => _runtime.ServiceName;

    /// <summary>Processes a command that this service owns.</summary>
    /// <param name="command">The command.</param>
    /// <param name="envelope">Its envelope (message id = idempotency key when <paramref name="keyed"/>).</param>
    /// <param name="keyed">True when the message id is an idempotency key (keyed sends and received commands).</param>
    /// <param name="expectedVersion">An optional caller-side version check.</param>
    /// <param name="services">The per-message DI scope (command handlers).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async ValueTask<CommandResult> ProcessAsync(
        ICommand command, MessageEnvelope envelope, bool keyed, StreamVersion? expectedVersion, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(envelope);
        Type commandType = command.GetType();
        AggregateDescriptor aggregate = _runtime.Model.CommandOwners.TryGetValue(commandType, out AggregateDescriptor? owner)
            ? owner
            : throw new NoRouteException(commandType);
        _runtime.Model.CommandHandlers.TryGetValue(commandType, out CommandHandlerDescriptor? handler);

        StreamId stream;
        try
        {
            stream = aggregate.StreamOf(command);
        }
        catch (InvalidCommandException invalid)
        {
            return new CommandResult(CommandStatus.Rejected, StreamVersion.None, Error.InvalidCommand(invalid.Message), ConsistencyToken.None);
        }

        using Activity? activity = SourceFlowTelemetry.ActivitySource.StartActivity("handle " + commandType.Name, ActivityKind.Internal);
        activity?.SetTag(SourceFlowTelemetry.Tags.AggregateType, aggregate.Name);
        activity?.SetTag(SourceFlowTelemetry.Tags.StreamId, stream.Value);
        long started = Stopwatch.GetTimestamp();
        CommandResult result = default;
        int attempt = 0;
        try
        {
            for (; ; attempt++)
            {
                try
                {
                    result = await AttemptAsync(command, envelope, keyed, expectedVersion, services, aggregate, handler, stream, cancellationToken).ConfigureAwait(false);
                    return result;
                }
                catch (ConcurrencyException) when (attempt < _runtime.Options.MaxConcurrencyRetries)
                {
                    _aggregates.Invalidate(envelope.TenantId, stream);
                    _runtime.Metrics.ConcurrencyConflicts.Add(1, new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.AggregateType, aggregate.Name));
                    Log.ConcurrencyRetry(_logger, stream.Value, attempt + 1);
                    await Backoff(attempt, cancellationToken).ConfigureAwait(false);
                }
                catch (ConcurrencyException)
                {
                    _aggregates.Invalidate(envelope.TenantId, stream);
                    _runtime.Metrics.ConcurrencyConflicts.Add(1, new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.AggregateType, aggregate.Name));
                    Log.ConcurrencyExhausted(_logger, stream.Value, attempt + 1);
                    result = new CommandResult(CommandStatus.Conflict, StreamVersion.None, Error.ConcurrencyConflict, ConsistencyToken.None);
                    return result;
                }
                catch (DuplicateMessageException) when (keyed)
                {
                    // A concurrent duplicate committed first: return its stored result.
                    _runtime.Metrics.InboxDuplicates.Add(1, new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.ConsumerGroup, Consumer));
                    result = await StoredResultAsync(envelope, cancellationToken).ConfigureAwait(false);
                    return result;
                }
            }
        }
        finally
        {
            string outcome = result.Status == default ? "failed" : result.Status.ToString().ToLowerInvariant();   // default: an exception (the availability SLO counts these)
            _runtime.Metrics.CommandDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.CommandType, commandType.Name),
                new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.Outcome, outcome));
            activity?.SetTag(SourceFlowTelemetry.Tags.Outcome, outcome);
            activity?.SetTag(SourceFlowTelemetry.Tags.ConcurrencyRetries, attempt);
            Log.CommandHandled(_logger, commandType.Name, stream.Value, outcome, result.Version.Value);
        }
    }

    private async ValueTask<CommandResult> AttemptAsync(
        ICommand command, MessageEnvelope envelope, bool keyed, StreamVersion? expectedVersion, IServiceProvider services,
        AggregateDescriptor aggregate, CommandHandlerDescriptor? handler, StreamId stream, CancellationToken cancellationToken)
    {
        await using IStorageSession session = await _runtime.Storage.Sessions.BeginAsync(envelope.TenantId, cancellationToken).ConfigureAwait(false);
        if (keyed && await session.InboxSeenAsync(Consumer, envelope.MessageId, cancellationToken).ConfigureAwait(false) is { } seen)
        {
            _runtime.Metrics.InboxDuplicates.Add(1, new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.ConsumerGroup, Consumer));
            return Duplicate(seen.Result, envelope.TenantId);
        }

        // The optimistic cache shortcut needs no caller-side version check: If-Match must see the stored version.
        LoadResult loaded = await _aggregates.LoadAsync(
            aggregate, stream, envelope.TenantId, (from, max, ct) => session.ReadStreamAsync(stream, from, max, ct), cancellationToken,
            useCache: true, optimistic: expectedVersion is null).ConfigureAwait(false);
        (object state, StreamVersion version) = (loaded.State, loaded.Version);

        if (expectedVersion is { } expected && expected != version)
        {
            return new CommandResult(CommandStatus.Conflict, version, Error.VersionMismatch, ConsistencyToken.None);
        }

        var context = new DecisionContext(_runtime.Time.GetUtcNow(), version, envelope.MessageId, envelope.CorrelationId, envelope.Headers);
        Decision decision = handler is null
            ? aggregate.Decide(state, command, context)
            : await handler.HandleAsync(services, command, state, context, cancellationToken).ConfigureAwait(false);

        if (decision.Kind != DecisionKind.Accepted && loaded.Optimistic
            && await session.ReadStreamAsync(stream, version + 1, 1, cancellationToken).ConfigureAwait(false) is { Events.Count: > 0 } newer)
        {
            // A rejection appends nothing, so no version check would catch a stale cached state: re-decide on fresh state.
            throw new ConcurrencyException(stream, ExpectedVersion.Exact(version), newer.CurrentVersion);
        }

        if (decision.Kind != DecisionKind.Accepted)
        {
            CommandStatus status = decision.IsRejected ? CommandStatus.Rejected : CommandStatus.NoOp;
            if (keyed)
            {
                if (decision.IsRejected && envelope.Headers.TryGetValue(CommandReplies.ReplyToHeader, out string? replyTo) && replyTo.Length > 0)
                {
                    // ADR-018: the rejection reply commits with the inbox record, so it is sent exactly when the rejection is.
                    session.EnqueueOutbox(_runtime.ToOutbox(RejectionReply(envelope, decision.Error!), replyTo, OutboxKind.Send));
                }

                session.RecordInbox(Consumer, envelope.MessageId, new InboxResult(status, version, decision.Error?.Code));
                await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return new CommandResult(status, version, decision.Error, ConsistencyToken.None);
        }

        IEvent[] events = decision.Events.ToArray();
        var toAppend = new EventToAppend[events.Length];
        var envelopes = new MessageEnvelope[events.Length];
        for (int i = 0; i < events.Length; i++)
        {
            MessageEnvelope eventEnvelope = _runtime.CreateEnvelope(events[i], envelope, subject: stream.Value) with
            {
                Time = context.Now,
                StreamVersion = (version + (i + 1)).Value,
            };
            envelopes[i] = eventEnvelope;
            toAppend[i] = new EventToAppend(eventEnvelope.MessageId, eventEnvelope.Type, eventEnvelope.TypeVersion, eventEnvelope.Body, Metadata(eventEnvelope), context.Now, []);
        }

        long appendStarted = Stopwatch.GetTimestamp();
        using Activity? append = SourceFlowTelemetry.ActivitySource.StartActivity("sourceflow.eventstore.append", ActivityKind.Client);
        append?.SetTag(SourceFlowTelemetry.Tags.DbOperationName, "append");
        append?.SetTag(SourceFlowTelemetry.Tags.EventsCount, events.Length);
        session.Append(stream, aggregate.Name, ExpectedVersion.Exact(version), toAppend);
        EnqueuePublications(session, events, envelopes, stream);
        StreamVersion newVersion = version + events.Length;
        if (keyed)
        {
            session.RecordInbox(Consumer, envelope.MessageId, new InboxResult(CommandStatus.Accepted, newVersion, null));
        }

        CommitResult commit;
        await using (var projections = new ProjectionScope(_runtime, session, services))
        {
            await ApplyInlineProjectionsAsync(projections, events, envelopes, stream, cancellationToken).ConfigureAwait(false);
            await projections.CompleteAsync(cancellationToken).ConfigureAwait(false);
            commit = await session.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        append?.Dispose();   // the span covers the commit, as AppendDuration does
        _runtime.Metrics.AppendDuration.Record(Stopwatch.GetElapsedTime(appendStarted).TotalSeconds);
        _runtime.Metrics.AppendEvents.Record(events.Length, new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.AggregateType, aggregate.Name));
        await _aggregates.CommittedAsync(aggregate, stream, envelope.TenantId, state, version, events, cancellationToken).ConfigureAwait(false);
        return new CommandResult(CommandStatus.Accepted, commit.StreamVersion ?? newVersion, null, Token(commit.LastPosition, envelope.TenantId));
    }

    // CommandRejected<T> on the wire: { "command": <the command's JSON, as received>, "error": { "code", "message" } }.
    // Built from the received body, so the owner needs no JSON metadata for the reply type.
    private MessageEnvelope RejectionReply(MessageEnvelope command, Error error)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("command");
            writer.WriteRawValue(command.Body.Span, skipInputValidation: true);
            writer.WriteStartObject("error");
            writer.WriteString("code", error.Code);
            writer.WriteString("message", error.Message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return new MessageEnvelope
        {
            MessageId = MessageIds.New(_runtime.Time),
            Type = CommandReplies.RejectedTypeName(command.Type),
            TypeVersion = command.TypeVersion,
            Source = _runtime.ServiceName,
            Time = _runtime.Time.GetUtcNow(),
            CorrelationId = command.CorrelationId,
            CausationId = command.MessageId,
            TenantId = command.TenantId,
            Actor = command.Actor,
            TraceParent = Activity.Current?.Id ?? command.TraceParent,
            TraceState = Activity.Current?.TraceStateString ?? command.TraceState,
            Body = buffer.WrittenMemory.ToArray(),
        };
    }

    private void EnqueuePublications(IStorageSession session, IEvent[] events, MessageEnvelope[] envelopes, StreamId stream)
    {
        for (int i = 0; i < events.Length; i++)
        {
            if (!_runtime.Routing.Publications.TryGetValue(events[i].GetType(), out IReadOnlyList<PublicationRoute>? routes))
            {
                continue;
            }

            foreach (PublicationRoute route in routes)
            {
                IMessage published = route.Map(events[i]);
                string partitionKey = route.PartitionKey?.Invoke(events[i]) ?? stream.Value;
                MessageEnvelope envelope = ReferenceEquals(published, events[i])
                    ? envelopes[i] with { PartitionKey = partitionKey }
                    : _runtime.CreateEnvelope(published, envelopes[i], subject: stream.Value, partitionKey: partitionKey);
                session.EnqueueOutbox(_runtime.ToOutbox(envelope, route.Destination, OutboxKind.Publish));
            }
        }
    }

    private async ValueTask ApplyInlineProjectionsAsync(ProjectionScope projections, IEvent[] events, MessageEnvelope[] envelopes, StreamId stream, CancellationToken cancellationToken)
    {
        if (_runtime.InlineProjections.Count == 0)
        {
            return;
        }

        for (int i = 0; i < events.Length; i++)
        {
            var context = new EventContext(envelopes[i].MessageId, stream, new StreamVersion(envelopes[i].StreamVersion!.Value), GlobalPosition.Start,
                envelopes[i].Time, envelopes[i].CorrelationId, envelopes[i].CausationId, envelopes[i].TenantId == MessageEnvelope.DefaultTenant ? null : envelopes[i].TenantId);
            foreach (ProjectionDescriptor projection in _runtime.InlineProjections)
            {
                await projections.ApplyAsync(projection, events[i], context, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<CommandResult> StoredResultAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        await using IStorageSession session = await _runtime.Storage.Sessions.BeginAsync(envelope.TenantId, cancellationToken).ConfigureAwait(false);
        InboxEntry? entry = await session.InboxSeenAsync(Consumer, envelope.MessageId, cancellationToken).ConfigureAwait(false);
        return entry is null
            ? new CommandResult(CommandStatus.Duplicate, StreamVersion.None, null, ConsistencyToken.None)
            : Duplicate(entry.Result, envelope.TenantId);
    }

    private static CommandResult Duplicate(InboxResult stored, string tenant)
    {
        Error? error = stored.ErrorCode is null ? null : new Error(stored.ErrorCode, "The original request was not accepted.");
        return new CommandResult(CommandStatus.Duplicate, stored.Version, error, Token(stored.Position == default ? null : stored.Position, tenant));
    }

    private static ConsistencyToken Token(GlobalPosition? position, string tenant)
    {
        return position is { } p ? new ConsistencyToken(p, tenant == MessageEnvelope.DefaultTenant ? null : tenant) : ConsistencyToken.None;
    }

    private static EventMetadata Metadata(MessageEnvelope envelope)
    {
        return new EventMetadata
        {
            CorrelationId = envelope.CorrelationId,
            CausationId = envelope.CausationId,
            Source = envelope.Source,
            Actor = envelope.Actor,
            TraceParent = envelope.TraceParent,
            TraceState = envelope.TraceState,
            Headers = envelope.Headers,
        };
    }

    private Task Backoff(int attempt, CancellationToken cancellationToken)
    {
        // Jittered exponential backoff: 2^attempt ms scaled by a random factor, capped at 200 ms. This is infrastructure
        // timing, not business time, so it uses the system clock: a test's fake clock must not stall re-decides.
        double milliseconds = Math.Min(200, Math.Pow(2, attempt) * (0.5 + Random.Shared.NextDouble()));
        return attempt == 0 ? Task.CompletedTask : Task.Delay(TimeSpan.FromMilliseconds(milliseconds), TimeProvider.System, cancellationToken);
    }
}

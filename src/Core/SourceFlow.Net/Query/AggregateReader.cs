using SourceFlow.Runtime;
using SourceFlow.Storage;

namespace SourceFlow.Query;

/// <summary>A temporal bound for <see cref="IAggregateReader"/> (doc 03 §5.4).</summary>
public readonly record struct AsOf
{
    private AsOf(StreamVersion? version, DateTimeOffset? time)
    {
        VersionBound = version;
        TimeBound = time;
    }

    /// <summary>The latest state.</summary>
    public static AsOf Latest => default;

    /// <summary>The version bound, if any.</summary>
    public StreamVersion? VersionBound { get; }

    /// <summary>The time bound, if any.</summary>
    public DateTimeOffset? TimeBound { get; }

    /// <summary>The state after event <paramref name="version"/>.</summary>
    public static AsOf Version(long version)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        return new AsOf(new StreamVersion(version), null);
    }

    /// <summary>The state including every event decided at or before <paramref name="time"/>.</summary>
    public static AsOf Time(DateTimeOffset time)
    {
        return new AsOf(null, time);
    }
}

/// <summary>An aggregate's state at a point in time.</summary>
/// <typeparam name="TAggregate">The aggregate.</typeparam>
/// <param name="State">The folded state.</param>
/// <param name="Version">The version of the last folded event.</param>
public sealed record AggregateSnapshot<TAggregate>(TAggregate State, StreamVersion Version);

/// <summary>
/// Strongly consistent, read-only access to aggregate state, optionally as of a version or time (read side; never used
/// inside <c>Decide</c>, SF0015).
/// </summary>
public interface IAggregateReader
{
    /// <summary>Loads an aggregate's state.</summary>
    ValueTask<AggregateSnapshot<TAggregate>> LoadAsync<TAggregate, TId>(TId id, AsOf asOf = default, string? tenantId = null, CancellationToken cancellationToken = default)
        where TAggregate : class, IAggregateRuntime<TAggregate, TId>
        where TId : struct, IStronglyTypedId<TId>;
}

internal sealed class AggregateReader(SourceFlowRuntime runtime) : IAggregateReader
{
    private readonly AggregateStore _decoder = new(runtime);

    public async ValueTask<AggregateSnapshot<TAggregate>> LoadAsync<TAggregate, TId>(TId id, AsOf asOf = default, string? tenantId = null, CancellationToken cancellationToken = default)
        where TAggregate : class, IAggregateRuntime<TAggregate, TId>
        where TId : struct, IStronglyTypedId<TId>
    {
        StreamId stream = StreamId.For(TAggregate.AggregateName, id);
        string tenant = tenantId ?? runtime.CurrentTenant;
        TAggregate state = TAggregate.Initial;
        StreamVersion version = StreamVersion.None;
        while (true)
        {
            StreamSlice slice = await runtime.Storage.Events.ReadStreamAsync(tenant, stream, version + 1, 512, cancellationToken).ConfigureAwait(false);
            foreach (StoredEvent stored in slice.Events)
            {
                if ((asOf.VersionBound is { } maxVersion && stored.StreamVersion > maxVersion)
                    || (asOf.TimeBound is { } maxTime && stored.CreatedAt > maxTime))
                {
                    return new AggregateSnapshot<TAggregate>(state, version);
                }

                state = state.Evolve((IEvent)_decoder.Deserialize(stored, upcast: true));
                version = stored.StreamVersion;
            }

            if (slice.IsEndOfStream || slice.Events.Count == 0)
            {
                return new AggregateSnapshot<TAggregate>(state, version);
            }
        }
    }
}

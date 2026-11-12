using System.Collections.Frozen;
using System.Diagnostics;
using SourceFlow.Storage;

namespace SourceFlow.Runtime;

/// <summary>Resolves which read-model generation serves reads (doc 06 §6), for queries over EF Core read models.</summary>
public interface IReadModelResolver
{
    /// <summary>The generation of <paramref name="projection"/> that currently serves reads.</summary>
    /// <exception cref="ArgumentException">The service has no projection with that name.</exception>
    ValueTask<ReadModelGeneration> ResolveAsync(string projection, CancellationToken cancellationToken = default);
}

/// <summary>
/// The node's view of the read-model aliases (doc 06 §6, ADR-032): which generation serves reads and which generations
/// this node writes. Refreshed from the store at most once per poll interval; a switch made by this node is seen at once.
/// </summary>
internal sealed class ReadModelGenerations : IReadModelResolver
{
    private readonly SourceFlowRuntime _runtime;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Snapshot _snapshot = new(FrozenDictionary<string, CollectionAlias>.Empty, 0);
    private FrozenSet<ProjectionDescriptor> _participants = FrozenSet<ProjectionDescriptor>.Empty;
    private int _revision;

    public ReadModelGenerations(SourceFlowRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <summary>True when the store keeps aliases.</summary>
    public bool Enabled => _runtime.Storage.Aliases is not null;

    /// <summary>The alias rows, by projection name.</summary>
    public IReadOnlyDictionary<string, CollectionAlias> Aliases => _snapshot.Aliases;

    /// <summary>Changes whenever the aliases are reloaded or switched, so the subscription engine recomposes its subscriptions.</summary>
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>Sets the async projections that are built blue/green (document projections, and session projections whose store keeps generations apart).</summary>
    internal void SetParticipants(IEnumerable<ProjectionDescriptor> projections)
    {
        _participants = Enabled ? projections.ToFrozenSet() : FrozenSet<ProjectionDescriptor>.Empty;
    }

    /// <summary>True when <paramref name="projection"/> is built blue/green.</summary>
    public bool Participates(ProjectionDescriptor projection)
    {
        return _participants.Contains(projection);
    }

    /// <summary>Reloads the aliases when the last load is older than the poll interval (or always, when forced).</summary>
    /// <returns>True when the aliases were reloaded.</returns>
    public async ValueTask<bool> RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        if (_runtime.Storage.Aliases is not { } aliases || (!force && !Stale()))
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && !Stale())
            {
                return false;   // another caller refreshed while this one waited
            }

            IReadOnlyList<CollectionAlias> rows = await aliases.ListAliasesAsync(cancellationToken).ConfigureAwait(false);
            _snapshot = new Snapshot(rows.ToFrozenDictionary(r => r.Alias, StringComparer.Ordinal), Stopwatch.GetTimestamp());
            Interlocked.Increment(ref _revision);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The generation that serves reads of <paramref name="projection"/>.</summary>
    public ReadModelGeneration ReadGeneration(ProjectionDescriptor projection)
    {
        ReadModelGeneration first = First(projection);
        return Participates(projection)
            && Aliases.TryGetValue(projection.Name, out CollectionAlias? alias)
            && ReadModelGeneration.TryParse(alias.Target, out ReadModelGeneration? target)
            ? target.Value
            : first;
    }

    /// <summary>The collection that serves reads of a projection, refreshing the aliases when they are stale.</summary>
    public async ValueTask<string> ReadCollectionAsync(string projectionName, string fallback, CancellationToken cancellationToken)
    {
        ProjectionDescriptor? projection = _runtime.AsyncProjections.FirstOrDefault(p => p.Name == projectionName);
        if (projection is null || !Participates(projection))
        {
            return fallback;
        }

        await RefreshAsync(force: false, cancellationToken).ConfigureAwait(false);
        return ReadGeneration(projection).Collection;
    }

    /// <inheritdoc />
    public async ValueTask<ReadModelGeneration> ResolveAsync(string projection, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(projection);
        ProjectionDescriptor descriptor = _runtime.Model.Projections.FirstOrDefault(p => p.Name == projection)
            ?? throw new ArgumentException($"This service has no projection named '{projection}'.", nameof(projection));
        if (Participates(descriptor))
        {
            await RefreshAsync(force: false, cancellationToken).ConfigureAwait(false);
        }

        return ReadGeneration(descriptor);
    }

    /// <summary>
    /// The generations this node writes for an async projection: the one serving reads (when it is this code's version)
    /// and one being built (a version bump, a requested rebuild, or the first build before any alias exists). Both are
    /// null when a newer version already serves reads: this node's generation is retired.
    /// </summary>
    /// <summary>Every collection a projection writes now: its configured one, and the serving and building generations.</summary>
    internal IReadOnlyCollection<string> CollectionsOf(ProjectionDescriptor projection)
    {
        var collections = new HashSet<string>(StringComparer.Ordinal) { projection.Collection };
        (ReadModelGeneration? serving, ReadModelGeneration? building) = Plan(projection);
        if (serving is { } servingGeneration)
        {
            collections.Add(servingGeneration.Collection);
        }

        if (building is { } buildingGeneration)
        {
            collections.Add(buildingGeneration.Collection);
        }

        return collections;
    }

    public (ReadModelGeneration? Serving, ReadModelGeneration? Building) Plan(ProjectionDescriptor projection)
    {
        ReadModelGeneration first = First(projection);
        if (!Participates(projection))
        {
            return (first, null);
        }

        if (!Aliases.TryGetValue(projection.Name, out CollectionAlias? alias))
        {
            return (null, first);   // nothing serves yet: build the first generation, then claim the alias
        }

        if (!ReadModelGeneration.TryParse(alias.Target, out ReadModelGeneration? parsed))
        {
            return (first, null);
        }

        ReadModelGeneration target = parsed.Value;
        ReadModelGeneration? requested = ReadModelGeneration.TryParse(alias.Building, out ReadModelGeneration? b)
            && b.Value.Version == projection.Version && b.Value.IsNewerThan(target)
            ? b
            : null;
        if (target.Version > projection.Version)
        {
            return (null, null);
        }

        return target.Version == projection.Version ? (target, requested) : (null, requested ?? first);
    }

    /// <summary>Records a switch made by this node without waiting for the next refresh.</summary>
    public void Switched(string alias, string? previous, string target, DateTimeOffset at)
    {
        Snapshot current = _snapshot;
        var rows = current.Aliases.ToDictionary(StringComparer.Ordinal);
        string? building = rows.TryGetValue(alias, out CollectionAlias? row) && row.Building != target ? row.Building : null;
        rows[alias] = new CollectionAlias(alias, target, previous, building, at);
        _snapshot = new Snapshot(rows.ToFrozenDictionary(StringComparer.Ordinal), current.LoadedAt);
        Interlocked.Increment(ref _revision);
    }

    private static ReadModelGeneration First(ProjectionDescriptor projection)
    {
        return new ReadModelGeneration(projection.Name, projection.Version, 1);
    }

    private bool Stale()
    {
        return _snapshot.LoadedAt == 0 || Stopwatch.GetElapsedTime(_snapshot.LoadedAt) >= _runtime.Options.PollInterval;
    }

    private sealed record Snapshot(FrozenDictionary<string, CollectionAlias> Aliases, long LoadedAt);
}

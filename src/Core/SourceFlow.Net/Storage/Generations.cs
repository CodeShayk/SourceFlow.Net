using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace SourceFlow.Storage;

/// <summary>
/// A read-model generation (doc 06 §6): one build of a projection, written to its own collection (documents) or schema
/// (EF Core). The first generation of a version is <c>{name}@{version}</c>; a rebuild of the same version is
/// <c>{name}@{version}.{number}</c>. Generations are ordered by version, then number, and the alias only moves forward.
/// </summary>
/// <param name="Name">The projection name.</param>
/// <param name="Version">The projection version.</param>
/// <param name="Number">The build of that version, from 1.</param>
public readonly record struct ReadModelGeneration(string Name, int Version, int Number) : IComparable<ReadModelGeneration>
{
    /// <summary>The collection id: <c>{name}@{version}</c>, or <c>{name}@{version}.{number}</c> for a rebuild.</summary>
    public string Collection => Number <= 1
        ? string.Create(CultureInfo.InvariantCulture, $"{Name}@{Version}")
        : string.Create(CultureInfo.InvariantCulture, $"{Name}@{Version}.{Number}");

    /// <summary>The subscription that builds this generation.</summary>
    public string Subscription => "projection:" + Collection;

    /// <summary>The next build of the same version (a rebuild without a code change).</summary>
    public ReadModelGeneration Next()
    {
        return this with { Number = Number + 1 };
    }

    /// <summary>Orders generations of the same projection: by version, then number.</summary>
    public int CompareTo(ReadModelGeneration other)
    {
        int byVersion = Version.CompareTo(other.Version);
        return byVersion != 0 ? byVersion : Number.CompareTo(other.Number);
    }

    /// <summary>True when this is a later generation of the same projection.</summary>
    public bool IsNewerThan(ReadModelGeneration other)
    {
        return string.Equals(Name, other.Name, StringComparison.Ordinal) && CompareTo(other) > 0;
    }

    /// <summary>Parses <c>{name}@{version}</c> or <c>{name}@{version}.{number}</c>.</summary>
    public static bool TryParse(string? collection, [NotNullWhen(true)] out ReadModelGeneration? generation)
    {
        generation = null;
        int at = collection?.LastIndexOf('@') ?? -1;
        if (at <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> rest = collection.AsSpan(at + 1);
        int dot = rest.IndexOf('.');
        ReadOnlySpan<char> version = dot < 0 ? rest : rest[..dot];
        int number = 1;
        if (!int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out int v) || v < 1
            || (dot >= 0 && (!int.TryParse(rest[(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out number) || number < 2)))
        {
            return false;
        }

        generation = new ReadModelGeneration(collection![..at], v, number);
        return true;
    }

    /// <summary>Parses a collection id.</summary>
    /// <exception cref="FormatException">The text is not a generation id.</exception>
    public static ReadModelGeneration Parse(string collection)
    {
        return TryParse(collection, out ReadModelGeneration? generation)
            ? generation.Value
            : throw new FormatException($"'{collection}' is not a read-model generation (name@version or name@version.number).");
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Collection;
    }
}

/// <summary>The alias row of a projection (doc 06 §6): which generation serves reads, which is building, which is retired.</summary>
/// <param name="Alias">The projection name.</param>
/// <param name="Target">The generation that serves reads.</param>
/// <param name="Previous">The generation retired by the last switch, kept until <c>RetainPreviousFor</c> has passed.</param>
/// <param name="Building">A generation requested by <c>dotnet sourceflow projection rebuild</c>, until it is switched in.</param>
/// <param name="SwitchedAt">When the alias last moved (store clock).</param>
public sealed record CollectionAlias(string Alias, string Target, string? Previous, string? Building, DateTimeOffset SwitchedAt);

/// <summary>
/// Read-model aliases (doc 06 §6, ADR-032): one row per projection name pointing at the generation that serves reads.
/// Every change is a compare-and-set, so concurrent nodes switch an alias exactly once.
/// </summary>
public interface ICollectionAliases
{
    /// <summary>Every alias row.</summary>
    ValueTask<IReadOnlyList<CollectionAlias>> ListAliasesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Points <paramref name="alias"/> at <paramref name="target"/> when it currently points at
    /// <paramref name="expectedTarget"/> (null: the alias does not exist yet). The old target becomes
    /// <see cref="CollectionAlias.Previous"/>, a matching <see cref="CollectionAlias.Building"/> is cleared, and the switch
    /// time is the store's clock.
    /// </summary>
    /// <returns>True when this call switched the alias.</returns>
    ValueTask<bool> SwitchAsync(string alias, string? expectedTarget, string target, CancellationToken cancellationToken);

    /// <summary>
    /// Requests a new generation (<paramref name="building"/>) of <paramref name="alias"/>; creates the row pointing at
    /// <paramref name="currentTarget"/> when it does not exist.
    /// </summary>
    ValueTask RequestAsync(string alias, string currentTarget, string building, CancellationToken cancellationToken);

    /// <summary>Clears <see cref="CollectionAlias.Previous"/> once that generation's data is dropped (when it still matches).</summary>
    ValueTask ClearPreviousAsync(string alias, string previous, CancellationToken cancellationToken);
}

/// <summary>
/// The read-model generation the current unit of work writes (doc 06 §6). Scoped; the engine sets it before it begins a
/// session projection's unit of work, so a schema-per-generation store (EF Core) can pick the target schema.
/// </summary>
public sealed class ProjectionTarget
{
    /// <summary>The generation being written, or null outside a projection.</summary>
    public ReadModelGeneration? Generation { get; set; }
}

/// <summary>
/// A session-projection store that keeps each read-model generation apart (for example an EF Core schema per generation),
/// so the projection can be rebuilt blue/green (doc 06 §6). Implemented by the unit-of-work factory.
/// </summary>
public interface IProjectionGenerationStore
{
    /// <summary>True when this store keeps each generation apart; false for a store shared by every generation.</summary>
    bool SeparatesGenerations { get; }

    /// <summary>Deletes a retired generation's data inside <paramref name="session"/> (committed by the caller).</summary>
    ValueTask DropGenerationAsync(ReadModelGeneration generation, IStorageSession session, CancellationToken cancellationToken);
}

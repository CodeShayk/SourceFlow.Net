using System.Data.Common;
using System.Globalization;
using SourceFlow.Messaging;

namespace SourceFlow.Migration;

/// <summary>
/// A v2 entity row, read from its table with SQL (doc 12 §3). <c>migrate v2 scaffold</c> generates the type from the
/// table's columns; no v2 assembly is referenced.
/// </summary>
/// <typeparam name="TSelf">The row type.</typeparam>
public interface IV2Entity<TSelf>
    where TSelf : IV2Entity<TSelf>
{
    /// <summary>The v2 table, after v2's naming convention (by default the entity's class name).</summary>
    static abstract string TableName { get; }

    /// <summary>The v2 id (<c>IEntity.Id</c>), which commands reference as <c>EntityId</c>.</summary>
    int Id { get; }

    /// <summary>Reads the current row.</summary>
    static abstract TSelf Read(V2Row row);
}

/// <summary>
/// Imports one v2 entity type as genesis events of a v3 aggregate (doc 12 §3). One implementation per aggregate type;
/// <c>migrate v2 scaffold</c> generates a stub.
/// </summary>
/// <typeparam name="TV2Entity">The v2 entity row.</typeparam>
/// <typeparam name="TAggregate">The v3 aggregate.</typeparam>
/// <typeparam name="TId">The aggregate's id.</typeparam>
public interface IV2Import<TV2Entity, TAggregate, TId>
    where TV2Entity : IV2Entity<TV2Entity>
    where TAggregate : class, IAggregateRuntime<TAggregate, TId>
    where TId : struct, IStronglyTypedId<TId>
{
    /// <summary>The v3 id of a v2 entity; deterministic, usually <see cref="V2Ids.FromLegacy{TId}(int, string?)"/>.</summary>
    TId MapId(TV2Entity entity);

    /// <summary>The genesis events of the stream: the entity's current state as one or more events.</summary>
    IEnumerable<IEvent> ToEvents(TV2Entity entity, V2ImportContext context);

    /// <summary>
    /// Compares the rebuilt v3 state with the v2 row for <c>migrate v2 verify</c>; returns one description per
    /// difference (none when they agree).
    /// </summary>
    IEnumerable<string> Compare(TV2Entity entity, TAggregate state);

    /// <summary>
    /// True when a v2 command (by its assembly-qualified <c>CommandType</c>) belongs to this entity type. Commands count
    /// towards <see cref="V2ImportContext.CommandCount"/>; v2 entity ids are only unique per type, so filter here when
    /// several entity types share the command table.
    /// </summary>
    bool OwnsCommand(string commandType)
    {
        return true;
    }
}

/// <summary>What an import knows about the v2 entity it converts.</summary>
public sealed record V2ImportContext
{
    /// <summary>When the import runs.</summary>
    public required DateTimeOffset ImportedAt { get; init; }

    /// <summary>The v2 id.</summary>
    public required int V2Id { get; init; }

    /// <summary>The entity's commands in the v2 command table (those its import owns).</summary>
    public required int CommandCount { get; init; }

    /// <summary>The highest v2 <c>SequenceNo</c> of those commands, or 0 without commands.</summary>
    public required int LastSequenceNo { get; init; }

    /// <summary>The v3 tenant the stream is imported into.</summary>
    public required string TenantId { get; init; }
}

/// <summary>
/// The deterministic map from v2 <c>int</c> ids to v3 ids (doc 12 §3): RFC 9562 UUIDv5 of the id, so every run, every
/// node and the v2 wire bridge agree on the v3 id of a v2 entity.
/// </summary>
public static class V2Ids
{
    /// <summary>The namespace of v2 id mapping.</summary>
    public static readonly Guid Namespace = new("8d0f3c52-4b7e-5a19-b6c2-2e9f71a4d038");

    /// <summary>The v3 UUID of a v2 id.</summary>
    /// <param name="id">The v2 id.</param>
    /// <param name="scope">
    /// Separates entity types whose ids overlap (each v2 entity table numbers from 1); null for the plain id. Use the same
    /// scope wherever the id is mapped, including the wire bridge.
    /// </param>
    public static Guid FromLegacy(int id, string? scope = null)
    {
        string name = id.ToString(CultureInfo.InvariantCulture);
        return MessageIds.CreateVersion5(Namespace, scope is null ? name : scope + "/" + name);
    }

    /// <summary>The v3 id of a v2 id, for ids backed by a <see cref="Guid"/>.</summary>
    public static TId FromLegacy<TId>(int id, string? scope = null)
        where TId : struct, IStronglyTypedId<TId>
    {
        return TId.Parse(FromLegacy(id, scope).ToString("D"));
    }
}

/// <summary>
/// The current row of a v2 table, with conversions for the column types v2's EF store produced on each provider
/// (SQLite-style <c>INTEGER</c> booleans, <c>REAL</c> decimals and <c>TEXT</c> dates, or native types).
/// </summary>
public sealed class V2Row
{
    private readonly DbDataReader _reader;
    private readonly Dictionary<string, int> _ordinals;

    /// <summary>Reads the current row of <paramref name="reader"/> (the row moves with the reader).</summary>
    public V2Row(DbDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
        _ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < reader.FieldCount; i++)
        {
            _ordinals.TryAdd(reader.GetName(i), i);
        }
    }

    /// <summary>True when the row has the column.</summary>
    public bool Has(string column)
    {
        return _ordinals.ContainsKey(column);
    }

    /// <summary>True when the column is null.</summary>
    public bool IsNull(string column)
    {
        return Value(column) is null;
    }

    /// <summary>The raw value, or null for a database null.</summary>
    public object? Value(string column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (!_ordinals.TryGetValue(column, out int ordinal))
        {
            throw new KeyNotFoundException($"The v2 row has no column '{column}'.");
        }

        object value = _reader.GetValue(ordinal);
        return value is DBNull ? null : value;
    }

    /// <summary>A string column.</summary>
    public string? String(string column)
    {
        return Value(column) switch
        {
            null => null,
            string s => s,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            object o => o.ToString(),
        };
    }

    /// <summary>An <c>int</c> column (0 when null).</summary>
    public int Int32(string column)
    {
        return Value(column) is { } v ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>A <c>long</c> column (0 when null).</summary>
    public long Int64(string column)
    {
        return Value(column) is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>A <c>decimal</c> column (0 when null); also reads <c>REAL</c> and text.</summary>
    public decimal Decimal(string column)
    {
        return Value(column) switch
        {
            null => 0m,
            string s => decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture),
            double d => (decimal)d,
            float f => (decimal)f,
            object o => Convert.ToDecimal(o, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>A <c>double</c> column (0 when null).</summary>
    public double Double(string column)
    {
        return Value(column) is { } v ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 0d;
    }

    /// <summary>A <c>bool</c> column (false when null); also reads <c>INTEGER</c> 0/1 and text.</summary>
    public bool Boolean(string column)
    {
        return Value(column) switch
        {
            null => false,
            bool b => b,
            string s => s is "1" || bool.Parse(s),
            object o => Convert.ToInt64(o, CultureInfo.InvariantCulture) != 0,
        };
    }

    /// <summary>A <c>DateTime</c> column, as UTC (v2 stored <c>DateTime.UtcNow</c>); also reads ISO 8601 text.</summary>
    public DateTime DateTime(string column)
    {
        return Value(column) switch
        {
            null => default,
            System.DateTime d => System.DateTime.SpecifyKind(d, DateTimeKind.Utc),
            DateTimeOffset o => o.UtcDateTime,
            string s => System.DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            object o => Convert.ToDateTime(o, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>A binary column.</summary>
    public byte[]? Bytes(string column)
    {
        return Value(column) switch
        {
            null => null,
            byte[] b => b,
            string s => Convert.FromBase64String(s),
            object o => throw new InvalidCastException($"Column '{column}' is {o.GetType().Name}, not binary."),
        };
    }
}

using System.Collections.Frozen;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SourceFlow.Messaging;
using SourceFlow.Runtime;

namespace SourceFlow.Query;

/// <summary>The static type of a query value (doc 14 §5.4).</summary>
public enum QueryType
{
    /// <summary>Unknown or dynamic (CESQL implicit casting applies).</summary>
    Any,

    /// <summary>A string.</summary>
    String,

    /// <summary>A whole number.</summary>
    Integer,

    /// <summary>An exact decimal.</summary>
    Decimal,

    /// <summary>A boolean.</summary>
    Boolean,

    /// <summary>A UTC timestamp.</summary>
    Timestamp,

    /// <summary>A UUID.</summary>
    Uuid,

    /// <summary>A duration.</summary>
    Duration,

    /// <summary>A global log position (opaque, comparable).</summary>
    Position,

    /// <summary>A JSON object.</summary>
    Object,

    /// <summary>A JSON array.</summary>
    Array,
}

/// <summary>A schema field: its JSON name, type, nested fields and query flags (doc 14 §2.1).</summary>
/// <param name="Name">The JSON (camelCase) name.</param>
/// <param name="Type">The type.</param>
/// <param name="Queryable">True for <c>[Queryable]</c>: an indexed predicate (doc 14 §7.2).</param>
/// <param name="PersonalData">True for <c>[PersonalData]</c>: never a filter, sort, group or join key (SFQ0301).</param>
/// <param name="RenamedFrom">Earlier names by version (<c>[RenamedFrom]</c>).</param>
/// <param name="Fields">Nested fields of an object.</param>
/// <param name="Element">The element of an array.</param>
public sealed record QueryField(
    string Name,
    QueryType Type,
    bool Queryable,
    bool PersonalData,
    IReadOnlyDictionary<int, string> RenamedFrom,
    IReadOnlyList<QueryField> Fields,
    QueryField? Element);

/// <summary>One version of an event type.</summary>
public sealed record QueryEventVersion(int Version, Type ClrType, IReadOnlyList<QueryField> Fields);

/// <summary>An event type of this service, with all its versions (ascending).</summary>
public sealed record QueryEventType(string Name, IReadOnlyList<QueryEventVersion> Versions)
{
    /// <summary>The latest version.</summary>
    public QueryEventVersion Latest => Versions[^1];
}

/// <summary>A queryable read model: a document projection's collection.</summary>
public sealed record QueryReadModel(string Name, string Collection, Type DocumentType, IReadOnlyList<QueryField> Fields);

/// <summary>A queryable aggregate (temporal state, doc 14 §5.6).</summary>
public sealed record QueryAggregate(string Name, Type StateType, IReadOnlyList<QueryField> Fields);

/// <summary>
/// The schema catalog of one service (doc 14 §2; Q-B1): its event types (every version), read models and aggregates,
/// derived from the runtime model, which holds only what this service's modules register. Names outside it don't bind.
/// </summary>
public sealed class QueryCatalog
{
    private readonly FrozenDictionary<string, QueryEventType> _events;
    private readonly FrozenDictionary<string, QueryReadModel> _readModels;
    private readonly FrozenDictionary<string, QueryAggregate> _aggregates;

    /// <summary>Creates a catalog.</summary>
    public QueryCatalog(IEnumerable<QueryEventType> events, IEnumerable<QueryReadModel> readModels, IEnumerable<QueryAggregate> aggregates)
    {
        _events = events.ToFrozenDictionary(e => e.Name, StringComparer.Ordinal);
        _readModels = readModels.ToFrozenDictionary(r => r.Name, StringComparer.Ordinal);
        _aggregates = aggregates.ToFrozenDictionary(a => a.Name, StringComparer.Ordinal);
        Fingerprint = ComputeFingerprint();
    }

    /// <summary>The event types.</summary>
    public IEnumerable<QueryEventType> EventTypes => _events.Values;

    /// <summary>The read models.</summary>
    public IEnumerable<QueryReadModel> ReadModels => _readModels.Values;

    /// <summary>The aggregates.</summary>
    public IEnumerable<QueryAggregate> Aggregates => _aggregates.Values;

    /// <summary>A SHA-256 over the catalog's shape; part of plan-cache keys and continuation tokens (doc 14 §7.1, §7.4).</summary>
    public string Fingerprint { get; }

    /// <summary>Finds an event type by logical name.</summary>
    public bool TryGetEventType(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out QueryEventType? type)
    {
        return _events.TryGetValue(name, out type);
    }

    /// <summary>Finds a read model by projection name, or by collection (<c>name@version</c>).</summary>
    public bool TryGetReadModel(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out QueryReadModel? readModel)
    {
        if (_readModels.TryGetValue(name, out readModel))
        {
            return true;
        }

        readModel = _readModels.Values.FirstOrDefault(r => r.Collection == name);
        return readModel is not null;
    }

    /// <summary>Finds an aggregate by name.</summary>
    public bool TryGetAggregate(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out QueryAggregate? aggregate)
    {
        return _aggregates.TryGetValue(name, out aggregate);
    }

    /// <summary>Builds the catalog of a service from its runtime model.</summary>
    public static QueryCatalog Build(RuntimeModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        JsonSerializerOptions options = model.Registry.Options;
        QueryEventType[] events =
        [
            .. model.Registry.All
                .Where(t => t.Kind == MessageKind.Event)
                .GroupBy(t => t.Name, StringComparer.Ordinal)
                .Select(g => new QueryEventType(g.Key, [.. g.OrderBy(t => t.Version).Select(t => new QueryEventVersion(t.Version, t.ClrType, Fields(t.JsonTypeInfo, options, 0)))])),
        ];
        QueryReadModel[] readModels =
        [
            .. model.Projections.OfType<DocumentProjectionDescriptor>()
                .Select(p => new QueryReadModel(p.Name, p.Collection, p.DocumentType, Fields(model.JsonTypeInfoFor(p.DocumentType), options, 0))),
            // DSL workflows keep one document per instance (doc 15 §6.1 observe, §7).
            .. model.ProcessManagers.Any(p => p.IsWorkflow)
                ? [new QueryReadModel(Workflows.WorkflowInstanceSummary.ReadModel, Workflows.WorkflowInstanceSummary.Collection, typeof(Workflows.WorkflowInstanceSummary),
                    Fields(model.JsonTypeInfoFor(typeof(Workflows.WorkflowInstanceSummary)), options, 0))]
                : Array.Empty<QueryReadModel>(),
        ];
        QueryAggregate[] aggregates =
        [
            .. model.Aggregates.Values.Select(a => new QueryAggregate(a.Name, a.StateType, Fields(model.JsonTypeInfoFor(a.StateType), options, 0))),
        ];
        return new QueryCatalog(events, readModels, aggregates);
    }

    private static IReadOnlyList<QueryField> Fields(JsonTypeInfo info, JsonSerializerOptions options, int depth)
    {
        if (info.Kind != JsonTypeInfoKind.Object || depth > 8)
        {
            return [];
        }

        var fields = new List<QueryField>();
        foreach (JsonPropertyInfo property in info.Properties)
        {
            // On the property or, for a positional record, on its constructor parameter (doc 10 §4).
            bool queryable = MemberAttributes.IsDefined<QueryableAttribute>(property);
            bool personal = MemberAttributes.IsDefined<PersonalDataAttribute>(property);
            var renamed = new Dictionary<int, string>();
            foreach (RenamedFromAttribute rename in MemberAttributes.Of<RenamedFromAttribute>(property))
            {
                renamed[rename.Version] = rename.Name;
            }

            (QueryType type, IReadOnlyList<QueryField> nested, QueryField? element) = Describe(property.PropertyType, options, depth);
            fields.Add(new QueryField(property.Name, type, queryable, personal, renamed, nested, element));
        }

        return fields;
    }

    private static (QueryType Type, IReadOnlyList<QueryField> Fields, QueryField? Element) Describe(Type clr, JsonSerializerOptions options, int depth)
    {
        Type type = Nullable.GetUnderlyingType(clr) ?? clr;
        if (typeof(IStronglyTypedId).IsAssignableFrom(type))
        {
            // As the schema catalog: what the converter writes (CR-30). An Int64 id is a JSON number.
            return (IdBackings.Of(type, options) == IdBacking.Int64 ? QueryType.Integer : QueryType.String, [], null);
        }

        if (type == typeof(string) || type == typeof(char))
        {
            return (QueryType.String, [], null);
        }

        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort))
        {
            return (QueryType.Integer, [], null);
        }

        if (type == typeof(decimal) || type == typeof(double) || type == typeof(float))
        {
            return (QueryType.Decimal, [], null);
        }

        if (type == typeof(bool))
        {
            return (QueryType.Boolean, [], null);
        }

        if (type == typeof(DateTimeOffset) || type == typeof(DateTime) || type == typeof(DateOnly))
        {
            return (QueryType.Timestamp, [], null);
        }

        if (type == typeof(Guid))
        {
            return (QueryType.Uuid, [], null);
        }

        if (type == typeof(TimeSpan))
        {
            return (QueryType.Duration, [], null);
        }

        JsonTypeInfo? info = TryGetTypeInfo(type, options);
        if (type.IsEnum)
        {
            // Numbers by default; strings with a string-enum converter. Serialize a value to see which.
            return (info is not null && JsonSerializer.SerializeToElement(Enum.ToObject(type, 0), info).ValueKind == JsonValueKind.String
                ? QueryType.String
                : QueryType.Integer, [], null);
        }

        return info?.Kind switch
        {
            JsonTypeInfoKind.Object => (QueryType.Object, Fields(info, options, depth + 1), null),
            JsonTypeInfoKind.Enumerable when info.ElementType is { } elementType => Element(elementType, options, depth),
            JsonTypeInfoKind.Dictionary => (QueryType.Object, [], null),
            _ => (QueryType.Any, [], null),
        };
    }

    private static (QueryType, IReadOnlyList<QueryField>, QueryField?) Element(Type elementType, JsonSerializerOptions options, int depth)
    {
        (QueryType type, IReadOnlyList<QueryField> fields, QueryField? element) = Describe(elementType, options, depth + 1);
        return (QueryType.Array, [], new QueryField("[]", type, false, false, FrozenDictionary<int, string>.Empty, fields, element));
    }

    private static JsonTypeInfo? TryGetTypeInfo(Type type, JsonSerializerOptions options)
    {
        try
        {
            return options.TryGetTypeInfo(type, out JsonTypeInfo? info) ? info : null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private string ComputeFingerprint()
    {
        var shape = new StringBuilder();
        foreach (QueryEventType e in _events.Values.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            foreach (QueryEventVersion v in e.Versions)
            {
                shape.Append("e:").Append(e.Name).Append(':').Append(v.Version);
                Append(shape, v.Fields);
            }
        }

        foreach (QueryReadModel r in _readModels.Values.OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            shape.Append("r:").Append(r.Collection);
            Append(shape, r.Fields);
        }

        foreach (QueryAggregate a in _aggregates.Values.OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            shape.Append("a:").Append(a.Name);
            Append(shape, a.Fields);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(shape.ToString())));
    }

    private static void Append(StringBuilder shape, IReadOnlyList<QueryField> fields)
    {
        foreach (QueryField f in fields)
        {
            shape.Append('|').Append(f.Name).Append(':').Append(f.Type).Append(f.Queryable ? "q" : "").Append(f.PersonalData ? "p" : "");
            Append(shape, f.Fields);
            if (f.Element is { } element)
            {
                Append(shape, [element]);
            }
        }
    }
}

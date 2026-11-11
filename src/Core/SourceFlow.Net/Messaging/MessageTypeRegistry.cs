using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SourceFlow.Messaging;

/// <summary>Everything the runtime knows about one registered message type (doc 04 §2.1).</summary>
public sealed class MessageTypeInfo
{
    private Func<object, object>? _upcast;

    internal MessageTypeInfo(string name, int version, MessageKind kind, Type clrType, JsonTypeInfo jsonTypeInfo)
    {
        Name = name;
        Version = version;
        Kind = kind;
        ClrType = clrType;
        JsonTypeInfo = jsonTypeInfo;
    }

    /// <summary>The logical name.</summary>
    public string Name { get; }

    /// <summary>The schema version.</summary>
    public int Version { get; }

    /// <summary>The message kind.</summary>
    public MessageKind Kind { get; }

    /// <summary>The CLR type.</summary>
    public Type ClrType { get; }

    /// <summary>The source-generated JSON metadata.</summary>
    public JsonTypeInfo JsonTypeInfo { get; }

    /// <summary>The next version this type upcasts to when read from storage, if any.</summary>
    public MessageTypeInfo? UpcastsTo { get; private set; }

    /// <summary>The CloudEvents type, <c>{name}.v{version}</c>.</summary>
    public string CloudEventType => CloudEvents.FormatType(Name, Version);

    internal void SetUpcaster(MessageTypeInfo target, Func<object, object> upcast)
    {
        UpcastsTo = target;
        _upcast = upcast;
    }

    internal object UpcastOnce(object message)
    {
        return _upcast!(message);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{Name}.v{Version} ({ClrType.Name})";
    }
}

/// <summary>
/// The frozen allow-list of message types (ADR-005, GAP-10). Envelopes whose <c>(type, version)</c> is not registered
/// are never deserialized.
/// </summary>
public sealed class MessageTypeRegistry
{
    private readonly FrozenDictionary<(string Name, int Version), MessageTypeInfo> _byName;
    private readonly FrozenDictionary<Type, MessageTypeInfo> _byType;

    internal MessageTypeRegistry(IEnumerable<MessageTypeInfo> types, JsonSerializerOptions options)
    {
        MessageTypeInfo[] all = types.ToArray();
        _byName = all.ToFrozenDictionary(t => (t.Name, t.Version));
        _byType = all.ToFrozenDictionary(t => t.ClrType);
        Options = options;
    }

    /// <summary>The serializer options shared by every message (camelCase, strict numbers, no duplicate properties).</summary>
    public JsonSerializerOptions Options { get; }

    /// <summary>Every registered type.</summary>
    public IReadOnlyCollection<MessageTypeInfo> All => _byType.Values;

    /// <summary>Finds a type by logical name and version.</summary>
    public bool TryGet(string name, int version, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MessageTypeInfo? info)
    {
        return _byName.TryGetValue((name, version), out info);
    }

    /// <summary>Finds a type by CLR type.</summary>
    public bool TryGet(Type clrType, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MessageTypeInfo? info)
    {
        return _byType.TryGetValue(clrType, out info);
    }

    /// <summary>Gets a type by CLR type.</summary>
    /// <exception cref="UnknownMessageTypeException">The type is not registered in any module.</exception>
    public MessageTypeInfo Get(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        return _byType.TryGetValue(clrType, out MessageTypeInfo? info)
            ? info
            : throw new UnknownMessageTypeException(clrType.FullName ?? clrType.Name, 0,
                $"CLR type '{clrType.FullName}' is not a registered message. Add [MessageType] and register its module.");
    }

    /// <summary>Gets a type by logical name and version.</summary>
    /// <exception cref="UnknownMessageTypeException">The pair is not registered.</exception>
    public MessageTypeInfo Get(string name, int version)
    {
        return TryGet(name, version, out MessageTypeInfo? info) ? info : throw new UnknownMessageTypeException(name, version);
    }

    /// <summary>Applies the upcaster chain to a message read from storage (doc 03 §2.1). Wire messages are not upcast.</summary>
    public object UpcastToLatest(object message, MessageTypeInfo type, out MessageTypeInfo latest)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(type);
        latest = type;
        object current = message;
        int guard = 0;
        while (latest.UpcastsTo is not null)
        {
            current = latest.UpcastOnce(current);
            latest = latest.UpcastsTo;
            if (++guard > 64)
            {
                throw new InvalidOperationException($"Upcaster chain from {type} is too long or cyclic.");
            }
        }

        return current;
    }
}

/// <summary>Builds a <see cref="MessageTypeRegistry"/> from module registrations and validates it (UT-MSG-04/05/07).</summary>
public sealed class MessageTypeRegistryBuilder
{
    private readonly List<(Type ClrType, string Name, int Version, MessageKind Kind)> _types = [];
    private readonly List<(Type From, Type To, Func<object, object> Upcast)> _upcasters = [];

    /// <summary>Registers a message type.</summary>
    public MessageTypeRegistryBuilder Add(Type clrType, string name, int version)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "Message versions start at 1 (DM-R1).");
        }

        if (!IsValidName(name))
        {
            throw new ArgumentException($"Message type name '{name}' must be lowercase, dot-separated words (DM-R1).", nameof(name));
        }

        _types.Add((clrType, name, version, KindOf(clrType)));
        return this;
    }

    /// <summary>Registers an upcaster between two registered types.</summary>
    public MessageTypeRegistryBuilder AddUpcaster(Type from, Type to, Func<object, object> upcast)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(upcast);
        _upcasters.Add((from, to, upcast));
        return this;
    }

    /// <summary>Validates the registrations and freezes them.</summary>
    public MessageTypeRegistry Build(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.TypeInfoResolver is null)
        {
            throw new InvalidOperationException("The serializer options need the modules' source-generated resolvers (DC-1).");
        }

        options.MakeReadOnly();
        var byName = new Dictionary<(string, int), MessageTypeInfo>();
        var byType = new Dictionary<Type, MessageTypeInfo>();
        foreach ((Type clrType, string name, int version, MessageKind kind) in _types)
        {
            if (byType.TryGetValue(clrType, out MessageTypeInfo? existingType))
            {
                if (existingType.Name == name && existingType.Version == version)
                {
                    continue; // the same module registered twice
                }

                throw new InvalidOperationException($"CLR type '{clrType.FullName}' is registered twice, as {existingType} and as {name}.v{version}.");
            }

            if (byName.TryGetValue((name, version), out MessageTypeInfo? existing))
            {
                throw new InvalidOperationException(
                    $"Message type ('{name}', {version}) is declared by both '{existing.ClrType.FullName}' and '{clrType.FullName}'. " +
                    "Logical names must be unique across every module in the deployment (SF0002, DM-R1).");
            }

            JsonTypeInfo jsonTypeInfo = ResolveJsonTypeInfo(options, clrType);
            var info = new MessageTypeInfo(name, version, kind, clrType, jsonTypeInfo);
            byName[(name, version)] = info;
            byType[clrType] = info;
        }

        foreach ((Type from, Type to, Func<object, object> upcast) in _upcasters)
        {
            if (!byType.TryGetValue(from, out MessageTypeInfo? source) || !byType.TryGetValue(to, out MessageTypeInfo? target))
            {
                throw new InvalidOperationException($"Upcaster {from.Name} → {to.Name} references an unregistered message type.");
            }

            if (source.Name != target.Name || target.Version != source.Version + 1)
            {
                throw new InvalidOperationException($"Upcaster {source} → {target} must go from one version of a name to the next.");
            }

            source.SetUpcaster(target, upcast);
        }

        foreach (IGrouping<string, MessageTypeInfo> group in byName.Values.GroupBy(t => t.Name))
        {
            MessageTypeInfo[] versions = group.OrderBy(t => t.Version).ToArray();
            for (int i = 0; i < versions.Length - 1; i++)
            {
                if (versions[i].UpcastsTo is null && versions[i].Kind == MessageKind.Event)
                {
                    throw new InvalidOperationException(
                        $"Event '{versions[i].Name}' has versions {versions[i].Version} and {versions[i + 1].Version} but no upcaster between them (doc 03 §2.1).");
                }
            }
        }

        return new MessageTypeRegistry(byType.Values, options);
    }

    private static JsonTypeInfo ResolveJsonTypeInfo(JsonSerializerOptions options, Type clrType)
    {
        JsonTypeInfo? info = null;
        try
        {
            info = options.TypeInfoResolver?.GetTypeInfo(clrType, options);
        }
        catch (NotSupportedException)
        {
            info = null;
        }

        return info ?? throw new InvalidOperationException(
            $"No JSON metadata for '{clrType.FullName}'. Add [JsonSerializable(typeof({clrType.Name}))] to the module's JsonSerializerContext (SF0010, DC-1).");
    }

    private static MessageKind KindOf(Type type)
    {
        if (typeof(ICommand).IsAssignableFrom(type))
        {
            return MessageKind.Command;
        }

        if (typeof(IEvent).IsAssignableFrom(type))
        {
            return MessageKind.Event;
        }

        if (typeof(IIntegrationEvent).IsAssignableFrom(type))
        {
            return MessageKind.IntegrationEvent;
        }

        return typeof(ITimeout).IsAssignableFrom(type)
            ? MessageKind.Timeout
            : throw new ArgumentException($"'{type.FullName}' does not implement ICommand, IEvent, IIntegrationEvent or ITimeout.", nameof(type));
    }

    private static bool IsValidName(string name)
    {
        if (name.StartsWith('.') || name.EndsWith('.') || name.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!(c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c is '.' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>An envelope's <c>(type, version)</c> is not in the registry; it is parked, never deserialized (GAP-10).</summary>
public sealed class UnknownMessageTypeException : Exception
{
    /// <summary>Creates the exception.</summary>
    public UnknownMessageTypeException(string type, int version, string? message = null)
        : base(message ?? $"Message type ('{type}', {version}) is not registered; the message is not deserialized (allow-list, GAP-10).")
    {
        Type = type;
        Version = version;
    }

    /// <summary>The logical type name.</summary>
    public string Type { get; }

    /// <summary>The type version.</summary>
    public int Version { get; }
}

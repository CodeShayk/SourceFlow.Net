using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SourceFlow.Messaging;
using SourceFlow.Runtime;

namespace SourceFlow.Catalog;

/// <summary>What a schema describes (doc 14 §2.2).</summary>
public enum SchemaKind
{
    /// <summary>A command.</summary>
    Command,

    /// <summary>A domain event.</summary>
    Event,

    /// <summary>An integration event (a public contract).</summary>
    IntegrationEvent,

    /// <summary>A timeout.</summary>
    Timeout,

    /// <summary>A reply, such as <see cref="CommandRejected{TCommand}"/>.</summary>
    Reply,

    /// <summary>A read-model document (<c>name</c> = projection, <c>version</c> = projection version).</summary>
    ReadModel,

    /// <summary>A process manager's state.</summary>
    ProcessState,
}

/// <summary>One schema: a <c>(type, version)</c>, its kind and its JSON Schema, with a canonical text and fingerprint.</summary>
public sealed class SchemaEntry
{
    /// <summary>Creates an entry; the canonical text and fingerprint are computed from <paramref name="schema"/>.</summary>
    public SchemaEntry(string type, int version, SchemaKind kind, JsonObject schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(schema);
        Type = type;
        Version = version;
        Kind = kind;
        Schema = schema;
        Canonical = JsonSchemaGenerator.Canonical(schema);
        Fingerprint = JsonSchemaGenerator.Fingerprint(Canonical);
    }

    /// <summary>The logical type name (message type, projection or process name).</summary>
    public string Type { get; }

    /// <summary>The version.</summary>
    public int Version { get; }

    /// <summary>The kind.</summary>
    public SchemaKind Kind { get; }

    /// <summary>The JSON Schema 2020-12 document.</summary>
    public JsonObject Schema { get; }

    /// <summary>The canonical schema text (sorted keys, no whitespace).</summary>
    public string Canonical { get; }

    /// <summary>The SHA-256 of <see cref="Canonical"/> (<c>sha256:…</c>). A published <c>(type, version)</c> never changes it.</summary>
    public string Fingerprint { get; }

    /// <summary>The version this one upcasts to when read from storage, if any (events, doc 03 §2.1).</summary>
    public int? UpcastsTo => Schema["x-sourceflow"]?["upcastsTo"]?.GetValue<int>();

    /// <summary>The kind's wire name: <c>command</c>, <c>event</c>, <c>integration-event</c>, <c>timeout</c>, <c>reply</c>, <c>readmodel</c>, <c>process-state</c>.</summary>
    public static string KindName(SchemaKind kind)
    {
        return kind switch
        {
            SchemaKind.Command => "command",
            SchemaKind.Event => "event",
            SchemaKind.IntegrationEvent => "integration-event",
            SchemaKind.Timeout => "timeout",
            SchemaKind.Reply => "reply",
            SchemaKind.ReadModel => "readmodel",
            _ => "process-state",
        };
    }

    /// <summary>Parses a kind's wire name.</summary>
    public static SchemaKind ParseKind(string name)
    {
        return name switch
        {
            "command" => SchemaKind.Command,
            "event" => SchemaKind.Event,
            "integration-event" => SchemaKind.IntegrationEvent,
            "timeout" => SchemaKind.Timeout,
            "reply" => SchemaKind.Reply,
            "readmodel" => SchemaKind.ReadModel,
            "process-state" => SchemaKind.ProcessState,
            _ => throw new FormatException($"Unknown schema kind '{name}'."),
        };
    }
}

/// <summary>A registered <c>(type, version)</c> whose fingerprint differs from this service's schema.</summary>
/// <param name="Type">The type.</param>
/// <param name="Version">The version.</param>
/// <param name="RegisteredFingerprint">The fingerprint already registered (published).</param>
/// <param name="CurrentFingerprint">This service's fingerprint.</param>
public sealed record SchemaConflict(string Type, int Version, string RegisteredFingerprint, string CurrentFingerprint);

/// <summary>Startup found published schemas that this service changed (doc 14 §2.2): a published version is immutable.</summary>
public sealed class SchemaConflictException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public SchemaConflictException(IReadOnlyList<SchemaConflict> conflicts)
        : base("Published schemas changed without a new version (a published (type, version) is immutable; add a version and an upcaster): "
            + string.Join(", ", conflicts.Select(c => $"{c.Type} v{c.Version}")))
    {
        Conflicts = conflicts;
    }

    /// <summary>The conflicts.</summary>
    public IReadOnlyList<SchemaConflict> Conflicts { get; }
}

/// <summary>
/// The schema catalog of one service (doc 14 §2): a JSON Schema per message type (every version), read-model document and
/// process state, built from the runtime model. It is registered at startup (<see cref="Storage.ISchemaStore"/>), checked
/// for compatibility against a baseline lock file in CI (<see cref="SchemaCompatibility"/>) and exported as AsyncAPI
/// (<see cref="AsyncApiExporter"/>).
/// </summary>
public sealed class SchemaCatalog
{
    private readonly Dictionary<(string Type, int Version), SchemaEntry> _entries;

    /// <summary>Creates a catalog.</summary>
    public SchemaCatalog(IEnumerable<SchemaEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Entries = [.. entries.OrderBy(e => e.Type, StringComparer.Ordinal).ThenBy(e => e.Version)];
        _entries = Entries.ToDictionary(e => (e.Type, e.Version));
    }

    /// <summary>The schemas, ordered by type and version.</summary>
    public IReadOnlyList<SchemaEntry> Entries { get; }

    /// <summary>Finds a schema.</summary>
    public bool TryGet(string type, int version, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SchemaEntry? entry)
    {
        return _entries.TryGetValue((type, version), out entry);
    }

    /// <summary>Builds the catalog of a service from its runtime model.</summary>
    public static SchemaCatalog Build(RuntimeModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        JsonSerializerOptions options = model.Registry.Options;
        var entries = new List<SchemaEntry>();
        foreach (MessageTypeInfo message in model.Registry.All)
        {
            SchemaKind kind = message.Kind switch
            {
                MessageKind.Command => SchemaKind.Command,
                MessageKind.Event => SchemaKind.Event,
                MessageKind.IntegrationEvent => SchemaKind.IntegrationEvent,
                MessageKind.Timeout => SchemaKind.Timeout,
                _ => SchemaKind.Reply,
            };
            JsonObject schema = Root(message.Name, message.Version, kind, message.ClrType.Name, JsonSchemaGenerator.Generate(message.JsonTypeInfo, options));
            if (message.UpcastsTo is { } next)
            {
                schema["x-sourceflow"]!["upcastsTo"] = next.Version;
            }

            entries.Add(new SchemaEntry(message.Name, message.Version, kind, schema));
        }

        foreach (DocumentProjectionDescriptor projection in model.Projections.OfType<DocumentProjectionDescriptor>())
        {
            JsonObject schema = Root(projection.Name, projection.Version, SchemaKind.ReadModel, projection.DocumentType.Name,
                JsonSchemaGenerator.Generate(model.JsonTypeInfoFor(projection.DocumentType), options));
            entries.Add(new SchemaEntry(projection.Name, projection.Version, SchemaKind.ReadModel, schema));
        }

        foreach (ProcessManagerDescriptor process in model.ProcessManagers)
        {
            JsonObject schema = Root(process.Name, 1, SchemaKind.ProcessState, process.StateType.Name,
                JsonSchemaGenerator.Generate(model.JsonTypeInfoFor(process.StateType), options));
            entries.Add(new SchemaEntry(process.Name, 1, SchemaKind.ProcessState, schema));
        }

        return new SchemaCatalog(entries);
    }

    /// <summary>Writes the catalog as a lock file (<c>schemas.lock.json</c>), the CI baseline for compatibility checks.</summary>
    public string ToLockFile()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("sourceflowSchemas", 1);
            writer.WriteStartArray("schemas");
            foreach (SchemaEntry entry in Entries)
            {
                writer.WriteStartObject();
                writer.WriteString("type", entry.Type);
                writer.WriteNumber("version", entry.Version);
                writer.WriteString("kind", SchemaEntry.KindName(entry.Kind));
                writer.WriteString("fingerprint", entry.Fingerprint);
                writer.WritePropertyName("schema");
                using JsonDocument canonical = JsonDocument.Parse(entry.Canonical);
                canonical.WriteTo(writer);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Reads a lock file written by <see cref="ToLockFile"/>.</summary>
    public static SchemaCatalog FromLockFile(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonNode root = JsonNode.Parse(json) ?? throw new FormatException("The schema lock file is empty.");
        var entries = new List<SchemaEntry>();
        foreach (JsonNode? item in root["schemas"]?.AsArray() ?? [])
        {
            if (item is null)
            {
                continue;
            }

            entries.Add(new SchemaEntry(
                item["type"]!.GetValue<string>(),
                item["version"]!.GetValue<int>(),
                SchemaEntry.ParseKind(item["kind"]!.GetValue<string>()),
                item["schema"]!.AsObject().DeepClone().AsObject()));
        }

        return new SchemaCatalog(entries);
    }

    private static JsonObject Root(string type, int version, SchemaKind kind, string title, JsonObject body)
    {
        var root = new JsonObject
        {
            ["$schema"] = JsonSchemaGenerator.Dialect,
            ["$id"] = $"urn:sourceflow:schema:{type}:{version}",
            ["title"] = title,
        };
        foreach (KeyValuePair<string, JsonNode?> property in body.ToArray())
        {
            body.Remove(property.Key);
            root[property.Key] = property.Value;
        }

        root["x-sourceflow"] = new JsonObject { ["type"] = type, ["version"] = version, ["kind"] = SchemaEntry.KindName(kind) };
        return root;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SourceFlow.Studio.Dsl.Json;

namespace SourceFlow.Studio.Dsl.Manifests
{
    /// <summary>
    /// A service manifest (doc 15 §3, <c>manifestVersion: 1</c>): the merged one written by
    /// <c>dotnet sourceflow manifest export</c>, or the static part the generator puts in each assembly (then
    /// <see cref="Service"/>, <see cref="Endpoint"/> and the routing lists are empty).
    /// </summary>
    public sealed class ServiceManifest
    {
        /// <summary>The supported manifest version.</summary>
        public const int CurrentVersion = 1;

        private ServiceManifest()
        {
        }

        /// <summary>The manifest version (always <see cref="CurrentVersion"/>).</summary>
        public int ManifestVersion { get; private set; }

        /// <summary>The service name, or null in a static manifest.</summary>
        public string? Service { get; private set; }

        /// <summary>The service's endpoint, or null in a static manifest.</summary>
        public string? Endpoint { get; private set; }

        /// <summary>The assembly a static manifest came from, or null.</summary>
        public string? Assembly { get; private set; }

        /// <summary>The SourceFlow version the service was built with.</summary>
        public string? SourceFlowVersion { get; private set; }

        /// <summary>The message types the service registers.</summary>
        public IReadOnlyList<ManifestMessage> Messages { get; private set; } = Array.Empty<ManifestMessage>();

        /// <summary>The aggregates, with each command's events and rejection codes.</summary>
        public IReadOnlyList<ManifestAggregate> Aggregates { get; private set; } = Array.Empty<ManifestAggregate>();

        /// <summary>The process managers.</summary>
        public IReadOnlyList<ManifestProcessManager> ProcessManagers { get; private set; } = Array.Empty<ManifestProcessManager>();

        /// <summary>The projections.</summary>
        public IReadOnlyList<ManifestProjection> Projections { get; private set; } = Array.Empty<ManifestProjection>();

        /// <summary>Domain events published as integration events.</summary>
        public IReadOnlyList<ManifestPublication> Publishes { get; private set; } = Array.Empty<ManifestPublication>();

        /// <summary>Topic subscriptions.</summary>
        public IReadOnlyList<ManifestSubscription> Subscribes { get; private set; } = Array.Empty<ManifestSubscription>();

        /// <summary>Commands sent to other services.</summary>
        public IReadOnlyList<ManifestCommandRoute> CommandRoutes { get; private set; } = Array.Empty<ManifestCommandRoute>();

        /// <summary>The DSL workflows the service owns.</summary>
        public IReadOnlyList<ManifestWorkflow> Workflows { get; private set; } = Array.Empty<ManifestWorkflow>();

        /// <summary>The schema catalog reference, for example <c>catalog://payments</c>.</summary>
        public string? Schemas { get; private set; }

        /// <summary>The command <paramref name="reference"/> (<c>{name}.v{version}</c>) and its aggregate, if this service owns it.</summary>
        public bool TryGetCommand(string reference, out ManifestAggregate? aggregate, out ManifestCommand? command)
        {
            foreach (ManifestAggregate candidate in Aggregates)
            {
                foreach (ManifestCommand c in candidate.Commands)
                {
                    if (c.Ref == reference)
                    {
                        aggregate = candidate;
                        command = c;
                        return true;
                    }
                }
            }

            aggregate = null;
            command = null;
            return false;
        }

        /// <summary>The message type <paramref name="reference"/>, if registered.</summary>
        public ManifestMessage? FindMessage(string reference)
        {
            return Messages.FirstOrDefault(m => m.Ref == reference);
        }

        /// <summary>Parses a manifest.</summary>
        /// <exception cref="FormatException">The text is not a version-1 manifest.</exception>
        public static ServiceManifest Parse(string json)
        {
            JsonValue root = JsonLite.Parse(json);
            if (root.Members.Count == 0)
            {
                throw new FormatException("A service manifest is a JSON object.");
            }

            long? version = root["manifestVersion"]?.AsInteger;
            if (version != CurrentVersion)
            {
                throw new FormatException("Unsupported manifestVersion " + (version?.ToString(CultureInfo.InvariantCulture) ?? "(missing)") + "; this version of SourceFlow Studio reads manifestVersion 1.");
            }

            return new ServiceManifest
            {
                ManifestVersion = CurrentVersion,
                Service = root["service"]?.AsString,
                Endpoint = root["endpoint"]?.AsString,
                Assembly = root["assembly"]?.AsString,
                SourceFlowVersion = root["sourceflowVersion"]?.AsString,
                Schemas = root["schemas"]?.AsString,
                Messages = List(root["messages"], m => new ManifestMessage(
                    Required(m, "type"),
                    Int(m, "version"),
                    Required(m, "kind"),
                    m["clrType"]?.AsString,
                    m["aggregate"]?.AsString,
                    List(m["fields"], f => new ManifestField(Required(f, "name"), Required(f, "type"), f["nullable"]?.AsBoolean ?? false, f["personalData"]?.AsBoolean ?? false)))),
                Aggregates = List(root["aggregates"], a => new ManifestAggregate(
                    Required(a, "name"),
                    a["clrType"]?.AsString,
                    List(a["commands"], c => new ManifestCommand(Required(c, "type"), Int(c, "version"), Strings(c["emits"]), Strings(c["rejects"]))),
                    Strings(a["events"]))),
                ProcessManagers = List(root["processManagers"], p => new ManifestProcessManager(
                    Required(p, "name"), p["clrType"]?.AsString, Strings(p["handles"]), Strings(p["sends"]), Strings(p["schedules"]))),
                Projections = List(root["projections"], p => new ManifestProjection(
                    Required(p, "name"), Int(p, "version"), p["clrType"]?.AsString, Strings(p["handles"]))),
                Publishes = List(root["publishes"], p => new ManifestPublication(Required(p, "domainEvent"), Required(p, "as"), Required(p, "topic"))),
                Subscribes = List(root["subscribes"], s => new ManifestSubscription(Required(s, "topic"), Strings(s["only"]), s["where"]?.AsString)),
                CommandRoutes = List(root["commandRoutes"], r => new ManifestCommandRoute(Required(r, "command"), Required(r, "to"), r["replyOnReject"]?.AsBoolean ?? false)),
                Workflows = List(root["workflows"], w => new ManifestWorkflow(Required(w, "name"), Int(w, "version"), w["file"]?.AsString) { Fingerprint = w["fingerprint"]?.AsString }),
            };
        }

        private static IReadOnlyList<T> List<T>(JsonValue? array, Func<JsonValue, T> read)
        {
            return array is null ? Array.Empty<T>() : array.Items.Select(read).ToArray();
        }

        private static IReadOnlyList<string> Strings(JsonValue? array)
        {
            return array is null ? Array.Empty<string>() : array.Items.Select(i => i.AsString ?? throw new FormatException("Expected a string in a manifest list.")).ToArray();
        }

        private static string Required(JsonValue node, string name)
        {
            return node[name]?.AsString ?? throw new FormatException("A manifest entry has no '" + name + "'.");
        }

        private static int Int(JsonValue node, string name)
        {
            long? value = node[name]?.AsInteger;
            return value is null ? 1 : checked((int)value.Value);
        }
    }

    /// <summary>Formats message references.</summary>
    public static class MessageRef
    {
        /// <summary>The CloudEvents-style reference <c>{name}.v{version}</c>.</summary>
        public static string Of(string name, int version)
        {
            return name + ".v" + version.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Splits <c>{name}.v{version}</c>; false when the text has no version suffix.</summary>
        public static bool TryParse(string reference, out string name, out int version)
        {
            int index = reference?.LastIndexOf(".v", StringComparison.Ordinal) ?? -1;
            if (index > 0 && int.TryParse(reference!.Substring(index + 2), NumberStyles.None, CultureInfo.InvariantCulture, out version) && version > 0)
            {
                name = reference.Substring(0, index);
                return true;
            }

            name = string.Empty;
            version = 0;
            return false;
        }
    }

    /// <summary>A registered message type.</summary>
    public sealed record ManifestMessage(string Type, int Version, string Kind, string? ClrType, string? Aggregate, IReadOnlyList<ManifestField> Fields)
    {
        /// <summary>The reference, <c>{type}.v{version}</c>.</summary>
        public string Ref => MessageRef.Of(Type, Version);

        /// <summary>The field called <paramref name="name"/> (its JSON name), or null.</summary>
        public ManifestField? Field(string name)
        {
            return Fields.FirstOrDefault(f => f.Name == name);
        }
    }

    /// <summary>A message field: its JSON name and schema-like type.</summary>
    public sealed record ManifestField(string Name, string Type, bool Nullable, bool PersonalData);

    /// <summary>An aggregate.</summary>
    public sealed record ManifestAggregate(string Name, string? ClrType, IReadOnlyList<ManifestCommand> Commands, IReadOnlyList<string> Events);

    /// <summary>A command with the events it can produce and the codes it can reject with.</summary>
    public sealed record ManifestCommand(string Type, int Version, IReadOnlyList<string> Emits, IReadOnlyList<string> Rejects)
    {
        /// <summary>The reference, <c>{type}.v{version}</c>.</summary>
        public string Ref => MessageRef.Of(Type, Version);
    }

    /// <summary>A process manager.</summary>
    public sealed record ManifestProcessManager(string Name, string? ClrType, IReadOnlyList<string> Handles, IReadOnlyList<string> Sends, IReadOnlyList<string> Schedules);

    /// <summary>A projection.</summary>
    public sealed record ManifestProjection(string Name, int Version, string? ClrType, IReadOnlyList<string> Handles);

    /// <summary>A domain event published (as <see cref="As"/>) to a topic.</summary>
    public sealed record ManifestPublication(string DomainEvent, string As, string Topic);

    /// <summary>A topic subscription, optionally limited to some types or a CESQL filter.</summary>
    public sealed record ManifestSubscription(string Topic, IReadOnlyList<string> Only, string? Where);

    /// <summary>A command sent to another service's endpoint.</summary>
    public sealed record ManifestCommandRoute(string Command, string To, bool ReplyOnReject);

    /// <summary>A DSL workflow owned by the service, with the structural fingerprint of the published version (WF-V15).</summary>
    public sealed record ManifestWorkflow(string Name, int Version, string? File)
    {
        /// <summary>The structural fingerprint (<c>WorkflowFingerprint</c>), or null.</summary>
        public string? Fingerprint { get; init; }
    }
}

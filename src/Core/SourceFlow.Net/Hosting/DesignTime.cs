using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Runtime;

namespace SourceFlow.Hosting;

/// <summary>
/// Design-time mode (doc 15 §3.2, WP-S.2). With <c>SOURCEFLOW_DESIGN_TIME=1</c> the application's configuration runs as
/// usual, but SourceFlow writes the service manifest as soon as the host is built and ends the process: the host never
/// starts, so no hosted service, provisioning, storage or transport call runs. <c>dotnet sourceflow manifest export</c>
/// sets the variables.
/// </summary>
public static class SourceFlowDesignTime
{
    /// <summary>The variable that turns design-time mode on (<c>1</c> or <c>true</c>).</summary>
    public const string EnvironmentVariable = "SOURCEFLOW_DESIGN_TIME";

    /// <summary>Where the manifest is written; standard output when unset.</summary>
    public const string ManifestOutputVariable = "SOURCEFLOW_MANIFEST_OUT";

    /// <summary>Whether this process runs in design-time mode.</summary>
    public static bool IsActive => Environment.GetEnvironmentVariable(EnvironmentVariable) is { } value
        && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    internal static DesignTimeExport? FromEnvironment()
    {
        return IsActive
            ? new DesignTimeExport(Environment.GetEnvironmentVariable(ManifestOutputVariable), Environment.Exit, Console.Out, Console.Error)
            : null;
    }
}

/// <summary>
/// Builds the service manifest (doc 15 §3.1): the static manifests the generator put in each assembly
/// (<see cref="SourceFlowManifestAttribute"/>), filtered to what this service registers, merged with its routing:
/// command routes (with <c>replyOnReject</c>), publications and subscriptions. Reads configuration only.
/// </summary>
public static class SourceFlowManifestExporter
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The manifest of the service configured in <paramref name="services"/>, as indented JSON ending in a newline.</summary>
    public static string Export(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        RuntimeModel model = services.GetRequiredService<RuntimeModel>();
        RoutingTable routing = services.GetRequiredService<RoutingTable>();
        string service = services.GetRequiredService<SourceFlowServiceName>().Name;
        return Build(service, model, routing).ToJsonString(Indented) + "\n";
    }

    internal static JsonObject Build(string service, RuntimeModel model, RoutingTable routing)
    {
        // Membership comes from the runtime model; emits, rejects and fields from the static manifests.
        var messages = model.Registry.All.ToDictionary(m => m.CloudEventType, StringComparer.Ordinal);
        HashSet<string> aggregates = [.. model.Aggregates.Keys.Select(Name), .. model.CommandHandlers.Values.Select(h => Name(h.AggregateType))];
        var handlerCommands = model.CommandHandlers.Keys.Select(t => model.Registry.Get(t).CloudEventType).ToHashSet(StringComparer.Ordinal);
        var decidedCommands = model.Aggregates.Values.SelectMany(a => a.DecidedCommands).Select(t => model.Registry.Get(t).CloudEventType).ToHashSet(StringComparer.Ordinal);
        HashSet<string> processes = [.. model.ProcessManagers.Select(p => Name(p.StateType))];
        HashSet<string> projections = [.. model.Projections.Select(p => Name(p.ProjectionType))];

        var foundMessages = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        var foundAggregates = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        var foundProcesses = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        var foundProjections = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        var foundWorkflows = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (JsonObject manifest in StaticManifests(model))
        {
            foreach (JsonObject message in Objects(manifest["messages"]))
            {
                string reference = Ref(message);
                if (messages.ContainsKey(reference))
                {
                    foundMessages.TryAdd(reference, (JsonObject)message.DeepClone());
                }
            }

            foreach (JsonObject aggregate in Objects(manifest["aggregates"]))
            {
                string clr = Str(aggregate, "clrType");
                if (!aggregates.Contains(clr))
                {
                    continue;
                }

                var copy = (JsonObject)aggregate.DeepClone();
                var commands = new JsonArray([.. Objects(copy["commands"])
                    .Where(c => decidedCommands.Contains(Ref(c)) || handlerCommands.Contains(Ref(c)))
                    .Select(c => c.DeepClone())]);
                copy["commands"] = commands;
                if (foundAggregates.TryGetValue(clr, out JsonObject? existing))
                {
                    // The same aggregate described by two assemblies (a handler in another one): union of the commands.
                    var known = Objects(existing["commands"]).Select(Ref).ToHashSet(StringComparer.Ordinal);
                    foreach (JsonObject command in Objects(commands).Where(c => !known.Contains(Ref(c))))
                    {
                        existing["commands"]!.AsArray().Add(command.DeepClone());
                    }
                }
                else
                {
                    foundAggregates[clr] = copy;
                }
            }

            Collect(manifest["processManagers"], processes, foundProcesses);
            Collect(manifest["projections"], projections, foundProjections);
            foreach (JsonObject workflow in Objects(manifest["workflows"]))
            {
                foundWorkflows.TryAdd(Str(workflow, "name") + "@" + workflow["version"], (JsonObject)workflow.DeepClone());
            }
        }

        foreach (JsonObject aggregate in foundAggregates.Values)
        {
            aggregate["commands"] = new JsonArray([.. Objects(aggregate["commands"]).OrderBy(Ref, StringComparer.Ordinal).Select(c => c.DeepClone())]);
        }

        var publishes = new JsonArray();
        foreach ((Type domainEvent, IReadOnlyList<PublicationRoute> publications) in routing.Publications.OrderBy(p => model.Registry.Get(p.Key).CloudEventType, StringComparer.Ordinal))
        {
            foreach (PublicationRoute route in publications.OrderBy(r => r.Destination, StringComparer.Ordinal).ThenBy(r => model.Registry.Get(r.ContractType).CloudEventType, StringComparer.Ordinal))
            {
                publishes.Add((JsonNode)new JsonObject
                {
                    ["domainEvent"] = model.Registry.Get(domainEvent).CloudEventType,
                    ["as"] = model.Registry.Get(route.ContractType).CloudEventType,
                    ["topic"] = route.Destination,
                });
            }
        }

        var subscribes = new JsonArray();
        foreach (SubscriptionRoute subscription in routing.Subscriptions.OrderBy(s => s.Topic, StringComparer.Ordinal))
        {
            var entry = new JsonObject { ["topic"] = subscription.Topic };
            if (subscription.OnlyTypes.Count > 0)
            {
                entry["only"] = new JsonArray([.. subscription.OnlyTypes.Select(t => model.Registry.Get(t).CloudEventType).Order(StringComparer.Ordinal).Select(t => (JsonNode)t)]);
            }

            if (subscription.Where is { } where)
            {
                entry["where"] = where;
            }

            subscribes.Add((JsonNode)entry);
        }

        var routes = new JsonArray();
        foreach ((string command, CommandRoute route) in routing.Commands
            .Select(c => (model.Registry.Get(c.Key).CloudEventType, c.Value))
            .OrderBy(c => c.CloudEventType, StringComparer.Ordinal))
        {
            routes.Add((JsonNode)new JsonObject { ["command"] = command, ["to"] = route.Endpoint, ["replyOnReject"] = route.ReplyOnReject });
        }

        return new JsonObject
        {
            ["manifestVersion"] = 1,
            ["service"] = service,
            ["endpoint"] = routing.Endpoint,
            ["sourceflowVersion"] = typeof(SourceFlowManifestExporter).Assembly.GetName().Version?.ToString(3),
            ["messages"] = new JsonArray([.. foundMessages.Values]),
            ["aggregates"] = new JsonArray([.. foundAggregates.Values.OrderBy(a => Str(a, "name"), StringComparer.Ordinal)]),
            ["processManagers"] = new JsonArray([.. foundProcesses.Values.OrderBy(a => Str(a, "name"), StringComparer.Ordinal)]),
            ["projections"] = new JsonArray([.. foundProjections.Values.OrderBy(a => Str(a, "name"), StringComparer.Ordinal)]),
            ["publishes"] = publishes,
            ["subscribes"] = subscribes,
            ["commandRoutes"] = routes,
            ["workflows"] = new JsonArray([.. foundWorkflows.Values]),
            ["schemas"] = "catalog://" + service,
        };
    }

    private static IEnumerable<JsonObject> StaticManifests(RuntimeModel model)
    {
        IEnumerable<Type> types = model.Registry.All.Select(m => m.ClrType)
            .Concat(model.Aggregates.Keys)
            .Concat(model.CommandHandlers.Values.Select(h => h.HandlerType))
            .Concat(model.ProcessManagers.Select(p => p.StateType))
            .Concat(model.Projections.Select(p => p.ProjectionType));
        foreach (Assembly assembly in types.Select(t => t.Assembly).Distinct().OrderBy(a => a.GetName().Name, StringComparer.Ordinal))
        {
            if (assembly.GetCustomAttribute<SourceFlowManifestAttribute>() is { } attribute
                && JsonNode.Parse(attribute.Json) is JsonObject manifest
                && manifest["manifestVersion"]?.GetValue<int>() == 1)
            {
                yield return manifest;
            }
        }
    }

    private static void Collect(JsonNode? entries, HashSet<string> registered, SortedDictionary<string, JsonObject> found)
    {
        foreach (JsonObject entry in Objects(entries))
        {
            string clr = Str(entry, "clrType");
            if (registered.Contains(clr))
            {
                found.TryAdd(clr, (JsonObject)entry.DeepClone());
            }
        }
    }

    private static IEnumerable<JsonObject> Objects(JsonNode? array)
    {
        return array is JsonArray items ? items.OfType<JsonObject>() : [];
    }

    private static string Ref(JsonObject message)
    {
        return CloudEvents.FormatType(Str(message, "type"), message["version"]?.GetValue<int>() ?? 1);
    }

    private static string Str(JsonObject node, string name)
    {
        return node[name]?.GetValue<string>() ?? string.Empty;
    }

    private static string Name(Type type)
    {
        return type.FullName ?? type.Name;
    }
}

/// <summary>The configured service name, for the manifest.</summary>
internal sealed record SourceFlowServiceName(string Name);

/// <summary>A design-time export request: where to write and how to end the process.</summary>
internal sealed record DesignTimeExport(string? OutputPath, Action<int> Exit, TextWriter Output, TextWriter Error);

/// <summary>
/// Listens for the generic host's <c>HostBuilt</c> diagnostic event (written by <c>HostBuilder</c> and
/// <c>HostApplicationBuilder</c>, so also by <c>WebApplicationBuilder</c>) and exports when the built host is a
/// design-time one. Hosts without a <see cref="DesignTimeExport"/> are left alone.
/// </summary>
internal sealed class DesignTimeHook : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private const string HostingListener = "Microsoft.Extensions.Hosting";
    private const string HostBuilt = "HostBuilt";
    private static int _installed;
    private readonly List<IDisposable> _subscriptions = [];

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 0)
        {
            var hook = new DesignTimeHook();
            hook._subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(hook));
        }
    }

    internal static void Run(IServiceProvider services, DesignTimeExport export)
    {
        int code;
        try
        {
            string json = SourceFlowManifestExporter.Export(services);
            if (string.IsNullOrEmpty(export.OutputPath))
            {
                export.Output.Write(json);
            }
            else
            {
                string path = Path.GetFullPath(export.OutputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            code = 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or JsonException)
        {
            export.Error.WriteLine("SourceFlow design-time export failed: " + ex.Message);
            code = 1;
        }

        export.Exit(code);
    }

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener value)
    {
        if (value.Name == HostingListener)
        {
            lock (_subscriptions)
            {
                _subscriptions.Add(value.Subscribe(this));
            }
        }
    }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Key == HostBuilt && value.Value is IHost host && host.Services.GetService<DesignTimeExport>() is { } export)
        {
            Run(host.Services, export);
        }
    }

    void IObserver<DiagnosticListener>.OnCompleted()
    {
    }

    void IObserver<DiagnosticListener>.OnError(Exception error)
    {
    }

    void IObserver<KeyValuePair<string, object?>>.OnCompleted()
    {
    }

    void IObserver<KeyValuePair<string, object?>>.OnError(Exception error)
    {
    }
}

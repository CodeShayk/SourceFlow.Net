using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SourceFlow.Diagnostics;

namespace SourceFlow.Tool;

/// <summary>
/// Generates the reference Grafana dashboard and Prometheus alert rules from the instrument catalogue
/// (<see cref="SourceFlowTelemetry.Catalogue"/>, doc 08 §3, §6), so they cannot drift from the code
/// (<c>dotnet sourceflow observability export</c>, committed in <c>deploy/observability</c>).
/// </summary>
/// <remarks>
/// Metric names follow the OpenTelemetry-to-Prometheus conversion of the OpenTelemetry Prometheus exporter and the
/// Collector: dots become underscores, a unit of seconds adds <c>_seconds</c>, annotation units (<c>{message}</c>) add
/// nothing, counters end in <c>_total</c>, and histograms are read through <c>_bucket</c>, <c>_sum</c> and <c>_count</c>.
/// The service is the <c>job</c> label (from <c>service.name</c>).
/// </remarks>
public static class Observability
{
    /// <summary>The dashboard file name.</summary>
    public const string DashboardFile = "sourceflow-dashboard.json";

    /// <summary>The alert rules file name.</summary>
    public const string AlertsFile = "sourceflow-alerts.yaml";

    private const string Job = "job=~\"$job\"";
    private const string Rate = "$__rate_interval";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, IndentSize = 2, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The service-level objectives of doc 08 §6, as alerts (name, expression, duration, severity, summary).</summary>
    public static IReadOnlyList<AlertRule> Alerts { get; } =
    [
        new("SourceFlowDeadLetterParked", $"sum by (job, consumer, reason) (increase({Prom(SourceFlowTelemetry.Instruments.DeadLetterParked)}[5m])) > 0", "0m", "page",
            "{{ $labels.job }} parked a message from {{ $labels.consumer }} ({{ $labels.reason }}). Inspect it with `dotnet sourceflow dlq list`.",
            "Correctness: any parked message needs an operator (doc 08 §6)."),
        new("SourceFlowSubscriptionStalled", $"max by (job, sourceflow_subscription, sourceflow_partition) ({Prom(SourceFlowTelemetry.Instruments.SubscriptionStalled)}) > 0", "5m", "page",
            "{{ $labels.job }}: subscription {{ $labels.sourceflow_subscription }} partition {{ $labels.sourceflow_partition }} is halted at a failing event.",
            "A Stop-policy projection is stuck on a poison event; fix it or skip the event (`dotnet sourceflow projection skip`)."),
        new("SourceFlowOutboxDelayed", $"max by (job) ({Prom(SourceFlowTelemetry.Instruments.OutboxAge)}) > 60", "5m", "page",
            "{{ $labels.job }}: the oldest due outbox message has waited {{ $value | humanizeDuration }}.",
            "Delivery durability SLO: max outbox age < 60 s. Check the relay and the broker."),
        new("SourceFlowReadModelsStale", $"max by (job) ({Prom(SourceFlowTelemetry.Instruments.SubscriptionLagTime)}) > 5", "10m", "warning",
            "{{ $labels.job }}: read models are {{ $value | humanizeDuration }} behind.",
            "Read-model freshness SLO: lag time < 5 s."),
        new("SourceFlowWriteLatency", $"histogram_quantile(0.99, sum by (job, le) (rate({Prom(SourceFlowTelemetry.Instruments.CommandDuration)}_bucket[5m]))) > 0.05", "10m", "warning",
            "{{ $labels.job }}: p99 command latency is {{ $value | humanizeDuration }}.",
            "Write latency SLO: p99 < 50 ms."),
        new("SourceFlowWriteErrors", $"sum by (job) (rate({Prom(SourceFlowTelemetry.Instruments.CommandDuration)}_count{{sourceflow_outcome=\"failed\"}}[30m])) / sum by (job) (rate({Prom(SourceFlowTelemetry.Instruments.CommandDuration)}_count[30m])) > 0.001", "30m", "page",
            "{{ $labels.job }}: {{ $value | humanizePercentage }} of commands fail.",
            "Write availability SLO: 99.9 % of commands succeed (business rejections and conflicts count as successes)."),
        new("SourceFlowHorizonHeld", $"max by (job) ({Prom(SourceFlowTelemetry.Instruments.HorizonAge)}) > 30", "5m", "warning",
            "{{ $labels.job }}: a writing transaction has held the subscription horizon for {{ $value | humanizeDuration }}.",
            "A long transaction delays every subscription (doc 05 §5.4). Find it in pg_stat_activity."),
    ];

    /// <summary>The Prometheus name of an instrument (without the histogram suffixes).</summary>
    public static string Prom(string instrument)
    {
        InstrumentInfo info = SourceFlowTelemetry.Catalogue.SingleOrDefault(i => i.Name == instrument)
            ?? throw new ArgumentException($"'{instrument}' is not in the instrument catalogue.", nameof(instrument));
        string name = Label(info.Name);
        if (info.Unit == "s" && !name.EndsWith("_seconds", StringComparison.Ordinal))
        {
            name += "_seconds";
        }

        return info.Kind == InstrumentKind.Counter ? name + "_total" : name;
    }

    /// <summary>The Prometheus label name of a tag.</summary>
    public static string Label(string tag)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        return string.Create(tag.Length, tag, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                span[i] = char.IsAsciiLetterOrDigit(source[i]) ? source[i] : '_';
            }
        });
    }

    /// <summary>The Grafana dashboard (JSON, LF line endings, trailing newline).</summary>
    public static string Dashboard()
    {
        var panels = new JsonArray();
        int id = 1;
        int y = 0;

        panels.Add(Row(id++, "Service-level objectives (doc 08 §6)", y++));
        (string Title, string Expr, string Unit, string Description)[] slos =
        [
            ("Write availability", $"1 - (sum(rate({Prom(SourceFlowTelemetry.Instruments.CommandDuration)}_count{{{Job},sourceflow_outcome=\"failed\"}}[{Rate}])) or vector(0)) / sum(rate({Prom(SourceFlowTelemetry.Instruments.CommandDuration)}_count{{{Job}}}[{Rate}]))", "percentunit", "Objective 99.9 %: commands that did not fail."),
            ("Write latency p99", $"histogram_quantile(0.99, sum by (le) (rate({Prom(SourceFlowTelemetry.Instruments.CommandDuration)}_bucket{{{Job}}}[{Rate}])))", "s", "Objective < 50 ms."),
            ("Read-model freshness", $"max({Prom(SourceFlowTelemetry.Instruments.SubscriptionLagTime)}{{{Job}}})", "s", "Objective < 5 s: the oldest unprocessed event."),
            ("Delivery durability", $"max({Prom(SourceFlowTelemetry.Instruments.OutboxAge)}{{{Job}}})", "s", "Objective < 60 s: the oldest due outbox message."),
            ("Parked messages (1 h)", $"sum(increase({Prom(SourceFlowTelemetry.Instruments.DeadLetterParked)}{{{Job}}}[1h]))", "short", "Objective 0: page on any."),
        ];
        for (int i = 0; i < slos.Length; i++)
        {
            panels.Add(Panel(id++, "stat", slos[i].Title, slos[i].Description, slos[i].Unit, x: i * 5, y, w: i == slos.Length - 1 ? 4 : 5, h: 4, [(slos[i].Expr, slos[i].Title)]));
        }

        y += 4;
        foreach (IGrouping<string, InstrumentInfo> group in SourceFlowTelemetry.Catalogue.GroupBy(Group))
        {
            panels.Add(Row(id++, group.Key, y++));
            int column = 0;
            foreach (InstrumentInfo instrument in group)
            {
                panels.Add(Panel(id++, "timeseries", instrument.Name, instrument.Description, PanelUnit(instrument), column * 12, y, 12, 8, Queries(instrument)));
                column ^= 1;
                if (column == 0)
                {
                    y += 8;
                }
            }

            if (column == 1)
            {
                y += 8;
            }
        }

        var dashboard = new JsonObject
        {
            ["__inputs"] = new JsonArray(new JsonObject
            {
                ["name"] = "DS_PROMETHEUS",
                ["label"] = "Prometheus",
                ["type"] = "datasource",
                ["pluginId"] = "prometheus",
                ["pluginName"] = "Prometheus",
            }),
            ["title"] = "SourceFlow",
            ["uid"] = "sourceflow",
            ["description"] = "Generated from SourceFlowTelemetry.Catalogue by `dotnet sourceflow observability export`; do not edit by hand.",
            ["tags"] = new JsonArray("sourceflow"),
            ["schemaVersion"] = 39,
            ["version"] = 1,
            ["editable"] = true,
            ["refresh"] = "30s",
            ["time"] = new JsonObject { ["from"] = "now-6h", ["to"] = "now" },
            ["templating"] = new JsonObject
            {
                ["list"] = new JsonArray(new JsonObject
                {
                    ["name"] = "job",
                    ["label"] = "Service",
                    ["type"] = "query",
                    ["datasource"] = Datasource(),
                    ["query"] = new JsonObject { ["query"] = $"label_values({Prom(SourceFlowTelemetry.Instruments.CommandDuration)}_count, job)", ["refId"] = "job" },
                    ["includeAll"] = true,
                    ["multi"] = true,
                    ["current"] = new JsonObject { ["text"] = "All", ["value"] = "$__all" },
                    ["refresh"] = 2,
                }),
            },
            ["panels"] = panels,
        };
        return dashboard.ToJsonString(Indented).ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>The Prometheus alert rules (YAML, LF line endings).</summary>
    public static string AlertRules()
    {
        var yaml = new StringBuilder();
        yaml.Append("# Generated from SourceFlowTelemetry.Catalogue by `dotnet sourceflow observability export`; do not edit by hand.\n");
        yaml.Append("# The service-level objectives of doc 08 §6. Metric names follow the OpenTelemetry Prometheus conversion.\n");
        yaml.Append("groups:\n");
        yaml.Append("  - name: sourceflow\n");
        yaml.Append("    rules:\n");
        foreach (AlertRule rule in Alerts)
        {
            yaml.Append(CultureInfo.InvariantCulture, $"      - alert: {rule.Name}\n");
            yaml.Append(CultureInfo.InvariantCulture, $"        expr: {Quote(rule.Expression)}\n");
            yaml.Append(CultureInfo.InvariantCulture, $"        for: {rule.For}\n");
            yaml.Append("        labels:\n");
            yaml.Append(CultureInfo.InvariantCulture, $"          severity: {rule.Severity}\n");
            yaml.Append("        annotations:\n");
            yaml.Append(CultureInfo.InvariantCulture, $"          summary: {Quote(rule.Summary)}\n");
            yaml.Append(CultureInfo.InvariantCulture, $"          description: {Quote(rule.Description)}\n");
        }

        return yaml.ToString();
    }

    /// <summary>Writes both files to <paramref name="directory"/>; returns their paths.</summary>
    public static async Task<IReadOnlyList<string>> WriteAsync(string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        string dashboard = Path.Combine(directory, DashboardFile);
        string alerts = Path.Combine(directory, AlertsFile);
        await File.WriteAllTextAsync(dashboard, Dashboard(), encoding, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(alerts, AlertRules(), encoding, cancellationToken).ConfigureAwait(false);
        return [dashboard, alerts];
    }

    private static List<(string Expr, string Legend)> Queries(InstrumentInfo instrument)
    {
        string name = Prom(instrument.Name);
        string by = instrument.Tags.Count == 0 ? string.Empty : Label(instrument.Tags[0]);
        string legend = instrument.Tags.Count == 0 ? instrument.Name : "{{" + by + "}}";
        string grouping = by.Length == 0 ? string.Empty : $" by ({by})";
        return instrument.Kind switch
        {
            InstrumentKind.Histogram when instrument.Unit == "s" =>
            [
                ($"histogram_quantile(0.99, sum by (le{(by.Length == 0 ? string.Empty : ", " + by)}) (rate({name}_bucket{{{Job}}}[{Rate}])))", "p99 " + legend),
                ($"histogram_quantile(0.5, sum by (le{(by.Length == 0 ? string.Empty : ", " + by)}) (rate({name}_bucket{{{Job}}}[{Rate}])))", "p50 " + legend),
            ],
            InstrumentKind.Histogram =>
            [
                ($"sum{grouping} (rate({name}_sum{{{Job}}}[{Rate}])) / sum{grouping} (rate({name}_count{{{Job}}}[{Rate}]))", "mean " + legend),
            ],
            InstrumentKind.Counter => [($"sum{grouping} (rate({name}{{{Job}}}[{Rate}]))", legend)],
            // Gauges sampled from the store report the same value on every instance: the max is the service's value.
            // Leases are per instance.
            _ when instrument.Name == SourceFlowTelemetry.Instruments.LeaseOwned => [($"sum by (instance) ({name}{{{Job}}})", "{{instance}}")],
            _ => [($"max{(instrument.Tags.Count == 0 ? string.Empty : $" by ({string.Join(", ", instrument.Tags.Select(Label))})")} ({name}{{{Job}}})", instrument.Tags.Count == 0 ? instrument.Name : string.Join(" ", instrument.Tags.Select(t => "{{" + Label(t) + "}}")))],
        };
    }

    private static string Group(InstrumentInfo instrument)
    {
        string[] parts = instrument.Name.Split('.');
        return parts[0] == "messaging" ? "Messaging (semantic conventions)" : parts[1] switch
        {
            "command" => "Commands",
            "eventstore" or "aggregate" or "snapshot" => "Event store and aggregates",
            "outbox" or "inbox" or "retry" => "Outbox, inbox and retries",
            "deadletter" => "Dead letters",
            "subscription" => "Subscriptions",
            _ => "Process managers and leases",
        };
    }

    private static string PanelUnit(InstrumentInfo instrument)
    {
        return instrument.Unit == "s" ? "s" : instrument.Kind == InstrumentKind.Counter ? "ops" : "short";
    }

    private static JsonObject Row(int id, string title, int y)
    {
        return new JsonObject
        {
            ["id"] = id,
            ["type"] = "row",
            ["title"] = title,
            ["collapsed"] = false,
            ["gridPos"] = new JsonObject { ["h"] = 1, ["w"] = 24, ["x"] = 0, ["y"] = y },
            ["panels"] = new JsonArray(),
        };
    }

    private static JsonObject Panel(int id, string type, string title, string description, string unit, int x, int y, int w, int h, List<(string Expr, string Legend)> queries)
    {
        var targets = new JsonArray();
        for (int i = 0; i < queries.Count; i++)
        {
            targets.Add(new JsonObject
            {
                ["datasource"] = Datasource(),
                ["expr"] = queries[i].Expr,
                ["legendFormat"] = queries[i].Legend,
                ["refId"] = ((char)('A' + i)).ToString(),
            });
        }

        return new JsonObject
        {
            ["id"] = id,
            ["type"] = type,
            ["title"] = title,
            ["description"] = description,
            ["datasource"] = Datasource(),
            ["gridPos"] = new JsonObject { ["h"] = h, ["w"] = w, ["x"] = x, ["y"] = y },
            ["fieldConfig"] = new JsonObject { ["defaults"] = new JsonObject { ["unit"] = unit }, ["overrides"] = new JsonArray() },
            ["targets"] = targets,
        };
    }

    private static JsonObject Datasource()
    {
        return new JsonObject { ["type"] = "prometheus", ["uid"] = "${DS_PROMETHEUS}" };
    }

    // A YAML double-quoted scalar.
    private static string Quote(string value)
    {
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}

/// <summary>A Prometheus alert rule generated for a service-level objective.</summary>
/// <param name="Name">The alert name.</param>
/// <param name="Expression">The PromQL expression.</param>
/// <param name="For">How long it must hold (Prometheus duration).</param>
/// <param name="Severity">The <c>severity</c> label (page or warning).</param>
/// <param name="Summary">The summary annotation (Prometheus template).</param>
/// <param name="Description">The description annotation.</param>
public sealed record AlertRule(string Name, string Expression, string For, string Severity, string Summary, string Description);

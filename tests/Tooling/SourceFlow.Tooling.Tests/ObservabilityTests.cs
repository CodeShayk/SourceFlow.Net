using System.Text;
using System.Text.Json.Nodes;
using SourceFlow.Diagnostics;
using SourceFlow.Testing;
using SourceFlow.Tool;

namespace SourceFlow.Tooling.Tests;

/// <summary>
/// The dashboard and alert generator (WP-4.3, doc 08 §3, §6), and the CI drift check: <c>deploy/observability</c> must be
/// what the generator writes today. Set <c>SOURCEFLOW_APPROVE=1</c> to rewrite the committed files.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ObservabilityTests
{
    private static readonly string Directory = Path.Combine(FindRoot(), "deploy", "observability");

    [Theory]
    [InlineData(Observability.DashboardFile)]
    [InlineData(Observability.AlertsFile)]
    [Trait("Id", "UT-HC-15")]
    [Trait("Covers", "doc 08 6")]
    public void TheCommittedFiles_AreWhatTheGeneratorWrites(string file)
    {
        string generated = file == Observability.DashboardFile ? Observability.Dashboard() : Observability.AlertRules();
        string path = Path.Combine(Directory, file);
        string? committed = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : null;
        if (committed != generated && string.Equals(Environment.GetEnvironmentVariable(Approvals.ApproveVariable), "1", StringComparison.Ordinal))
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(path, generated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return;
        }

        committed.ShouldBe(generated, $"{path} is out of date: run `dotnet sourceflow observability export --output deploy/observability` or the tests with {Approvals.ApproveVariable}=1.");
    }

    [Fact]
    [Trait("Id", "UT-HC-15")]
    public void TheDashboard_HasAPanelPerInstrument_AndTheSloRow()
    {
        JsonObject dashboard = JsonNode.Parse(Observability.Dashboard())!.AsObject();
        JsonObject[] panels = [.. dashboard["panels"]!.AsArray().Select(p => p!.AsObject())];

        foreach (InstrumentInfo instrument in SourceFlowTelemetry.Catalogue)
        {
            JsonObject panel = panels.Single(p => p["title"]!.GetValue<string>() == instrument.Name);
            panel["targets"]!.AsArray().ShouldAllBe(t => t!["expr"]!.GetValue<string>().Contains(Observability.Prom(instrument.Name), StringComparison.Ordinal));
        }

        panels.Where(p => p["type"]!.GetValue<string>() == "stat").Select(p => p["title"]!.GetValue<string>())
            .ShouldBe(["Write availability", "Write latency p99", "Read-model freshness", "Delivery durability", "Parked messages (1 h)"]);
        panels.Select(p => p["id"]!.GetValue<int>()).ShouldBeUnique();
        foreach (JsonObject panel in panels.Where(p => p["type"]!.GetValue<string>() != "row"))
        {
            JsonObject grid = panel["gridPos"]!.AsObject();
            (grid["x"]!.GetValue<int>() + grid["w"]!.GetValue<int>()).ShouldBeLessThanOrEqualTo(24, panel["title"]!.GetValue<string>());
        }
    }

    [Fact]
    [Trait("Id", "UT-HC-15")]
    public void PrometheusNames_FollowTheOpenTelemetryConversion()
    {
        Observability.Prom(SourceFlowTelemetry.Instruments.CommandDuration).ShouldBe("sourceflow_command_duration_seconds");
        Observability.Prom(SourceFlowTelemetry.Instruments.DeadLetterParked).ShouldBe("sourceflow_deadletter_parked_total");
        Observability.Prom(SourceFlowTelemetry.Instruments.OutboxAge).ShouldBe("sourceflow_outbox_age_seconds");
        Observability.Prom(SourceFlowTelemetry.Instruments.SubscriptionLag).ShouldBe("sourceflow_subscription_lag");
        Observability.Prom(SourceFlowTelemetry.Instruments.SentMessages).ShouldBe("messaging_client_sent_messages_total");
        Observability.Label(SourceFlowTelemetry.Tags.Outcome).ShouldBe("sourceflow_outcome");
        Should.Throw<ArgumentException>(() => Observability.Prom("sourceflow.not.an.instrument"));
    }

    [Fact]
    [Trait("Id", "UT-HC-15")]
    [Trait("Covers", "doc 08 6")]
    public void TheAlerts_CoverEverySlo_AndUseOnlyCatalogueMetrics()
    {
        string[] metrics = [.. SourceFlowTelemetry.Catalogue.Select(i => Observability.Prom(i.Name))];
        HashSet<string> labels = [.. SourceFlowTelemetry.Catalogue.SelectMany(i => i.Tags).Select(Observability.Label)];
        string rules = Observability.AlertRules();

        Observability.Alerts.Select(a => a.Name).ShouldBe(
            ["SourceFlowDeadLetterParked", "SourceFlowSubscriptionStalled", "SourceFlowOutboxDelayed", "SourceFlowReadModelsStale", "SourceFlowWriteLatency", "SourceFlowWriteErrors", "SourceFlowHorizonHeld"]);
        foreach (AlertRule alert in Observability.Alerts)
        {
            System.Text.RegularExpressions.Regex.Matches(alert.Expression, "(sourceflow|messaging)_[a-z_]+")
                .Select(m => m.Value.Replace("_bucket", string.Empty, StringComparison.Ordinal).Replace("_count", string.Empty, StringComparison.Ordinal))
                .Where(m => !labels.Contains(m))
                .ShouldAllBe(m => metrics.Contains(m), alert.Name);
            alert.Severity.ShouldBeOneOf("page", "warning");
        }

        rules.ShouldStartWith("# Generated");
        rules.ShouldContain("groups:\n  - name: sourceflow\n    rules:\n      - alert: SourceFlowDeadLetterParked\n");
        rules.ShouldNotContain("\r");
    }

    [Fact]
    [Trait("Id", "UT-HC-15")]
    public async Task TheCli_WritesBothFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sourceflow-observability-" + Guid.CreateVersion7().ToString("N")[^12..]);
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            int code = await SourceFlowCli.RunAsync(["observability", "export", "--output", directory], output: output, error: error, cancellationToken: TestContext.Current.CancellationToken);

            code.ShouldBe(0, error.ToString());
            (await File.ReadAllTextAsync(Path.Combine(directory, Observability.DashboardFile), TestContext.Current.CancellationToken)).ShouldBe(Observability.Dashboard());
            (await File.ReadAllTextAsync(Path.Combine(directory, Observability.AlertsFile), TestContext.Current.CancellationToken)).ShouldBe(Observability.AlertRules());
        }
        finally
        {
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SourceFlow.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found.");
    }
}

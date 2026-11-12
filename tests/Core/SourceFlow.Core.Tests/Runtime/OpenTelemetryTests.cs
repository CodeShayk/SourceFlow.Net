using System.Diagnostics;
using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SourceFlow.Diagnostics;
using SourceFlow.Messaging;
using SourceFlow.Pipeline;
using SourceFlow.Testing;

namespace SourceFlow.Core.Tests.Runtime;

/// <summary>
/// UT-OTEL-01..08 (plan 05, WP-4.6; ADR-019, ADR-027 §5): the OpenTelemetry SDK, referenced by the test only, subscribes
/// with <c>AddSource</c>/<c>AddMeter</c> and nothing else: no SourceFlow package for OpenTelemetry, and no SDK views.
/// Spans are matched by the trace of a root activity each test starts, so parallel tests do not interfere.
/// </summary>
[Collection(OpenTelemetryCollection.Name)]
[Trait("Category", "Unit")]
public sealed class OpenTelemetryTests
{
    private static readonly ActivitySource TestSource = new("SourceFlow.Tests.OpenTelemetry");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-OTEL-01")]
    [Trait("Covers", "ADR-027 5;doc 08 1")]
    public void SourceAndMeter_HaveTheDocumentedNames_VersionAndSchema()
    {
        using var metrics = new SourceFlowMetrics();

        SourceFlowTelemetry.ActivitySourceName.ShouldBe("SourceFlow");
        SourceFlowTelemetry.MeterName.ShouldBe("SourceFlow");
        SourceFlowTelemetry.ActivitySource.Name.ShouldBe(SourceFlowTelemetry.ActivitySourceName);
        SourceFlowTelemetry.ActivitySource.Version.ShouldBe(SourceFlowTelemetry.Version);
        SourceFlowTelemetry.ActivitySource.TelemetrySchemaUrl.ShouldBe(SemanticConventions.SchemaUrl);
        metrics.Meter.Name.ShouldBe(SourceFlowTelemetry.MeterName);
        metrics.Meter.Version.ShouldBe(SourceFlowTelemetry.Version);
        metrics.Meter.TelemetrySchemaUrl.ShouldBe(SemanticConventions.SchemaUrl);
        SemanticConventions.SchemaUrl.ShouldBe("https://opentelemetry.io/schemas/" + SemanticConventions.Version);
    }

    [Fact]
    [Trait("Id", "UT-OTEL-02")]
    [Trait("Covers", "doc 08 2.1")]
    public async Task Sdk_ExportsTheCommandSpans_InOneTrace()
    {
        var exported = new List<Activity>();
        using TracerProvider tracer = Tracer(exported);
        await using SourceFlowTestHost host = await BankAsync();

        ActivityTraceId trace;
        using (Activity root = TestSource.StartActivity("request")!)
        {
            trace = root.TraceId;
            (await host.SendAsync(new OpenAccount(AccountId.New(), "Ada", 10m), cancellationToken: Ct)).Status.ShouldBe(CommandStatus.Accepted);
        }

        await host.WaitForIdleAsync();
        tracer.ForceFlush();
        Activity[] spans = InTrace(exported, trace);

        Activity handle = spans.Single(s => s.DisplayName == "handle OpenAccount");
        handle.Kind.ShouldBe(ActivityKind.Internal);
        handle.GetTagItem(SourceFlowTelemetry.Tags.Outcome).ShouldBe("accepted");
        Activity append = spans.Single(s => s.DisplayName == "sourceflow.eventstore.append");
        append.Kind.ShouldBe(ActivityKind.Client);
        append.ParentSpanId.ShouldBe(handle.SpanId);
        append.GetTagItem(SourceFlowTelemetry.Tags.DbOperationName).ShouldBe("append");
        append.GetTagItem(SourceFlowTelemetry.Tags.EventsCount).ShouldBe(1);
        spans.ShouldContain(s => s.DisplayName == "sourceflow.eventstore.load" && s.Kind == ActivityKind.Client);
        spans.ShouldContain(s => s.DisplayName == "create bank.account.opened" && s.Kind == ActivityKind.Producer
            && (string?)s.GetTagItem(SourceFlowTelemetry.Tags.OperationType) == "create");
    }

    [Fact]
    [Trait("Id", "UT-OTEL-03")]
    [Trait("Covers", "doc 08 3")]
    public async Task Sdk_UsesTheDurationBucketsFromInstrumentAdvice_WithoutViews()
    {
        var exported = new List<MetricSnapshot>();
        using MeterProvider meters = Meters(exported);
        await using SourceFlowTestHost host = await BankAsync();
        await host.SendAsync(new OpenAccount(AccountId.New(), "Ada", 10m), cancellationToken: Ct);

        meters.ForceFlush();

        Bounds(exported, "sourceflow.command.duration").ShouldBe(SourceFlowTelemetry.DurationBuckets);
        Bounds(exported, "sourceflow.eventstore.append.duration").ShouldBe(SourceFlowTelemetry.DurationBuckets);
    }

    [Fact]
    [Trait("Id", "UT-OTEL-04")]
    [Trait("Covers", "doc 08 3")]
    public async Task Sdk_UsesTheCountBucketsFromInstrumentAdvice()
    {
        var exported = new List<MetricSnapshot>();
        using MeterProvider meters = Meters(exported);
        await using SourceFlowTestHost host = await BankAsync();
        await host.SendAsync(new OpenAccount(AccountId.New(), "Ada", 10m), cancellationToken: Ct);

        meters.ForceFlush();

        Bounds(exported, "sourceflow.eventstore.append.events").ShouldBe(SourceFlowTelemetry.CountBuckets.Select(b => (double)b));
        Bounds(exported, "sourceflow.aggregate.load.events").ShouldBe(SourceFlowTelemetry.CountBuckets.Select(b => (double)b));
    }

    [Fact]
    [Trait("Id", "UT-OTEL-05")]
    [Trait("Covers", "doc 08 2.1;doc 08 3")]
    public async Task Sdk_ExportedInstruments_MatchTheCatalogue_AndCarryNoIdTags()
    {
        var exported = new List<MetricSnapshot>();
        using MeterProvider meters = Meters(exported);
        await using SourceFlowTestHost host = await BankAsync();
        var id = AccountId.New();
        await host.SendAsync(new OpenAccount(id, "Ada", 10m), cancellationToken: Ct);
        await host.SendAsync(new Deposit(id, 5m), cancellationToken: Ct);
        await host.WaitForIdleAsync();

        meters.ForceFlush();
        Dictionary<string, InstrumentInfo> catalogue = SourceFlowTelemetry.Catalogue.ToDictionary(i => i.Name);
        MetricSnapshot[] ours = [.. exported.Where(m => m.MeterName == SourceFlowTelemetry.MeterName)];

        ours.ShouldNotBeEmpty();
        foreach (MetricSnapshot metric in ours)
        {
            catalogue.ShouldContainKey(metric.Name, "every exported instrument is in the catalogue");
            metric.Unit.ShouldBe(catalogue[metric.Name].Unit, metric.Name);
            foreach (MetricPoint point in metric.MetricPoints)
            {
                foreach (KeyValuePair<string, object?> tag in point.Tags)
                {
                    tag.Key.ShouldNotBeOneOf(SourceFlowTelemetry.Tags.StreamId, SourceFlowTelemetry.Tags.MessageId, SourceFlowTelemetry.Tags.ProcessId);
                }
            }
        }
    }

    [Theory]
    [Trait("Id", "UT-OTEL-06")]
    [Trait("Covers", "ADR-019")]
    [InlineData(null, SemanticConventionsMode.Pinned)]
    [InlineData("", SemanticConventionsMode.Pinned)]
    [InlineData("http", SemanticConventionsMode.Pinned)]
    [InlineData("database/dup,http", SemanticConventionsMode.Pinned)]
    [InlineData("messaging", SemanticConventionsMode.Latest)]
    [InlineData(" http , Messaging ", SemanticConventionsMode.Latest)]
    [InlineData("messaging/dup", SemanticConventionsMode.Duplicate)]
    [InlineData("messaging,messaging/dup", SemanticConventionsMode.Duplicate)]
    public void OptInVariable_SelectsTheConventions(string? value, SemanticConventionsMode expected)
    {
        SemanticConventions.Parse(value).ShouldBe(expected);
        SemanticConventions.OptInVariable.ShouldBe("OTEL_SEMCONV_STABILITY_OPT_IN");
        SemanticConventions.PinnedVersion.ShouldBe("1.27.0");
    }

    [Fact]
    [Trait("Id", "UT-OTEL-07")]
    [Trait("Covers", "ADR-019;doc 08 2.1")]
    public async Task DuplicateMode_AlsoEmitsTheDeprecatedOperationAttribute()
    {
        var exported = new List<Activity>();
        using TracerProvider tracer = Tracer(exported);
        await using SourceFlowTestHost host = await BankAsync(routed: true);
        SemanticConventionsMode before = SemanticConventions.Mode;

        ActivityTraceId pinned;
        ActivityTraceId duplicate;
        try
        {
            SemanticConventions.Mode = SemanticConventionsMode.Pinned;
            pinned = await OpenInTraceAsync(host);
            SemanticConventions.Mode = SemanticConventionsMode.Duplicate;
            duplicate = await OpenInTraceAsync(host);
        }
        finally
        {
            SemanticConventions.Mode = before;
        }

        tracer.ForceFlush();
        Activity pinnedSend = InTrace(exported, pinned).Single(s => s.DisplayName == "send accounts.events");
        Activity duplicateSend = InTrace(exported, duplicate).Single(s => s.DisplayName == "send accounts.events");
        Activity duplicateCreate = InTrace(exported, duplicate).First(s => s.DisplayName.StartsWith("create ", StringComparison.Ordinal));

        pinnedSend.GetTagItem(SourceFlowTelemetry.Tags.OperationType).ShouldBe("send");
        pinnedSend.GetTagItem(SemanticConventions.LegacyOperation).ShouldBeNull();
        duplicateSend.GetTagItem(SourceFlowTelemetry.Tags.OperationType).ShouldBe("send");
        duplicateSend.GetTagItem(SemanticConventions.LegacyOperation).ShouldBe("publish");
        duplicateCreate.GetTagItem(SemanticConventions.LegacyOperation).ShouldBe("create");
        duplicateSend.Kind.ShouldBe(ActivityKind.Producer);
    }

    [Theory]
    [Trait("Id", "UT-OTEL-08")]
    [Trait("Covers", "doc 08 2.1")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessSpan_IsLinkedToTheCreateSpan_OrItsChildWhenParentBasedOnMessage(bool parentBased)
    {
        var exported = new List<Activity>();
        using TracerProvider tracer = Tracer(exported);
        using var metrics = new SourceFlowMetrics();
        string consumer = "otel-" + (parentBased ? "child" : "link") + "-" + Guid.CreateVersion7().ToString("N")[..8];
        var producer = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        var envelope = new MessageEnvelope
        {
            MessageId = MessageIds.New(TimeProvider.System),
            Type = "t",
            TypeVersion = 1,
            Source = "s",
            Time = TimeProvider.System.GetUtcNow(),
            Body = "{}"u8.ToArray(),
            TraceParent = $"00-{producer.TraceId}-{producer.SpanId}-01",
        };
        await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        var context = new MessageContext(PipelineStage.Receive, envelope, services, Ct);

        await new TelemetryMiddleware(metrics, consumer, parentBased).InvokeAsync(context, _ => ValueTask.CompletedTask);
        tracer.ForceFlush();

        Activity process;
        lock (exported)
        {
            process = exported.Single(a => a.DisplayName == "process " + consumer);
        }

        process.Kind.ShouldBe(ActivityKind.Consumer);
        process.GetTagItem(SourceFlowTelemetry.Tags.OperationType).ShouldBe("process");
        process.Links.ShouldHaveSingleItem().Context.SpanId.ShouldBe(producer.SpanId);
        if (parentBased)
        {
            process.TraceId.ShouldBe(producer.TraceId);
            process.ParentSpanId.ShouldBe(producer.SpanId);
        }
        else
        {
            process.TraceId.ShouldNotBe(producer.TraceId);
        }
    }

    // ================================================================ helpers

    private static TracerProvider Tracer(List<Activity> exported)
    {
        return Sdk.CreateTracerProviderBuilder()
            .AddSource(SourceFlowTelemetry.ActivitySourceName, TestSource.Name)
            .SetSampler(new AlwaysOnSampler())
            .AddInMemoryExporter(exported)
            .Build()!;
    }

    private static MeterProvider Meters(List<MetricSnapshot> exported)
    {
        return Sdk.CreateMeterProviderBuilder()
            .AddMeter(SourceFlowTelemetry.MeterName)
            .AddInMemoryExporter(exported)
            .Build()!;
    }

    private static Task<SourceFlowTestHost> BankAsync(bool routed = false)
    {
        return SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            if (routed)
            {
                sf.Routing(r => r.Publish<AccountOpened>().AsIs().To("accounts.events"));
            }
        });
    }

    private static async Task<ActivityTraceId> OpenInTraceAsync(SourceFlowTestHost host)
    {
        ActivityTraceId trace;
        using (Activity root = TestSource.StartActivity("request")!)
        {
            trace = root.TraceId;
            await host.SendAsync(new OpenAccount(AccountId.New(), "Ada", 10m), cancellationToken: Ct);
        }

        await host.WaitForIdleAsync();   // the outbox relay sends, as a child of the create span
        return trace;
    }

    private static Activity[] InTrace(List<Activity> exported, ActivityTraceId trace)
    {
        lock (exported)
        {
            return [.. exported.Where(a => a.TraceId == trace)];
        }
    }

    // The explicit bucket boundaries the SDK uses for a histogram, without the +Inf bucket.
    private static double[] Bounds(List<MetricSnapshot> exported, string name)
    {
        MetricSnapshot metric = exported.Last(m => m.MeterName == SourceFlowTelemetry.MeterName && m.Name == name);
        MetricPoint point = metric.MetricPoints.First();
        var bounds = new List<double>();
        foreach (HistogramBucket bucket in point.GetHistogramBuckets())
        {
            if (!double.IsPositiveInfinity(bucket.ExplicitBound))
            {
                bounds.Add(bucket.ExplicitBound);
            }
        }

        return [.. bounds];
    }
}

/// <summary>UT-OTEL tests change the process-wide semantic conventions mode: they run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OpenTelemetryCollection
{
    public const string Name = "OpenTelemetry";
}

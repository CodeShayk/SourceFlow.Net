using SourceFlow.Studio.Dsl;
using SourceFlow.Studio.Dsl.Manifests;
using SourceFlow.Studio.Dsl.Model;
using SourceFlow.Studio.Dsl.Simulation;
using SourceFlow.Studio.Dsl.Specs;
using SourceFlow.Studio.Dsl.Validation;
using SourceFlow.Studio.Workspace;

namespace SourceFlow.Studio.Tests;

/// <summary>
/// The TravelBooking sample (samples/TravelBooking) as a workspace: its exported manifests are exact, its trip-booking
/// workflow validates against both services with no diagnostic and has the expected path set, and the specs Studio
/// generated for it are current.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TravelBookingSampleTests
{
    private static readonly string Sample = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "samples", "TravelBooking"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-MAN-17")]
    [Trait("Covers", "WFD-11;WFD-12")]
    public async Task Manifests_HaveExactEmitsAndRejects_ForBothServices()
    {
        DomainWorkspace workspace = await DomainWorkspace.LoadAsync(Sample, cancellationToken: Ct);

        workspace.Services.Select(s => s.Name).ShouldBe(["bookings", "hotels"]);
        Commands(workspace, "hotels").ShouldBe(
        [
            "hotels.confirm-reservation: emits hotels.reservation-confirmed.v1; rejects hotels.hold_lost",
            "hotels.register-hotel: emits hotels.hotel-registered.v1; rejects hotels.invalid_rooms",
            "hotels.release-room: emits hotels.room-released.v1; rejects none",
            "hotels.reserve-room: emits hotels.room-reserved.v1; rejects hotels.sold_out, hotels.unknown_hotel",
        ]);
        Commands(workspace, "bookings").ShouldContain("bookings.request-booking: emits bookings.booking-requested.v1; rejects bookings.guest_required, bookings.invalid_stay");

        ServiceManifest bookings = workspace.Services.Single(s => s.Name == "bookings").Manifest;
        bookings.CommandRoutes.Where(r => r.ReplyOnReject).Select(r => r.Command).ShouldBe(["hotels.confirm-reservation.v1", "hotels.reserve-room.v1"]);
        bookings.Workflows.ShouldHaveSingleItem().Name.ShouldBe("trip-booking");
        ServiceManifest hotels = workspace.Services.Single(s => s.Name == "hotels").Manifest;
        hotels.Publishes.Select(p => p.Topic).Distinct().ShouldBe(["hotels.events"]);
    }

    [Fact]
    [Trait("Id", "UT-WF-74")]
    [Trait("Covers", "WFD-28")]
    public async Task Workflow_ValidatesAgainstBothServices_WithNoDiagnostic()
    {
        (DomainWorkspace workspace, WorkflowAnalysisResult analysis) = await AnalyzeAsync();

        workspace.Services.Single(s => s.Name == "bookings").WorkflowFiles.ShouldHaveSingleItem().ShouldEndWith("trip-booking.sfflow.yaml");
        analysis.Diagnostics.ShouldBeEmpty();
        analysis.Workflow.ShouldNotBeNull().ProcessName.ShouldBe("trip-booking@1");
    }

    [Fact]
    [Trait("Id", "UT-WF-75")]
    [Trait("Covers", "WFD-33")]
    public async Task Workflow_ReachesEveryEnd()
    {
        (_, WorkflowAnalysisResult analysis) = await AnalyzeAsync();

        PathSet paths = PathEnumerator.Enumerate(analysis.Workflow!);
        paths.Paths.Count.ShouldBe(ExpectedPaths);
        paths.MinimalCover().Count.ShouldBe(ExpectedCover);
        paths.Paths.Select(p => p.End).Distinct().Order().ShouldBe([WorkflowStatus.Completed, WorkflowStatus.Compensated, WorkflowStatus.ManualIntervention]);
    }

    [Fact]
    [Trait("Id", "UT-WF-76")]
    [Trait("Covers", "WFD-34")]
    public async Task GeneratedSpecs_AreCurrent()
    {
        (DomainWorkspace workspace, WorkflowAnalysisResult analysis) = await AnalyzeAsync();
        WorkspaceService bookings = workspace.Services.Single(s => s.Name == "bookings");

        // The options the local host uses for the service's tests and module (sourceflow.domain.yaml).
        string expected = SpecGenerator.Generate(analysis.Workflow!, new WorkflowEnvironment(workspace.Manifests), new SpecOptions
        {
            Namespace = "TravelBooking.Tests.Workflows",
            ModuleType = bookings.ModuleType!,
            WorkflowType = "Bookings.TripBookingWorkflowV1",
        });
        string file = Path.Combine(Sample, bookings.TestsLocation!, "Workflows", "TripBookingV1PathTests.cs");
        if (string.Equals(Environment.GetEnvironmentVariable(SourceFlow.Testing.Approvals.ApproveVariable), "1", StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, expected, Ct);
        }

        File.ReadAllText(file).ReplaceLineEndings("\n").ShouldBe(expected, "regenerate the specs in Studio (Paths → Generate specs) after changing the workflow");
    }

    // The trip-booking workflow's paths (retry loops at 0, 1 and the maximum) and the minimal cover Studio generates specs for.
    private const int ExpectedPaths = 27;
    private const int ExpectedCover = 6;

    private static async Task<(DomainWorkspace Workspace, WorkflowAnalysisResult Analysis)> AnalyzeAsync()
    {
        DomainWorkspace workspace = await DomainWorkspace.LoadAsync(Sample, cancellationToken: Ct);
        string file = Path.Combine(workspace.Root, "Bookings", "Workflows", "trip-booking.sfflow.yaml");
        return (workspace, WorkflowAnalysis.Analyze(await File.ReadAllTextAsync(file, Ct), new WorkflowEnvironment(workspace.Manifests)));
    }

    private static string[] Commands(DomainWorkspace workspace, string service)
    {
        return [.. workspace.Services.Single(s => s.Name == service).Manifest.Aggregates
            .SelectMany(a => a.Commands)
            .Select(c => $"{c.Type}: emits {Join(c.Emits)}; rejects {Join(c.Rejects)}")
            .Order(StringComparer.Ordinal)];

        static string Join(IReadOnlyList<string> values) => values.Count == 0 ? "none" : string.Join(", ", values.Order(StringComparer.Ordinal));
    }
}

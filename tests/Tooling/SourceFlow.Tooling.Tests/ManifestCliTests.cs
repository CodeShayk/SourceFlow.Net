using System.Text.Json.Nodes;
using Bank.Accounts;
using SourceFlow.Testing;
using SourceFlow.Tool;

namespace SourceFlow.Tooling.Tests;

/// <summary><c>dotnet sourceflow manifest export</c> and the manifest drift check (doc 15 §3.2, WP-S.2).</summary>
public sealed class ManifestCliTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Id", "UT-MAN-15")]
    [Trait("Covers", "WFD-11")]
    public async Task Export_InsideAService_WritesTheManifest_AndCheckDetectsDrift()
    {
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<Bank.Contracts.ContractsModule>();
            sf.AddModule<AccountsModule>();
            sf.Routing(r => r.Publish<AccountOpened>().As(AccountContracts.ToV1).To("accounts.events"));
        }, cancellationToken: Ct);
        string folder = Path.Combine(Path.GetTempPath(), "sourceflow-manifest-" + Path.GetRandomFileName());
        string file = Path.Combine(folder, "artifacts", "sourceflow.manifest.json");
        try
        {
            (int code, string output, _) = await RunAsync(host, "manifest", "export", "--output", file);
            code.ShouldBe(0);
            output.ShouldContain("Wrote " + file);
            JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!.AsObject();
            manifest["service"]!.GetValue<string>().ShouldBe("accounts");
            manifest["publishes"]![0]!["as"]!.GetValue<string>().ShouldBe("accounts.account-opened.v1");

            (code, output, _) = await RunAsync(host, "manifest", "export", "--check", "--output", file);
            code.ShouldBe(0);
            output.ShouldContain("up to date");

            // Windows line endings in a checked-out file are not drift.
            await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file, Ct)).Replace("\n", "\r\n", StringComparison.Ordinal), Ct);
            (code, _, _) = await RunAsync(host, "manifest", "export", "--check", "--output", file);
            code.ShouldBe(0);

            await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file, Ct)).Replace("\"accounts.events\"", "\"old.events\"", StringComparison.Ordinal), Ct);
            (code, _, string error) = await RunAsync(host, "manifest", "export", "--check", "--output", file);
            code.ShouldBe(1);
            error.ShouldContain("Manifest drift");
            error.ShouldContain("committed:   \"topic\": \"old.events\"");
            error.ShouldContain("regenerated: \"topic\": \"accounts.events\"");

            (code, _, error) = await RunAsync(host, "manifest", "export", "--check", "--output", Path.Combine(folder, "none.json"));
            code.ShouldBe(1);
            error.ShouldContain("doesn't exist");

            (code, output, _) = await RunAsync(host, "manifest", "export");
            code.ShouldBe(0);
            output.ShouldStartWith("{\n  \"manifestVersion\": 1,");

            (code, _, error) = await RunAsync(host, "manifest", "export", "--check");
            code.ShouldBe(1);
            error.ShouldContain("--check needs --output");
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Id", "UT-MAN-15")]
    public async Task Export_FromTheGlobalTool_NeedsAProject()
    {
        (int code, _, string error) = await RunAsync(null, "manifest", "export");
        code.ShouldBe(1);
        error.ShouldContain("needs the service's model");

        (code, _, error) = await RunAsync(null, "manifest", "export", "--project", Path.Combine(Path.GetTempPath(), "no-such-project-" + Path.GetRandomFileName()));
        code.ShouldBe(1);
        error.ShouldContain("is not a project file");
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Id", "UT-MAN-15")]
    [Trait("Covers", "WFD-11;WFD-NFR-3")]
    public async Task Export_DesignTimeRunOfASampleHost_MatchesTheCommittedManifest_WithoutADatabase()
    {
        string root = RepositoryRoot();
        string project = Path.Combine(root, "samples", "TravelBooking", "Bookings.Api", "Bookings.Api.csproj");
        string configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Release" : "Debug";
        bool built = File.Exists(Path.Combine(root, "samples", "TravelBooking", "Bookings.Api", "bin", configuration, "net10.0", "Bookings.Api.dll"));

        // Port 1 refuses connections: a design-time run that touched the database would fail.
        (int code, string output, string error) = await RunAsync(
            null,
            ["manifest", "export", "--project", project, "-c", configuration, .. built ? new[] { "--no-build" } : [], "--check",
             "--output", Path.Combine(root, "samples", "TravelBooking", "Bookings", "sourceflow.manifest.json"),
             "--", "--ConnectionStrings:SourceFlow=Host=localhost;Port=1;Timeout=1"]);

        code.ShouldBe(0, error);
        output.ShouldContain("up to date");
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(SourceFlowTestHost? host, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int code = await SourceFlowCli.RunAsync(args, host?.Services, output, error, Ct);
        return (code, output.ToString(), error.ToString());
    }

    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "SourceFlow.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new InvalidOperationException("Repository root not found.");
    }
}

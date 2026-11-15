using System.Reflection;
using System.Text.RegularExpressions;

namespace SourceFlow.Architecture.Tests;

/// <summary>
/// ARCH-06: a package marked AOT-compatible is published with PublishAot (warnings as errors) by an AotCheck fixture that
/// CI runs. The marking is a claim to users, so it is backed by a publish, not by the analyzers alone (CR-58).
/// </summary>
public sealed partial class AotTests
{
    // Marked, but no AotCheck fixture publishes them yet: the list may only shrink (plan 09 IR-08).
    private static readonly string[] NotYetChecked = ["SourceFlow.Storage.SqlServer", "SourceFlow.Testing", "SourceFlow.Studio"];

    // Not marked: the EF Core adapter, the CLI and the generator (plan 02 §6); the GCP adapter, whose SDK warns when trimmed.
    private static readonly string[] Unmarked = ["SourceFlow.Storage.EntityFrameworkCore", "SourceFlow.Cloud.Gcp"];

    [Fact]
    [Trait("Id", "ARCH-06")]
    [Trait("Covers", "ARCH-06;plan 09 IR-08")]
    public void Every_AOT_marked_package_is_published_by_an_AOT_check_that_CI_runs()
    {
        string root = RepositoryRoot();
        string workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "v3-pr.yml"));
        string[] fixtures = Directory.GetFiles(Path.Combine(root, "tests", "Fixtures"), "*.AotCheck.csproj", SearchOption.AllDirectories);
        fixtures.Length.ShouldBeGreaterThanOrEqualTo(2);

        var checkedPackages = new SortedSet<string>(StringComparer.Ordinal) { Packages.Core };
        foreach (string fixture in fixtures)
        {
            string relative = Path.GetRelativePath(root, fixture).Replace('\\', '/');
            workflow.ShouldContain($"dotnet publish {relative} -c Release -p:SourceFlowAotCheck=true -warnaserror", customMessage: $"CI must publish {relative}");
            File.ReadAllText(fixture).ShouldContain("<PublishAot Condition=\"'$(SourceFlowAotCheck)' == 'true'\">true</PublishAot>");
            foreach (Match reference in SourceFlowReference().Matches(File.ReadAllText(fixture)))
            {
                checkedPackages.Add(reference.Groups["name"].Value);
            }
        }

        string[] marked = [.. Packages.Runtime.Where(IsAotCompatible).Order(StringComparer.Ordinal)];
        checkedPackages.Where(p => !marked.Contains(p)).ShouldBeEmpty("a package in an AOT check must be marked AOT-compatible");
        marked.Except(checkedPackages).Order(StringComparer.Ordinal).ShouldBe(NotYetChecked.Order(StringComparer.Ordinal), "a newly marked package needs an AOT check");
        Packages.Runtime.Where(p => !IsAotCompatible(p)).Order(StringComparer.Ordinal).ShouldBe(Unmarked.Order(StringComparer.Ordinal));
    }

    private static bool IsAotCompatible(string package)
    {
        return Packages.Load(package).GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(a => a.Key == "IsAotCompatible" && string.Equals(a.Value, "true", StringComparison.OrdinalIgnoreCase));
    }

    private static string RepositoryRoot()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "SourceFlow.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root not found.");
        }

        return root;
    }

    [GeneratedRegex(@"<ProjectReference Include=""[^""]*\\src\\[^""\\]+\\(?<name>SourceFlow[^""\\]*)\\[^""]+\.csproj""")]
    private static partial Regex SourceFlowReference();
}

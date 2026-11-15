using System.Reflection;

namespace SourceFlow.Architecture.Tests;

/// <summary>The v3 package set (ADR-027) loaded from the test's own output folder.</summary>
internal static class Packages
{
    public const string Core = "SourceFlow.Net";

    /// <summary>Adapter packages: they implement the core SPIs (ARCH-03, ARCH-07).</summary>
    public static readonly string[] Adapters =
    [
        "SourceFlow.Storage.PostgreSQL",
        "SourceFlow.Storage.SqlServer",
        "SourceFlow.Storage.EntityFrameworkCore",
        "SourceFlow.Cloud.Aws",
        "SourceFlow.Cloud.Azure",
        "SourceFlow.Cloud.Gcp",
        "SourceFlow.AspNetCore",
    ];

    /// <summary>Every runtime package that ships to applications (ARCH-05).</summary>
    public static readonly string[] Runtime = [Core, .. Adapters, "SourceFlow.Testing", "SourceFlow.Studio"];

    public static Assembly Load(string name)
    {
        return Assembly.Load(new AssemblyName(name));
    }

    public static IEnumerable<string> ReferencedSourceFlowAssemblies(Assembly assembly)
    {
        return assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("SourceFlow", StringComparison.Ordinal));
    }

    /// <summary>Every package in a project's restore graph (its <c>obj/project.assets.json</c>), by name.</summary>
    public static IReadOnlyList<string> Graph(string project)
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "SourceFlow.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root not found.");
        }

        string assets = Path.Combine(root, project, "obj", "project.assets.json");
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(assets));
        System.Text.Json.JsonElement targets = document.RootElement.GetProperty("targets");
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (System.Text.Json.JsonProperty target in targets.EnumerateObject())
        {
            foreach (System.Text.Json.JsonProperty library in target.Value.EnumerateObject())
            {
                if (library.Value.TryGetProperty("type", out System.Text.Json.JsonElement type) && type.GetString() == "package")
                {
                    names.Add(library.Name[..library.Name.IndexOf('/', StringComparison.Ordinal)]);
                }
            }
        }

        return [.. names];
    }

    public static TheoryData<string> AdapterData()
    {
        return new TheoryData<string>(Adapters);
    }

    public static TheoryData<string> RuntimeData()
    {
        return new TheoryData<string>(Runtime);
    }
}

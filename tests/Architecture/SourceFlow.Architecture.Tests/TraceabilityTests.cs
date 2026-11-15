using System.Reflection;
using System.Runtime.InteropServices;

namespace SourceFlow.Architecture.Tests;

/// <summary>
/// ARCH-08: every id listed in <c>tests/traceability.csv</c> is covered by at least one test carrying
/// <c>[Trait("Covers", "…")]</c> in some v3 test assembly. Test assemblies are inspected with a
/// <see cref="MetadataLoadContext"/>, so they are never executed or loaded for real.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class TraceabilityTests
{
    [Fact]
    [Trait("Id", "ARCH-08")]
    [Trait("Covers", "ARCH-08")]
    public void Every_traceability_id_is_covered_by_a_test()
    {
        DirectoryInfo root = RepositoryRoot();
        string[] required = ReadRequiredIds(Path.Combine(root.FullName, "tests", "traceability.csv"));
        HashSet<string> covered = CollectCoveredIds(root);

        string[] missing = required.Where(id => !covered.Contains(id)).ToArray();

        missing.ShouldBeEmpty("These traceability ids have no [Trait(\"Covers\", …)] test (ARCH-08).");
    }

    private static string[] ReadRequiredIds(string csvPath)
    {
        File.Exists(csvPath).ShouldBeTrue($"Missing {csvPath}.");
        return File.ReadAllLines(csvPath)
            .Skip(1)
            .Select(line => line.Split(',')[0].Trim())
            .Where(id => id.Length > 0 && !id.StartsWith('#'))
            .ToArray();
    }

    private static HashSet<string> CollectCoveredIds(DirectoryInfo root)
    {
        string own = typeof(TraceabilityTests).Assembly.Location;
        string configuration = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(own))!);
        string framework = Path.GetFileName(Path.GetDirectoryName(own))!;

        string[] testAssemblies = Directory.GetDirectories(Path.Combine(root.FullName, "tests"), "SourceFlow.*.Tests", SearchOption.AllDirectories)
            .Select(dir => Path.Combine(dir, "bin", configuration, framework, Path.GetFileName(dir) + ".dll"))
            .Where(File.Exists)
            .ToArray();

        testAssemblies.ShouldNotBeEmpty("No built v3 test assemblies were found; build SourceFlow.slnx first.");

        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in testAssemblies)
        {
            string directory = Path.GetDirectoryName(path)!;
            var resolver = new PathAssemblyResolver(
                Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll")
                    .Concat(Directory.GetFiles(directory, "*.dll")));
            using var context = new MetadataLoadContext(resolver);
            Assembly assembly = context.LoadFromAssemblyPath(path);
            foreach (Type type in SafeTypes(assembly))
            {
                AddCovers(type.CustomAttributes, covered);
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    AddCovers(method.CustomAttributes, covered);
                }
            }
        }

        return covered;
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t is not null)!;
        }
    }

    private static void AddCovers(IEnumerable<CustomAttributeData> attributes, HashSet<string> covered)
    {
        foreach (CustomAttributeData attribute in attributes)
        {
            if (attribute.AttributeType.FullName != "Xunit.TraitAttribute" || attribute.ConstructorArguments.Count != 2)
            {
                continue;
            }

            if (attribute.ConstructorArguments[0].Value is "Covers" && attribute.ConstructorArguments[1].Value is string value)
            {
                foreach (string id in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    covered.Add(id);
                }
            }
        }
    }

    private static DirectoryInfo RepositoryRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SourceFlow.slnx")))
        {
            dir = dir.Parent;
        }

        return dir ?? throw new InvalidOperationException("SourceFlow.slnx not found above " + AppContext.BaseDirectory);
    }
}

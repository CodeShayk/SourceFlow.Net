using System.Reflection;
using NetArchTest.Rules;

namespace SourceFlow.Architecture.Tests;

/// <summary>Dependency rules from ADR-027 §4 and plan 06 §9.</summary>
[Trait("Category", "Architecture")]
public sealed class DependencyRuleTests
{
    private static readonly string[] AllowedCorePrefixes =
    [
        "System.",
        "System",
        "Microsoft.Extensions.",
        "Polly",
        "netstandard",
        "mscorlib",
        Packages.Core,
    ];

    [Fact]
    [Trait("Id", "ARCH-01")]
    [Trait("Covers", "ARCH-01;ADR-027")]
    public void Core_references_only_the_BCL_Microsoft_Extensions_and_Polly()
    {
        Assembly core = Packages.Load(Packages.Core);

        string[] offending = core.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => !AllowedCorePrefixes.Any(prefix => name == prefix.TrimEnd('.') || name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        offending.ShouldBeEmpty($"{Packages.Core} may reference only the BCL, Microsoft.Extensions.* and Polly (ARCH-01).");
    }

    [Fact]
    [Trait("Id", "ARCH-02")]
    [Trait("Covers", "ARCH-02;ADR-027")]
    public void Core_never_references_an_adapter_or_other_SourceFlow_package()
    {
        Assembly core = Packages.Load(Packages.Core);

        Packages.ReferencedSourceFlowAssemblies(core).ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Packages.AdapterData), MemberType = typeof(Packages))]
    [Trait("Id", "ARCH-03")]
    [Trait("Covers", "ARCH-03;ADR-027")]
    public void Adapters_reference_the_core_only_never_each_other(string adapter)
    {
        Assembly assembly = Packages.Load(adapter);

        string[] others = Packages.ReferencedSourceFlowAssemblies(assembly)
            .Where(n => n != Packages.Core)
            .ToArray();

        others.ShouldBeEmpty($"{adapter} must depend on {Packages.Core} and its own SDK only (ARCH-03).");
    }

    [Theory]
    [MemberData(nameof(Packages.RuntimeData), MemberType = typeof(Packages))]
    [Trait("Id", "ARCH-05")]
    [Trait("Covers", "ARCH-05;ADR-004")]
    public void Runtime_packages_do_not_use_reflection_activation(string package)
    {
        NetArchTest.Rules.TestResult result = Types.InAssembly(Packages.Load(package))
            .ShouldNot()
            .HaveDependencyOnAny("System.Activator", "System.Reflection.Emit")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Theory]
    [MemberData(nameof(Packages.AdapterData), MemberType = typeof(Packages))]
    [Trait("Id", "ARCH-07")]
    [Trait("Covers", "ARCH-07")]
    public void Public_adapter_classes_are_sealed_unless_abstract_or_static(string adapter)
    {
        Type[] unsealed = Packages.Load(adapter).GetExportedTypes()
            .Where(t => t.IsClass && !t.IsSealed && !t.IsAbstract)
            .ToArray();

        unsealed.ShouldBeEmpty($"Public classes in {adapter} must be sealed unless designed for extension (ARCH-07).");
    }

    private static string Describe(NetArchTest.Rules.TestResult result)
    {
        return result.IsSuccessful
            ? string.Empty
            : "Offending types: " + string.Join(", ", result.FailingTypeNames ?? []);
    }
}

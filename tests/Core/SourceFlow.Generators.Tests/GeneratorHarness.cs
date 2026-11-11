using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using SourceFlow.Generators;

namespace SourceFlow.Generators.Tests;

/// <summary>Runs the generator and analyzers over in-memory sources against the real SourceFlow.Net contracts.</summary>
internal static class GeneratorHarness
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        string trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        return [.. trusted.Split(Path.PathSeparator)
            .Where(p => p.Length > 0)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(ICommand).Assembly.Location))];
    });

    public static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    public static CSharpCompilation Compile(params string[] sources)
    {
        return CSharpCompilation.Create(
            "TestAssembly",
            sources.Select((s, i) => CSharpSyntaxTree.ParseText(s, ParseOptions, path: $"Source{i}.cs")),
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    public static GeneratorRun Run(params string[] sources)
    {
        CSharpCompilation input = Compile(sources);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new SourceFlowGenerator().AsSourceGenerator()],
            parseOptions: ParseOptions,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGeneratorsAndUpdateCompilation(input, out Compilation output, out ImmutableArray<Diagnostic> diagnostics);
        return new GeneratorRun(driver, input, output, diagnostics);
    }

    public static ImmutableArray<Diagnostic> Analyze(params string[] sources)
    {
        GeneratorRun run = Run(sources);
        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new SourceFlowAnalyzer());
        return run.Output.WithAnalyzers(analyzers).GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
    }
}

internal sealed record GeneratorRun(GeneratorDriver Driver, Compilation Input, Compilation Output, ImmutableArray<Diagnostic> GeneratorDiagnostics)
{
    public GeneratorDriverRunResult Result => Driver.GetRunResult();

    public string Source(string hintSuffix)
    {
        return Result.GeneratedTrees.Single(t => t.FilePath.EndsWith(hintSuffix, StringComparison.Ordinal)).ToString();
    }

    public bool HasSource(string hintSuffix)
    {
        return Result.GeneratedTrees.Any(t => t.FilePath.EndsWith(hintSuffix, StringComparison.Ordinal));
    }

    public IReadOnlyList<Diagnostic> CompilationErrors()
    {
        return Output.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning).ToArray();
    }
}

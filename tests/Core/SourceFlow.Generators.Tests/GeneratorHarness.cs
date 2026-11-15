using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using SourceFlow.Generators;

namespace SourceFlow.Generators.Tests;

/// <summary>Runs the generator and analyzers over in-memory sources against the real SourceFlow.Net contracts.</summary>
internal static class GeneratorHarness
{
    public static ImmutableArray<MetadataReference> MetadataReferences => References.Value;

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

    /// <summary>Runs the generator with additional files (workflows) and build properties.</summary>
    public static GeneratorRun RunWithFiles(string[] sources, (string Path, string Text)[] files, IReadOnlyDictionary<string, string>? properties = null)
    {
        CSharpCompilation input = Compile(sources);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new SourceFlowGenerator().AsSourceGenerator()],
            files.Select(f => (AdditionalText)new InMemoryText(f.Path, f.Text)),
            ParseOptions,
            new Options(properties ?? new Dictionary<string, string>()),
            new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGeneratorsAndUpdateCompilation(input, out Compilation output, out ImmutableArray<Diagnostic> diagnostics);
        return new GeneratorRun(driver, input, output, diagnostics);
    }

    private sealed class InMemoryText(string path, string text) : AdditionalText
    {
        public override string Path { get; } = path;

        public override Microsoft.CodeAnalysis.Text.SourceText GetText(CancellationToken cancellationToken = default)
        {
            return Microsoft.CodeAnalysis.Text.SourceText.From(text);
        }
    }

    private sealed class Options(IReadOnlyDictionary<string, string> properties) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Values(properties);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new Values(new Dictionary<string, string>());

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new Values(new Dictionary<string, string>());

        private sealed class Values(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
            {
                return values.TryGetValue(key, out value);
            }
        }
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

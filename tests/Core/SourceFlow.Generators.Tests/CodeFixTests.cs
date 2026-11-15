using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace SourceFlow.Generators.Tests;

/// <summary>GEN-35: the SF0010 code fix (DC-1).</summary>
[Trait("Category", "Unit")]
public sealed class CodeFixTests
{
    private const string Source = Sources.Usings + """

        namespace T;

        [StronglyTypedId] public readonly partial record struct Aid(Guid Value);
        [MessageType("t.listed", 1)] public sealed record Listed(Aid A) : IEvent;
        [MessageType("t.forgotten", 1)] public sealed record Forgotten(Aid A) : IEvent;

        [System.Text.Json.Serialization.JsonSerializable(typeof(Listed))]
        public sealed partial class Ctx : System.Text.Json.Serialization.JsonSerializerContext;

        [SourceFlowModule(typeof(Ctx))] public sealed partial class Mod;
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "GEN-35")]
    [Trait("Covers", "DC-1")]
    public async Task SF0010_CodeFix_AddsTheMissingJsonSerializable()
    {
        using var workspace = new AdhocWorkspace();
        Project project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Default, "T", "T", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable),
            parseOptions: GeneratorHarness.ParseOptions,
            metadataReferences: GeneratorHarness.MetadataReferences));
        Document document = workspace.AddDocument(project.Id, "Source0.cs", SourceText.From(Source));

        Diagnostic diagnostic = (await AnalyzeAsync(document.Project)).Where(d => d.Id == "SF0010").ShouldHaveSingleItem();
        var actions = new List<CodeAction>();
        var context = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), Ct);
        await new JsonContextCodeFix().RegisterCodeFixesAsync(context);

        CodeAction fix = actions.ShouldHaveSingleItem();
        fix.Title.ShouldContain("Forgotten");
        ImmutableArray<CodeActionOperation> operations = await fix.GetOperationsAsync(Ct);
        Solution fixedSolution = operations.OfType<ApplyChangesOperation>().ShouldHaveSingleItem().ChangedSolution;
        Document fixedDocument = fixedSolution.GetDocument(document.Id)!;
        string text = (await fixedDocument.GetTextAsync(Ct)).ToString();

        text.ShouldContain("[System.Text.Json.Serialization.JsonSerializable(typeof(global::T.Forgotten))]");
        (await AnalyzeAsync(fixedDocument.Project)).ShouldNotContain(d => d.Id == "SF0010");
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(Project project)
    {
        Compilation compilation = (await project.GetCompilationAsync(Ct))!;
        return await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new SourceFlowAnalyzer())).GetAnalyzerDiagnosticsAsync(Ct);
    }
}

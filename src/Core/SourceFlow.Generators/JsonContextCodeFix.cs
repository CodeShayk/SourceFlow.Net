using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SourceFlow.Generators
{
    /// <summary>SF0010 code fix: adds <c>[JsonSerializable(typeof(T))]</c> to the module's JSON context (DC-1, GEN-35).</summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(JsonContextCodeFix))]
    public sealed class JsonContextCodeFix : CodeFixProvider
    {
        internal const string ContextKey = "context";
        internal const string TypeKey = "type";

        /// <inheritdoc />
        public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(Diagnostics.MissingJsonType.Id);

        /// <inheritdoc />
        public override FixAllProvider GetFixAllProvider()
        {
            return WellKnownFixAllProviders.BatchFixer;
        }

        /// <inheritdoc />
        public override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            foreach (Diagnostic diagnostic in context.Diagnostics)
            {
                if (!diagnostic.Properties.TryGetValue(ContextKey, out string? contextName) || contextName is null
                    || !diagnostic.Properties.TryGetValue(TypeKey, out string? typeName) || typeName is null)
                {
                    continue;
                }

                string title = $"Add [JsonSerializable(typeof({typeName.Replace("global::", string.Empty)}))] to {contextName.Split('.').Last()}";
                context.RegisterCodeFix(
                    CodeAction.Create(title, ct => AddAsync(context.Document.Project, contextName, typeName, ct), equivalenceKey: "SF0010:" + typeName),
                    diagnostic);
            }

            return Task.CompletedTask;
        }

        private static async Task<Solution> AddAsync(Project project, string contextName, string typeName, CancellationToken cancellationToken)
        {
            Solution solution = project.Solution;
            Compilation? compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            INamedTypeSymbol? jsonContext = compilation?.GetTypeByMetadataName(contextName);
            SyntaxReference? reference = jsonContext?.DeclaringSyntaxReferences.FirstOrDefault();
            if (reference is null || solution.GetDocument(reference.SyntaxTree) is not { } document)
            {
                return solution;
            }

            SyntaxNode node = await reference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
            if (node is not TypeDeclarationSyntax declaration)
            {
                return solution;
            }

            AttributeListSyntax attribute = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Attribute(
                        SyntaxFactory.ParseName("System.Text.Json.Serialization.JsonSerializable"),
                        SyntaxFactory.AttributeArgumentList(SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.AttributeArgument(SyntaxFactory.TypeOfExpression(SyntaxFactory.ParseTypeName(typeName))))))))
                .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);

            SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) ?? node.SyntaxTree.GetRoot(cancellationToken);
            TypeDeclarationSyntax updated = declaration.WithAttributeLists(declaration.AttributeLists.Add(attribute));
            return solution.WithDocumentSyntaxRoot(document.Id, root.ReplaceNode(declaration, updated));
        }
    }
}

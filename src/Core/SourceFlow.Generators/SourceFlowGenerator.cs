using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SourceFlow.Generators
{
    /// <summary>
    /// Emits strongly typed ids, aggregate / process-manager / projection dispatch and the module registration
    /// (ADR-004). Every pipeline step produces value-equatable models so unrelated edits cause no new output.
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class SourceFlowGenerator : IIncrementalGenerator
    {
        /// <inheritdoc />
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            IncrementalValuesProvider<IdModel> ids = context.SyntaxProvider
                .ForAttributeWithMetadataName(KnownNames.StronglyTypedIdAttribute, static (node, _) => node is TypeDeclarationSyntax, ModelBuilder.Id)
                .Where(static m => m is not null)!
                .WithTrackingName("Ids")!;

            IncrementalValuesProvider<MessageModel> messages = context.SyntaxProvider
                .ForAttributeWithMetadataName(KnownNames.MessageTypeAttribute, static (node, _) => node is TypeDeclarationSyntax, ModelBuilder.Message)
                .Where(static m => m is not null)!
                .WithTrackingName("Messages")!;

            IncrementalValuesProvider<AggregateModel> aggregates = context.SyntaxProvider
                .ForAttributeWithMetadataName(KnownNames.AggregateAttribute, static (node, _) => node is TypeDeclarationSyntax, ModelBuilder.Aggregate)
                .Where(static m => m is not null)!
                .WithTrackingName("Aggregates")!;

            IncrementalValuesProvider<ProcessManagerModel> processes = context.SyntaxProvider
                .ForAttributeWithMetadataName(KnownNames.ProcessManagerAttribute, static (node, _) => node is TypeDeclarationSyntax, ModelBuilder.ProcessManager)
                .Where(static m => m is not null)!
                .WithTrackingName("ProcessManagers")!;

            IncrementalValuesProvider<ProjectionModel> projections = context.SyntaxProvider
                .ForAttributeWithMetadataName(KnownNames.ProjectionAttribute, static (node, _) => node is TypeDeclarationSyntax, ModelBuilder.Projection)
                .Where(static m => m is not null)!
                .WithTrackingName("Projections")!;

            IncrementalValuesProvider<SessionProjectionModel> sessionProjections = context.SyntaxProvider
                .ForAttributeWithMetadataName(KnownNames.ProjectionAttribute, static (node, _) => node is TypeDeclarationSyntax, ModelBuilder.SessionProjection)
                .Where(static m => m is not null)!
                .WithTrackingName("SessionProjections")!;

            IncrementalValuesProvider<DomainQueryModel> domainQueries = context.SyntaxProvider
                .ForAttributeWithMetadataName(KnownNames.DomainQueryAttribute, static (node, _) => node is TypeDeclarationSyntax, ModelBuilder.DomainQuery)
                .Where(static m => m is not null)!
                .WithTrackingName("DomainQueries")!;

            context.RegisterSourceOutput(domainQueries, static (spc, model) =>
                spc.AddSource(Literal.HintName(model.FullName, "DomainQuery"), Emitters.DomainQuery(model)));

            IncrementalValuesProvider<ModuleModel> modules = context.SyntaxProvider
                .ForAttributeWithMetadataName(KnownNames.ModuleAttribute, static (node, _) => node is ClassDeclarationSyntax, ModelBuilder.Module)
                .Where(static m => m is not null)!
                .WithTrackingName("Modules")!;

            IncrementalValuesProvider<HandlerModel> handlers = context.SyntaxProvider
                .CreateSyntaxProvider(
                    static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                    static (ctx, ct) => ctx.SemanticModel.GetDeclaredSymbol((ClassDeclarationSyntax)ctx.Node, ct) is INamedTypeSymbol symbol
                        ? ModelBuilder.Handlers(symbol).ToEquatableArray()
                        : EquatableArray<HandlerModel>.Empty)
                .SelectMany(static (items, _) => items)
                .WithTrackingName("Handlers");

            context.RegisterSourceOutput(ids, static (spc, model) =>
                spc.AddSource(Literal.HintName(model.FullName, "Id"), Emitters.Id(model)));

            IncrementalValueProvider<ImmutableArray<MessageModel>> allMessages = messages.Collect();
            context.RegisterSourceOutput(aggregates.Combine(allMessages), static (spc, pair) =>
            {
                AggregateModel model = pair.Left;
                spc.AddSource(Literal.HintName(model.FullName, "Aggregate"), Emitters.Aggregate(model, Targeting(model, pair.Right)));
            });

            context.RegisterSourceOutput(processes, static (spc, model) =>
                spc.AddSource(Literal.HintName(model.FullName, "ProcessManager"), Emitters.ProcessManager(model)));

            context.RegisterSourceOutput(projections, static (spc, model) =>
                spc.AddSource(Literal.HintName(model.FullName, "Projection"), Emitters.Projection(model)));

            context.RegisterSourceOutput(sessionProjections, static (spc, model) =>
                spc.AddSource(Literal.HintName(model.FullName, "SessionProjection"), Emitters.SessionProjection(model)));

            var everything = modules.Collect()
                .Combine(ids.Collect())
                .Combine(allMessages)
                .Combine(aggregates.Collect())
                .Combine(processes.Collect())
                .Combine(projections.Collect().Combine(sessionProjections.Collect()))
                .Combine(handlers.Collect())
                .Combine(domainQueries.Collect());

            context.RegisterSourceOutput(everything, static (spc, all) =>
            {
                ImmutableArray<DomainQueryModel> queryModels = all.Right;
                var data = all.Left;
                ImmutableArray<HandlerModel> handlerModels = data.Right;
                ImmutableArray<ProjectionModel> projectionModels = data.Left.Right.Left;
                ImmutableArray<SessionProjectionModel> sessionProjectionModels = data.Left.Right.Right;
                ImmutableArray<ProcessManagerModel> processModels = data.Left.Left.Right;
                ImmutableArray<AggregateModel> aggregateModels = data.Left.Left.Left.Right;
                ImmutableArray<MessageModel> messageModels = data.Left.Left.Left.Left.Right;
                ImmutableArray<IdModel> idModels = data.Left.Left.Left.Left.Left.Right;
                ImmutableArray<ModuleModel> moduleModels = data.Left.Left.Left.Left.Left.Left;

                foreach (ModuleModel module in moduleModels)
                {
                    spc.AddSource(
                        Literal.HintName(module.FullName, "Module"),
                        Emitters.Module(module, idModels, messageModels, aggregateModels, processModels, projectionModels, sessionProjectionModels, handlerModels.Distinct().ToImmutableArray(), queryModels));
                }
            });

            RegisterManifest(context, modules);
        }

        // The static service manifest (doc 15 §3, WP-S.1). Its own models, so the dispatch outputs above stay cached
        // when only a Decide body changes.
        private static void RegisterManifest(IncrementalGeneratorInitializationContext context, IncrementalValuesProvider<ModuleModel> modules)
        {
            IncrementalValuesProvider<ManifestMessage> messages = context.SyntaxProvider
                .ForAttributeWithMetadataName(KnownNames.MessageTypeAttribute, static (node, _) => node is TypeDeclarationSyntax, ManifestBuilder.Message)
                .Where(static m => m is not null)!
                .WithTrackingName("ManifestMessages")!;

            IncrementalValuesProvider<ManifestAggregate> aggregates = context.SyntaxProvider
                .ForAttributeWithMetadataName(KnownNames.AggregateAttribute, static (node, _) => node is TypeDeclarationSyntax, ManifestBuilder.Aggregate)
                .Where(static m => m is not null)!
                .WithTrackingName("ManifestAggregates")!;

            IncrementalValuesProvider<ManifestProcess> processes = context.SyntaxProvider
                .ForAttributeWithMetadataName(KnownNames.ProcessManagerAttribute, static (node, _) => node is TypeDeclarationSyntax, ManifestBuilder.Process)
                .Where(static m => m is not null)!
                .WithTrackingName("ManifestProcessManagers")!;

            IncrementalValuesProvider<ManifestProjection> projections = context.SyntaxProvider
                .ForAttributeWithMetadataName(KnownNames.ProjectionAttribute, static (node, _) => node is TypeDeclarationSyntax, ManifestBuilder.Projection)
                .Where(static m => m is not null)!
                .WithTrackingName("ManifestProjections")!;

            IncrementalValuesProvider<ManifestHandler> handlers = context.SyntaxProvider
                .CreateSyntaxProvider(
                    static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                    static (ctx, ct) => ctx.SemanticModel.GetDeclaredSymbol((ClassDeclarationSyntax)ctx.Node, ct) is INamedTypeSymbol symbol
                        ? ManifestBuilder.Handlers(symbol, ctx.SemanticModel.Compilation, ct).ToEquatableArray()
                        : EquatableArray<ManifestHandler>.Empty)
                .SelectMany(static (items, _) => items)
                .WithTrackingName("ManifestHandlers");

            IncrementalValueProvider<ManifestAssembly> assembly = context.CompilationProvider
                .Select(ManifestBuilder.Assembly)
                .WithTrackingName("ManifestAssembly");

            var manifest = assembly
                .Combine(messages.Collect())
                .Combine(aggregates.Collect())
                .Combine(handlers.Collect())
                .Combine(processes.Collect())
                .Combine(projections.Collect());

            // The static manifest without workflows: what the workflows are validated against.
            IncrementalValueProvider<string?> staticManifest = manifest.Select(static (all, _) =>
            {
                ImmutableArray<ManifestProjection> projectionModels = all.Right;
                ImmutableArray<ManifestProcess> processModels = all.Left.Right;
                ImmutableArray<ManifestHandler> handlerModels = all.Left.Left.Right.Distinct().ToImmutableArray();
                ImmutableArray<ManifestAggregate> aggregateModels = all.Left.Left.Left.Right;
                ImmutableArray<ManifestMessage> messageModels = all.Left.Left.Left.Left.Right;
                ManifestAssembly target = all.Left.Left.Left.Left.Left;
                return target.CanEmit && ManifestEmitter.HasContent(messageModels, aggregateModels, handlerModels, processModels, projectionModels)
                    ? ManifestEmitter.Json(target, messageModels, aggregateModels, handlerModels, processModels, projectionModels)
                    : null;
            }).WithTrackingName("ManifestJson");

            IncrementalValuesProvider<WorkflowFile> files = context.AdditionalTextsProvider
                .Where(static t => t.Path.EndsWith(WorkflowPipeline.Suffix, System.StringComparison.OrdinalIgnoreCase))
                .Select(static (t, ct) => new WorkflowFile(t.Path, t.GetText(ct)?.ToString() ?? string.Empty))
                .WithTrackingName("WorkflowFiles");
            IncrementalValueProvider<ImmutableArray<WorkflowFile>> allFiles = files.Collect();
            IncrementalValueProvider<WorkflowContext> workflowContext = context.CompilationProvider
                .Combine(allFiles)
                .Select(static (pair, ct) => WorkflowPipeline.Context(pair.Left, pair.Right.Length > 0, ct))
                .WithTrackingName("WorkflowContext");
            IncrementalValueProvider<WorkflowOptions> options = context.AnalyzerConfigOptionsProvider.Select(WorkflowPipeline.Options);
            IncrementalValueProvider<ImmutableArray<ModuleModel>> moduleModels = modules.Collect();

            IncrementalValuesProvider<WorkflowOutput> workflows = files
                .Combine(allFiles)
                .Combine(workflowContext)
                .Combine(staticManifest)
                .Combine(moduleModels)
                .Combine(options)
                .Select(static (all, ct) => WorkflowPipeline.Build(
                    all.Left.Left.Left.Left.Left,
                    all.Left.Left.Left.Left.Right,
                    all.Left.Left.Left.Right,
                    all.Left.Left.Right,
                    all.Left.Right,
                    all.Right,
                    ct))
                .WithTrackingName("Workflows");

            context.RegisterSourceOutput(workflows, static (spc, output) =>
            {
                foreach (WorkflowDiagnostic d in output.Diagnostics)
                {
                    DiagnosticDescriptor descriptor = d.Id == "SF0021" ? Diagnostics.WorkflowHookMissing : Diagnostics.WorkflowInvalid;
                    spc.ReportDiagnostic(Diagnostic.Create(descriptor, WorkflowPipeline.Location(d, null), d.IsError ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, null, null, d.Message));
                }

                if (output.HintName is not null && output.Source is not null)
                {
                    spc.AddSource(output.HintName, output.Source);
                }
            });

            IncrementalValueProvider<ImmutableArray<WorkflowOutput>> allWorkflows = workflows.Collect();
            context.RegisterSourceOutput(allWorkflows.Combine(moduleModels), static (spc, pair) =>
            {
                WorkflowRegistration[] registrations = pair.Left.Select(o => o.Registration).Where(r => r is not null).Select(r => r!).ToArray();
                ModuleModel? module = pair.Right.OrderBy(m => m.FullName, System.StringComparer.Ordinal).FirstOrDefault();
                if (registrations.Length == 0 || module is null)
                {
                    return;
                }

                spc.AddSource(Literal.HintName(module.FullName, "Workflows"), WorkflowPipeline.Registration(module, registrations));
                foreach ((string hint, string source) in WorkflowPipeline.Engine())
                {
                    spc.AddSource(hint, source);
                }
            });

            context.RegisterSourceOutput(manifest.Combine(allWorkflows), static (spc, pair) =>
            {
                var all = pair.Left;
                ImmutableArray<ManifestProjection> projectionModels = all.Right;
                ImmutableArray<ManifestProcess> processModels = all.Left.Right;
                ImmutableArray<ManifestHandler> handlerModels = all.Left.Left.Right.Distinct().ToImmutableArray();
                ImmutableArray<ManifestAggregate> aggregateModels = all.Left.Left.Left.Right;
                ImmutableArray<ManifestMessage> messageModels = all.Left.Left.Left.Left.Right;
                ManifestAssembly target = all.Left.Left.Left.Left.Left;
                if (!target.CanEmit || !ManifestEmitter.HasContent(messageModels, aggregateModels, handlerModels, processModels, projectionModels))
                {
                    return;
                }

                ImmutableArray<WorkflowManifestEntry> entries = pair.Right.Select(o => o.Manifest).Where(m => m is not null).Select(m => m!).ToImmutableArray();
                string json = ManifestEmitter.Json(target, messageModels, aggregateModels, handlerModels, processModels, projectionModels, entries);
                spc.AddSource("SourceFlowManifest.g.cs", ManifestEmitter.Source(json));
            });
        }

        private static IReadOnlyList<(string Command, string? Member)> Targeting(AggregateModel aggregate, ImmutableArray<MessageModel> messages)
        {
            var result = new List<(string, string?)>();
            foreach (MessageModel message in messages.Where(m => m.TargetAggregate == aggregate.FullName).OrderBy(m => m.FullName, System.StringComparer.Ordinal))
            {
                MemberModel[] candidates = message.Members.Where(m => m.TypeFullName == aggregate.IdType).ToArray();
                MemberModel[] marked = candidates.Where(m => m.IsAggregateId).ToArray();
                string? member = marked.Length == 1 ? marked[0].Name : candidates.Length == 1 ? candidates[0].Name : null;
                result.Add((message.FullName, member));
            }

            return result;
        }
    }
}

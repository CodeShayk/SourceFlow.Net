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

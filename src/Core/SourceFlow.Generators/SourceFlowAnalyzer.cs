using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace SourceFlow.Generators
{
    /// <summary>Contract analyzers SF0001–SF0010 and SF0014 (design doc 03 §9).</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SourceFlowAnalyzer : DiagnosticAnalyzer
    {
        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
            Diagnostics.MissingMessageType,
            Diagnostics.DuplicateMessageType,
            Diagnostics.CommandTarget,
            Diagnostics.ImpureWhen,
            Diagnostics.NoOwner,
            Diagnostics.MissingJsonType,
            Diagnostics.AsyncDecide,
            Diagnostics.StateShape,
            Diagnostics.NonDeterministic,
            Diagnostics.EventWithoutWhen,
            Diagnostics.OwnedAndRouted);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                if (start.Compilation.GetTypeByMetadataName(KnownNames.Message) is null)
                {
                    return;
                }

                var seen = new ConcurrentDictionary<(string, int), INamedTypeSymbol>();
                start.RegisterSymbolAction(symbolContext => AnalyzeType(symbolContext, seen), SymbolKind.NamedType);
                start.RegisterOperationAction(AnalyzeNonDeterminism, OperationKind.Invocation, OperationKind.PropertyReference, OperationKind.ObjectCreation);
                start.RegisterOperationAction(AnalyzeWhenAssignment, OperationKind.SimpleAssignment, OperationKind.CompoundAssignment, OperationKind.Increment, OperationKind.Decrement);
                start.RegisterOperationAction(AnalyzeDecidedEvent, OperationKind.ObjectCreation);
                start.RegisterOperationAction(AnalyzeCommandRoute, OperationKind.Invocation);

                var inventory = new CompilationInventory();
                start.RegisterSymbolAction(symbolContext => inventory.Collect((INamedTypeSymbol)symbolContext.Symbol), SymbolKind.NamedType);
                start.RegisterCompilationEndAction(inventory.Report);
            });
        }

        private static void AnalyzeType(SymbolAnalysisContext context, ConcurrentDictionary<(string, int), INamedTypeSymbol> seen)
        {
            var type = (INamedTypeSymbol)context.Symbol;
            if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
            {
                return;
            }

            Location location = type.Locations.FirstOrDefault() ?? Location.None;
            string? kind = ModelBuilder.KindOf(type);
            AttributeData? messageType = type.FindAttribute(KnownNames.MessageTypeAttribute);

            if (kind is not null && messageType is null && !type.IsAbstract)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.MissingMessageType, location, type.Name));
            }

            if (messageType is not null && messageType.ConstructorArguments.Length > 0 && messageType.ConstructorArguments[0].Value is string name)
            {
                int version = messageType.ConstructorArguments.Length > 1 && messageType.ConstructorArguments[1].Value is int v ? v : 1;
                INamedTypeSymbol first = seen.GetOrAdd((name, version), type);
                if (!SymbolEqualityComparer.Default.Equals(first, type))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.DuplicateMessageType, location, name, version, first.Name, type.Name));
                }
            }

            if (type.FindInterface(KnownNames.CommandOf) is { } commandOf && !type.IsAbstract)
            {
                AnalyzeCommandTarget(context, type, commandOf, location);
            }

            if (type.FindAttribute(KnownNames.AggregateAttribute) is not null)
            {
                AnalyzeStateShape(context, type, location, "aggregate");
                foreach (IMethodSymbol decide in type.GetMembers("Decide").OfType<IMethodSymbol>())
                {
                    bool asyncReturn = decide.ReturnType.Is("System.Threading.Tasks.Task`1") || decide.ReturnType.Is("System.Threading.Tasks.ValueTask`1")
                        || decide.ReturnType.Is("System.Threading.Tasks.Task") || decide.ReturnType.Is("System.Threading.Tasks.ValueTask");
                    if (decide.IsAsync || asyncReturn)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.AsyncDecide, decide.Locations.FirstOrDefault() ?? location, type.Name));
                    }
                }

                foreach (IMethodSymbol when in type.GetMembers("When").OfType<IMethodSymbol>())
                {
                    if (when.IsAsync)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.ImpureWhen, when.Locations.FirstOrDefault() ?? location, type.Name, "it is async"));
                    }
                }
            }

            if (type.FindAttribute(KnownNames.ProcessManagerAttribute) is not null)
            {
                AnalyzeStateShape(context, type, location, "process manager");
            }
        }

        private static void AnalyzeCommandTarget(SymbolAnalysisContext context, INamedTypeSymbol command, INamedTypeSymbol commandOf, Location location)
        {
            ITypeSymbol aggregate = commandOf.TypeArguments[0];
            if (aggregate.FindInterface(KnownNames.Aggregate) is not { } contract)
            {
                return;
            }

            ITypeSymbol idType = contract.TypeArguments[1];
            IMethodSymbol? primary = command.InstanceConstructors.OrderByDescending(c => c.Parameters.Length).FirstOrDefault();
            IPropertySymbol[] candidates = command.GetMembers().OfType<IPropertySymbol>()
                .Where(p => !p.IsStatic && p.DeclaredAccessibility == Accessibility.Public && SymbolEqualityComparer.Default.Equals(p.Type, idType))
                .ToArray();
            int marked = candidates.Count(p => p.FindAttribute(KnownNames.AggregateIdAttribute) is not null
                || primary?.Parameters.FirstOrDefault(x => x.Name == p.Name)?.FindAttribute(KnownNames.AggregateIdAttribute) is not null);

            string? problem = candidates.Length == 0
                ? "it has no member of the id type"
                : candidates.Length > 1 && marked != 1 ? $"it has {candidates.Length} members of the id type" : null;
            if (problem is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.CommandTarget, location, command.Name, aggregate.Name, problem, idType.Name));
            }
        }

        private static void AnalyzeStateShape(SymbolAnalysisContext context, INamedTypeSymbol type, Location location, string what)
        {
            if (type.ContainingType is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.StateShape, location, type.Name, $"a top-level type (a nested {what} is not supported)"));
                return;
            }

            if (!type.IsRecord || type.IsValueType || !type.IsSealed || !type.IsPartial())
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.StateShape, location, type.Name, $"a 'sealed partial record' to be used as a {what}"));
            }

            foreach (IPropertySymbol property in type.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.SetMethod is { IsInitOnly: false } && !property.IsStatic && property.Name != "EqualityContract")
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.StateShape, property.Locations.FirstOrDefault() ?? location, type.Name, $"immutable: property '{property.Name}' has a 'set' accessor; use 'init'"));
                }
            }
        }

        private static void AnalyzeNonDeterminism(OperationAnalysisContext context)
        {
            if (!InPureMethod(context.ContainingSymbol, out string method))
            {
                return;
            }

            string? api = context.Operation switch
            {
                IInvocationOperation { TargetMethod: { Name: "NewGuid", ContainingType: var t } } when t.Is("System.Guid") => "Guid.NewGuid()",
                IPropertyReferenceOperation { Property: { Name: "Now" or "UtcNow" or "Today", ContainingType: var t } } p when t.Is("System.DateTime") || t.Is("System.DateTimeOffset") => $"{p.Property.ContainingType.Name}.{p.Property.Name}",
                IPropertyReferenceOperation { Property: { Name: "TickCount" or "TickCount64", ContainingType: var t } } p when t.Is("System.Environment") => $"Environment.{p.Property.Name}",
                IPropertyReferenceOperation { Property: { Name: "Shared", ContainingType: var t } } when t.Is("System.Random") => "Random.Shared",
                IObjectCreationOperation { Type: var t } when t.Is("System.Random") => "new Random()",
                _ => null,
            };

            if (api is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.NonDeterministic, context.Operation.Syntax.GetLocation(), api, method));
            }
        }

        private static void AnalyzeWhenAssignment(OperationAnalysisContext context)
        {
            if (context.ContainingSymbol is not IMethodSymbol { Name: "When" } method
                || method.ContainingType?.FindAttribute(KnownNames.AggregateAttribute) is null)
            {
                return;
            }

            IOperation? target = context.Operation switch
            {
                IAssignmentOperation assignment => assignment.Target,
                IIncrementOrDecrementOperation increment => increment.Target,
                _ => null,
            };

            bool mutatesState = target switch
            {
                IFieldReferenceOperation field => field.Field.IsStatic || field.Instance is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance },
                IPropertyReferenceOperation property => property.Property.IsStatic || property.Instance is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance },
                _ => false,
            };

            if (mutatesState)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.ImpureWhen, context.Operation.Syntax.GetLocation(), method.ContainingType.Name, "it assigns state; return 'this with { … }' instead"));
            }
        }

        // SF0005: an event created in an aggregate's Decide that no When of the aggregate accepts.
        private static void AnalyzeDecidedEvent(OperationAnalysisContext context)
        {
            var creation = (IObjectCreationOperation)context.Operation;
            if (creation.Type is not INamedTypeSymbol created || !created.Implements(KnownNames.Event) || created.Implements(KnownNames.IntegrationEvent))
            {
                return;
            }

            ISymbol? current = context.ContainingSymbol;
            while (current is IMethodSymbol { MethodKind: MethodKind.LambdaMethod or MethodKind.LocalFunction } local)
            {
                current = local.ContainingSymbol;
            }

            if (current is not IMethodSymbol { Name: "Decide" } decide
                || decide.ContainingType is not { } aggregate
                || aggregate.FindAttribute(KnownNames.AggregateAttribute) is null)
            {
                return;
            }

            bool handled = aggregate.GetMembers("When").OfType<IMethodSymbol>()
                .Any(when => when.Parameters.Length >= 1 && created.InheritsOrIs(when.Parameters[0].Type));
            if (!handled)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.EventWithoutWhen, creation.Syntax.GetLocation(), aggregate.Name, created.Name));
            }
        }

        // SF0014: routing.Command<T>().To(...) for a command this compilation owns (a Decide or an ICommandHandler).
        private static void AnalyzeCommandRoute(OperationAnalysisContext context)
        {
            var invocation = (IInvocationOperation)context.Operation;
            if (invocation.TargetMethod is not { Name: "To" } to || !to.ContainingType.Is("SourceFlow.Runtime.CommandRouteBuilder")
                || invocation.Instance is not IInvocationOperation { TargetMethod: { Name: "Command", IsGenericMethod: true } command }
                || !command.ContainingType.Is("SourceFlow.Runtime.RoutingBuilder")
                || command.TypeArguments[0] is not INamedTypeSymbol commandType)
            {
                return;
            }

            string? owner = OwnerOf(commandType, context.Compilation);
            if (owner is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.OwnedAndRouted, invocation.Syntax.GetLocation(), commandType.Name, owner));
            }
        }

        private static string? OwnerOf(INamedTypeSymbol command, Compilation compilation)
        {
            // The target aggregate's Decide (in source or in a referenced module).
            if (command.FindInterface(KnownNames.CommandOf)?.TypeArguments[0] is INamedTypeSymbol aggregate
                && aggregate.FindAttribute(KnownNames.AggregateAttribute) is not null
                && aggregate.GetMembers("Decide").OfType<IMethodSymbol>().Any(d => d.Parameters.Length >= 1 && SymbolEqualityComparer.Default.Equals(d.Parameters[0].Type, command)))
            {
                return aggregate.Name;
            }

            // An ICommandHandler<TCommand, …> declared in this compilation.
            foreach (INamedTypeSymbol type in AllTypes(compilation.Assembly.GlobalNamespace))
            {
                if (!type.IsAbstract && type.AllInterfaces.Any(i => SymbolExtensions.MetadataName(i) == KnownNames.CommandHandler
                    && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], command)))
                {
                    return type.Name;
                }
            }

            return null;
        }

        private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol root)
        {
            foreach (INamespaceOrTypeSymbol member in root.GetMembers())
            {
                if (member is INamespaceSymbol ns)
                {
                    foreach (INamedTypeSymbol nested in AllTypes(ns))
                    {
                        yield return nested;
                    }
                }
                else if (member is INamedTypeSymbol type)
                {
                    yield return type;
                }
            }
        }

        private static bool InPureMethod(ISymbol symbol, out string method)
        {
            method = string.Empty;
            ISymbol? current = symbol;
            while (current is IMethodSymbol { MethodKind: MethodKind.LambdaMethod or MethodKind.LocalFunction } local)
            {
                current = local.ContainingSymbol;
            }

            if (current is not IMethodSymbol m || m.ContainingType is null)
            {
                return false;
            }

            bool aggregate = m.ContainingType.FindAttribute(KnownNames.AggregateAttribute) is not null && (m.Name == "Decide" || m.Name == "When");
            bool process = m.ContainingType.FindAttribute(KnownNames.ProcessManagerAttribute) is not null && (m.Name == "React" || m.Name == "CorrelateBy");
            method = m.Name;
            return aggregate || process;
        }
    }
}

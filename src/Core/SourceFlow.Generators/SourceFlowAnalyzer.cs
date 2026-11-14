using System;
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
            Diagnostics.IdBackingMismatch,
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
            Diagnostics.OwnedAndRouted,
            Diagnostics.QueryInWriteModel,
            Diagnostics.QueryInCommandHandler,
            Diagnostics.DomainQueryShape,
            Diagnostics.UnresolvableSubject,
            Diagnostics.PersonalDataPublishedAsIs,
            Diagnostics.QueryablePersonalData,
            Diagnostics.UndeclaredOutcome,
            Diagnostics.UpcasterConstructor,
            Diagnostics.MutableStateCollection,
            Diagnostics.DecideForOtherAggregate);

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
                start.RegisterOperationAction(AnalyzeWhenEffects, OperationKind.Invocation, OperationKind.ObjectCreation, OperationKind.PropertyReference);
                start.RegisterOperationAction(AnalyzeDecidedEvent, OperationKind.ObjectCreation);
                start.RegisterOperationAction(AnalyzeCommandRoute, OperationKind.Invocation);
                start.RegisterOperationAction(AnalyzeAsIsPublication, OperationKind.Invocation);
                start.RegisterOperationAction(AnalyzeQueryUse, OperationKind.Invocation, OperationKind.PropertyReference);

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
            AnalyzeIdBacking(context, type, location);
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
                    else if (decide.Parameters.Length == 2 && decide.Parameters[0].Type.Implements(KnownNames.Command))
                    {
                        if (decide.Parameters[0].Type.FindInterface(KnownNames.CommandOf) is { } target
                            && !SymbolEqualityComparer.Default.Equals(target.TypeArguments[0], type))
                        {
                            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.DecideForOtherAggregate, decide.Locations.FirstOrDefault() ?? location,
                                type.Name, decide.Parameters[0].Type.Name, target.TypeArguments[0].Name));
                        }

                        AnalyzeOutcome(context, decide, type.Name + ".Decide", decide.Parameters[0].Type, location);
                    }
                }

                foreach (IPropertySymbol property in type.GetMembers().OfType<IPropertySymbol>().Where(p => !p.IsStatic && p.Name != "EqualityContract"))
                {
                    if (MutableCollection(property.Type) is { } collection)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.MutableStateCollection, property.Locations.FirstOrDefault() ?? location, type.Name, property.Name, collection));
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

            if (!type.IsAbstract)
            {
                foreach (INamedTypeSymbol contract in type.AllInterfaces.Where(i => SymbolExtensions.MetadataName(i) == KnownNames.CommandHandler))
                {
                    ITypeSymbol command = contract.TypeArguments[0];
                    if (type.GetMembers("HandleAsync").OfType<IMethodSymbol>()
                        .FirstOrDefault(m => m.Parameters.Length >= 1 && SymbolEqualityComparer.Default.Equals(m.Parameters[0].Type, command)) is { } handle)
                    {
                        AnalyzeOutcome(context, handle, type.Name + ".HandleAsync", command, location);
                    }
                }
            }

            if (!type.IsAbstract && type.AllInterfaces.Any(i => SymbolExtensions.MetadataName(i) == KnownNames.Upcaster)
                && !type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.UpcasterConstructor, location, type.Name));
            }

            if (type.FindAttribute(KnownNames.ProcessManagerAttribute) is not null)
            {
                AnalyzeStateShape(context, type, location, "process manager");
            }

            if (type.FindAttribute(KnownNames.DomainQueryAttribute) is { } domainQuery)
            {
                AnalyzeDomainQuery(context, type, domainQuery, location);
            }

            AnalyzePersonalData(context, type);
        }

        // SF0011 / SF0017 (doc 10 §4): a [PersonalData] member's subject must be a member of the event; [Queryable] on it does nothing.
        // SF0022 (CR-30): [StronglyTypedId(Backing = …)] must agree with the type of Value, which decides the storage.
        private static void AnalyzeIdBacking(SymbolAnalysisContext context, INamedTypeSymbol type, Location location)
        {
            AttributeData? attribute = type.FindAttribute(KnownNames.StronglyTypedIdAttribute);
            if (attribute is null)
            {
                return;
            }

            TypedConstant declared = attribute.NamedArguments.FirstOrDefault(a => a.Key == "Backing").Value;
            if (declared.IsNull || declared.Value is not int backing)
            {
                return;   // not set: the value's type decides
            }

            IPropertySymbol? value = type.GetMembers("Value").OfType<IPropertySymbol>().FirstOrDefault(p => !p.IsStatic);
            if (value is null)
            {
                return;
            }

            // IdBacking: Guid = 0, String = 1, Int64 = 2.
            int actual = value.Type.SpecialType switch
            {
                SpecialType.System_String => 1,
                SpecialType.System_Int64 => 2,
                _ => value.Type.Is("System.Guid") ? 0 : -1,
            };
            if (actual >= 0 && actual != backing)
            {
                string[] names = ["Guid", "String", "Int64"];
                string declaredName = backing is >= 0 and <= 2 ? names[backing] : backing.ToString(System.Globalization.CultureInfo.InvariantCulture);
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.IdBackingMismatch, location, type.Name, declaredName, value.Type.ToDisplayString()));
            }
        }

        private static void AnalyzePersonalData(SymbolAnalysisContext context, INamedTypeSymbol type)
        {
            bool isEvent = type.FindInterface(KnownNames.Event) is not null;
            foreach (ISymbol member in type.GetMembers().Where(m => m is IPropertySymbol or IFieldSymbol))
            {
                AttributeData? personal = member.FindMemberAttribute(KnownNames.PersonalDataAttribute);
                if (personal is null)
                {
                    continue;
                }

                Location location = member.Locations.FirstOrDefault() ?? Location.None;
                if (member.FindMemberAttribute(KnownNames.QueryableAttribute) is not null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.QueryablePersonalData, location, type.Name, member.Name));
                }

                if (isEvent && personal.NamedArguments.FirstOrDefault(a => a.Key == "Subject").Value.Value is string subject && !HasMember(type, subject))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.UnresolvableSubject, location, subject, type.Name, member.Name));
                }
            }
        }

        private static bool HasMember(INamedTypeSymbol type, string name)
        {
            for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
            {
                if (current.GetMembers(name).Any(m => m is IPropertySymbol or IFieldSymbol))
                {
                    return true;
                }
            }

            return false;
        }

        // SF0013 (doc 10 §4): routing.Publish<TEvent>().AsIs() for an event holding [PersonalData].
        private static void AnalyzeAsIsPublication(OperationAnalysisContext context)
        {
            var invocation = (IInvocationOperation)context.Operation;
            if (invocation.TargetMethod is not { Name: "AsIs" } asIs || !asIs.ContainingType.Is("SourceFlow.Runtime.PublicationBuilder`1")
                || asIs.ContainingType.TypeArguments[0] is not INamedTypeSymbol published)
            {
                return;
            }

            ISymbol? personal = published.GetMembers().FirstOrDefault(m => m is IPropertySymbol && m.FindMemberAttribute(KnownNames.PersonalDataAttribute) is not null);
            if (personal is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.PersonalDataPublishedAsIs, invocation.Syntax.GetLocation(), published.Name, personal.Name));
            }
        }

        // SF0018 (build-time part): the shape of a [DomainQuery]. Binding against the catalog happens at startup (ADR-030).
        private static void AnalyzeDomainQuery(SymbolAnalysisContext context, INamedTypeSymbol type, AttributeData attribute, Location location)
        {
            void Report(string problem)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.DomainQueryShape, location, type.Name, problem));
            }

            if (attribute.ConstructorArguments.Length == 0 || attribute.ConstructorArguments[0].Value is not string name || name.Length == 0)
            {
                Report("it needs a name, for example [DomainQuery(\"accounts.overdrawn\", 1)]");
                return;
            }

            if (type.FindInterface(KnownNames.DomainQuery) is not { } contract)
            {
                Report("it must implement IDomainQuery<TResult>");
                return;
            }

            if (type.IsValueType || type.IsAbstract || type.ContainingType is not null || !type.IsPartial())
            {
                Report("it must be a top-level 'partial record' (or partial class)");
                return;
            }

            IFieldSymbol? text = type.GetMembers().OfType<IFieldSymbol>()
                .FirstOrDefault(f => f.IsConst && f.Type.SpecialType == SpecialType.System_String && (f.Name == "Sfql" || f.Name == "Sql"));
            if (text?.ConstantValue is not string query)
            {
                Report("it needs a 'const string Sfql' (or 'Sql') with the query text");
                return;
            }

            if (contract.TypeArguments[0] is not INamedTypeSymbol result
                || !result.InstanceConstructors.Any(c => c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Length > 0))
            {
                Report($"its result '{contract.TypeArguments[0].Name}' needs a public constructor with a parameter per output column");
                return;
            }

            var members = new HashSet<string>(
                type.GetMembers().OfType<IPropertySymbol>().Where(p => !p.IsStatic && p.DeclaredAccessibility == Accessibility.Public).Select(p => p.Name),
                System.StringComparer.OrdinalIgnoreCase);
            foreach (string parameter in Parameters(query))
            {
                if (!members.Contains(parameter))
                {
                    Report($"the text uses @{parameter} but '{type.Name}' has no member '{parameter}'");
                }
            }
        }

        // @names in a query text, outside string and quoted-name literals.
        private static IEnumerable<string> Parameters(string text)
        {
            var found = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            char quote = '\0';
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (c is '\'' or '"')
                {
                    quote = c;
                }
                else if (c == '@' && i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] == '_'))
                {
                    int start = ++i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    {
                        i++;
                    }

                    found.Add(text.Substring(start, i - start));
                    i--;
                }
            }

            return found;
        }

        // SF0015 / SF0016 (Q-R1): IQueryService or IAggregateReader used where state must come from the own stream.
        private static void AnalyzeQueryUse(OperationAnalysisContext context)
        {
            INamedTypeSymbol? target = context.Operation switch
            {
                IInvocationOperation invocation => invocation.TargetMethod.ContainingType,
                IPropertyReferenceOperation property => property.Property.ContainingType,
                _ => null,
            };
            if (target is null || !(target.Is(KnownNames.QueryService) || target.Is(KnownNames.AggregateReader)))
            {
                return;
            }

            ISymbol? current = context.ContainingSymbol;
            while (current is IMethodSymbol { MethodKind: MethodKind.LambdaMethod or MethodKind.LocalFunction } local)
            {
                current = local.ContainingSymbol;
            }

            if (current?.ContainingType is not { } owner)
            {
                return;
            }

            string api = target.Name;
            if (owner.FindAttribute(KnownNames.AggregateAttribute) is not null
                || owner.FindAttribute(KnownNames.ProcessManagerAttribute) is not null
                || owner.FindAttribute(KnownNames.ProjectionAttribute) is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.QueryInWriteModel, context.Operation.Syntax.GetLocation(), owner.Name, api));
            }
            else if (owner.AllInterfaces.Any(i => SymbolExtensions.MetadataName(i) == KnownNames.CommandHandler))
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.QueryInCommandHandler, context.Operation.Syntax.GetLocation(), owner.Name, api));
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
            IMethodSymbol? primary = Conventions.PrimaryConstructor(command);   // the generator's rule, shared (CR-54)
            IPropertySymbol[] candidates = command.GetMembers().OfType<IPropertySymbol>()
                .Where(p => !p.IsStatic && p.DeclaredAccessibility == Accessibility.Public && SymbolEqualityComparer.Default.Equals(p.Type, idType))
                .ToArray();
            int marked = candidates.Count(p => Conventions.IsMarkedTarget(p, primary));

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
                IInvocationOperation { TargetMethod: { Name: "CreateVersion7", ContainingType: var t } } when t.Is("System.Guid") => "Guid.CreateVersion7()",
                IInvocationOperation { TargetMethod: { Name: "New", IsStatic: true, ContainingType: var t } } when t.FindAttribute(KnownNames.StronglyTypedIdAttribute) is not null
                    => $"{t.Name}.New() (use ctx.NewId<{t.Name}>(…), which is deterministic)",
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
            if (AggregateWhen(context.ContainingSymbol) is not { } method)
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

        // The aggregate 'When' a symbol belongs to, through lambdas and local functions inside it (CR-51).
        private static IMethodSymbol? AggregateWhen(ISymbol? symbol)
        {
            ISymbol? current = symbol;
            while (current is IMethodSymbol { MethodKind: MethodKind.LambdaMethod or MethodKind.LocalFunction } local)
            {
                current = local.ContainingSymbol;
            }

            return current is IMethodSymbol { Name: "When" } when && when.ContainingType?.FindAttribute(KnownNames.AggregateAttribute) is not null ? when : null;
        }

        // I/O in a When (CR-51): files, the network, databases and the console. A When folds history, so it may run any time.
        private static readonly string[] IoNamespaces = ["System.IO", "System.Net", "System.Data", "Microsoft.EntityFrameworkCore", "Microsoft.Data", "Npgsql", "Dapper"];

        private static readonly HashSet<string> MutatingMethods = new(StringComparer.Ordinal)
        {
            "Add", "AddRange", "Remove", "RemoveAt", "RemoveAll", "RemoveRange", "Clear", "Insert", "InsertRange", "Push", "Pop",
            "Enqueue", "Dequeue", "TryAdd", "TryRemove", "TryUpdate", "AddOrUpdate", "GetOrAdd", "Sort", "Reverse", "Set", "UnionWith",
            "ExceptWith", "IntersectWith", "SymmetricExceptWith",
        };

        // SF0004 beyond assignments (CR-51): I/O, and changing a collection reachable from 'this' through a method call.
        private static void AnalyzeWhenEffects(OperationAnalysisContext context)
        {
            if (AggregateWhen(context.ContainingSymbol) is not { } method)
            {
                return;
            }

            INamedTypeSymbol? used = context.Operation switch
            {
                IInvocationOperation invocation => invocation.TargetMethod.ContainingType,
                IObjectCreationOperation creation => creation.Type as INamedTypeSymbol,
                IPropertyReferenceOperation property => property.Property.ContainingType,
                _ => null,
            };
            string? ns = used?.ContainingNamespace?.ToDisplayString();
            if (used is not null && ns is not null
                && (used.Is("System.Console") || IoNamespaces.Any(n => ns == n || ns.StartsWith(n + ".", StringComparison.Ordinal))))
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.ImpureWhen, context.Operation.Syntax.GetLocation(), method.ContainingType.Name, $"it does I/O ({used.Name})"));
                return;
            }

            if (context.Operation is IInvocationOperation { TargetMethod.IsStatic: false } call && MutatingMethods.Contains(call.TargetMethod.Name) && ReachableFromThis(call.Instance))
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.ImpureWhen, context.Operation.Syntax.GetLocation(), method.ContainingType.Name,
                    $"it changes state in place ({call.TargetMethod.Name}); build a new collection and return 'this with {{ … }}'"));
            }
        }

        // A member of 'this' (or of a member of 'this'), whatever the depth.
        private static bool ReachableFromThis(IOperation? instance)
        {
            while (true)
            {
                switch (instance)
                {
                    case IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance }:
                        return true;
                    case IFieldReferenceOperation field:
                        if (field.Field.IsStatic)
                        {
                            return true;
                        }

                        instance = field.Instance;
                        break;
                    case IPropertyReferenceOperation property:
                        if (property.Property.IsStatic)
                        {
                            return true;
                        }

                        instance = property.Instance;
                        break;
                    case IConversionOperation conversion:
                        instance = conversion.Operand;
                        break;
                    default:
                        return false;
                }
            }
        }

        // A mutable collection type for SF0024: arrays and the System.Collections.Generic and Concurrent collections.
        private static string? MutableCollection(ITypeSymbol type)
        {
            if (type is IArrayTypeSymbol)
            {
                return "array";
            }

            string name = SymbolExtensions.MetadataName(type);
            return name switch
            {
                "System.Collections.Generic.List`1" or "System.Collections.Generic.Dictionary`2" or "System.Collections.Generic.HashSet`1"
                    or "System.Collections.Generic.Queue`1" or "System.Collections.Generic.Stack`1" or "System.Collections.Generic.SortedDictionary`2"
                    or "System.Collections.Generic.SortedSet`1" or "System.Collections.Generic.LinkedList`1"
                    or "System.Collections.Generic.IList`1" or "System.Collections.Generic.ICollection`1" or "System.Collections.Generic.IDictionary`2"
                    or "System.Collections.Generic.ISet`1" => type.Name,
                _ when name.StartsWith("System.Collections.Concurrent.", StringComparison.Ordinal) => type.Name,
                _ => null,
            };
        }

        // SF0019: a Decide (or command handler) whose events or rejection codes static analysis can't list.
        private static void AnalyzeOutcome(SymbolAnalysisContext context, IMethodSymbol method, string name, ITypeSymbol command, Location fallback)
        {
            if (method.DeclaringSyntaxReferences.Length == 0)
            {
                return;
            }

            DecideOutcome outcome = ManifestAnalysis.Decide(method, context.Compilation, context.CancellationToken);
            if (outcome.Unresolved is { } reason)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.UndeclaredOutcome, method.Locations.FirstOrDefault() ?? fallback, name, command.Name, reason));
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
            // The target aggregate's Decide, declared in this compilation. An aggregate from a referenced assembly is
            // another service's whose contracts this one uses; whether its module is registered here is checked at startup.
            if (command.FindInterface(KnownNames.CommandOf)?.TypeArguments[0] is INamedTypeSymbol aggregate
                && SymbolEqualityComparer.Default.Equals(aggregate.ContainingAssembly, compilation.Assembly)
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
            bool hook = m.IsStatic && m.ContainingType.Implements("SourceFlow.Workflows.IWorkflowHooks");   // workflow $hook methods (doc 15 §5.2)
            method = m.Name;
            return aggregate || process || hook;
        }
    }
}

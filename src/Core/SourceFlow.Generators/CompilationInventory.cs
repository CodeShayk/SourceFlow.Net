using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SourceFlow.Generators
{
    /// <summary>Compilation-wide facts for SF0006 and SF0010, collected per type and checked at compilation end.</summary>
    internal sealed class CompilationInventory
    {
        private const string JsonSerializableAttribute = "System.Text.Json.Serialization.JsonSerializableAttribute";

        private readonly ConcurrentBag<INamedTypeSymbol> _messages = new();
        private readonly ConcurrentBag<INamedTypeSymbol> _modules = new();
        private readonly ConcurrentBag<INamedTypeSymbol> _stateTypes = new();
        private readonly ConcurrentBag<(INamedTypeSymbol Reply, INamedTypeSymbol Process)> _replies = new();
        private readonly ConcurrentBag<(ITypeSymbol Command, ITypeSymbol Aggregate)> _handled = new();
        private readonly ConcurrentBag<(INamedTypeSymbol Command, ITypeSymbol Aggregate)> _commands = new();

        public void Collect(INamedTypeSymbol type)
        {
            if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
            {
                return;
            }

            if (type.FindAttribute(KnownNames.MessageTypeAttribute) is not null && !type.IsAbstract)
            {
                _messages.Add(type);
                if (type.FindInterface(KnownNames.CommandOf) is { } commandOf)
                {
                    _commands.Add((type, commandOf.TypeArguments[0]));
                }
            }

            if (type.FindAttribute(KnownNames.ModuleAttribute) is not null)
            {
                _modules.Add(type);
            }

            if (type.FindAttribute(KnownNames.ProcessManagerAttribute) is not null && type.FindInterface("SourceFlow.Workflows.IWorkflowState`1") is null)
            {
                // Generated DSL workflows bring their own JSON metadata (WorkflowRuntime.StateResolver).
                _stateTypes.Add(type);
                foreach (IMethodSymbol react in type.GetMembers("React").OfType<IMethodSymbol>())
                {
                    if (react.Parameters.Length == 2 && react.Parameters[0].Type is INamedTypeSymbol { IsGenericType: true } reply && reply.Is(KnownNames.CommandRejected))
                    {
                        _replies.Add((reply, type));
                    }
                }
            }

            if (type.FindAttribute(KnownNames.AggregateAttribute) is { } aggregate)
            {
                if (aggregate.Named("SnapshotEvery", 0) > 0)
                {
                    _stateTypes.Add(type);
                }

                foreach (IMethodSymbol decide in type.GetMembers("Decide").OfType<IMethodSymbol>())
                {
                    if (!decide.IsStatic && decide.Parameters.Length == 2)
                    {
                        _handled.Add((decide.Parameters[0].Type, type));
                    }
                }
            }

            if (type.FindAttribute(KnownNames.ProjectionAttribute) is not null
                && type.FindInterface(KnownNames.DocumentProjection) is { } projection
                && projection.TypeArguments[0] is INamedTypeSymbol document)
            {
                _stateTypes.Add(document);
            }

            if (!type.IsAbstract)
            {
                foreach (INamedTypeSymbol contract in type.AllInterfaces)
                {
                    if (SymbolExtensions.MetadataName(contract) == KnownNames.CommandHandler)
                    {
                        _handled.Add((contract.TypeArguments[0], contract.TypeArguments[1]));
                    }
                }
            }
        }

        public void Report(CompilationAnalysisContext context)
        {
            ReportOwners(context);
            ReportJsonContexts(context);
        }

        private void ReportOwners(CompilationAnalysisContext context)
        {
            var handled = new HashSet<(ITypeSymbol, ITypeSymbol)>(_handled.Select(h => (h.Command, h.Aggregate)), PairComparer.Instance);
            foreach ((INamedTypeSymbol command, ITypeSymbol aggregate) in _commands)
            {
                bool aggregateInSource = aggregate.FindAttribute(KnownNames.AggregateAttribute) is not null && aggregate.Locations.Any(l => l.IsInSource);
                if (aggregateInSource && !handled.Contains((command, aggregate)))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.NoOwner, command.Locations.FirstOrDefault() ?? Location.None, command.Name, aggregate.Name));
                }
            }
        }

        private void ReportJsonContexts(CompilationAnalysisContext context)
        {
            foreach (INamedTypeSymbol module in _modules)
            {
                AttributeData attribute = module.FindAttribute(KnownNames.ModuleAttribute)!;
                if (attribute.ConstructorArguments.Length == 0 || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol jsonContext)
                {
                    continue;
                }

                var listed = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
                foreach (AttributeData serializable in jsonContext.GetAttributes())
                {
                    if (serializable.AttributeClass?.ToDisplayString() == JsonSerializableAttribute
                        && serializable.ConstructorArguments.Length > 0 && serializable.ConstructorArguments[0].Value is ITypeSymbol listedType)
                    {
                        listed.Add(listedType);
                    }
                }

                foreach ((INamedTypeSymbol reply, INamedTypeSymbol process) in _replies)
                {
                    if (!listed.Contains(reply))
                    {
                        var properties = ImmutableDictionary<string, string?>.Empty
                            .Add(JsonContextCodeFix.ContextKey, SymbolExtensions.MetadataName(jsonContext))
                            .Add(JsonContextCodeFix.TypeKey, reply.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.MissingJsonType, process.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None, properties,
                            reply.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), jsonContext.Name));
                    }
                }

                foreach (INamedTypeSymbol required in _messages.Concat(_stateTypes).Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default))
                {
                    if (!listed.Contains(required) && required.Locations.Any(l => l.IsInSource))
                    {
                        // The code fix reads the context and the type from the properties.
                        var properties = ImmutableDictionary<string, string?>.Empty
                            .Add(JsonContextCodeFix.ContextKey, SymbolExtensions.MetadataName(jsonContext))
                            .Add(JsonContextCodeFix.TypeKey, required.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.MissingJsonType, required.Locations.First(l => l.IsInSource), properties, required.Name, jsonContext.Name));
                    }
                }
            }
        }

        private sealed class PairComparer : IEqualityComparer<(ITypeSymbol, ITypeSymbol)>
        {
            public static readonly PairComparer Instance = new();

            public bool Equals((ITypeSymbol, ITypeSymbol) x, (ITypeSymbol, ITypeSymbol) y)
            {
                return SymbolEqualityComparer.Default.Equals(x.Item1, y.Item1) && SymbolEqualityComparer.Default.Equals(x.Item2, y.Item2);
            }

            public int GetHashCode((ITypeSymbol, ITypeSymbol) obj)
            {
                return (SymbolEqualityComparer.Default.GetHashCode(obj.Item1) * 31) + SymbolEqualityComparer.Default.GetHashCode(obj.Item2);
            }
        }
    }
}

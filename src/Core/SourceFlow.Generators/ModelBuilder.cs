using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace SourceFlow.Generators
{
    /// <summary>Turns symbols into value-equatable models. Invalid shapes return null; analyzers report them.</summary>
    internal static class ModelBuilder
    {
        public static IdModel? Id(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
        {
            if (context.TargetSymbol is not INamedTypeSymbol type || type.IsNested() || !type.IsValueType || !type.IsPartial())
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            IPropertySymbol? value = type.GetMembers("Value").OfType<IPropertySymbol>().FirstOrDefault(p => !p.IsStatic);
            if (value is null)
            {
                return null;
            }

            IdBackingKind backing = value.Type.SpecialType switch
            {
                SpecialType.System_String => IdBackingKind.String,
                SpecialType.System_Int64 => IdBackingKind.Int64,
                _ => value.Type.Is("System.Guid") ? IdBackingKind.Guid : (IdBackingKind)(-1),
            };
            if ((int)backing < 0)
            {
                return null;
            }

            AttributeData attribute = context.Attributes[0];
            int maxLength = attribute.Named("MaxLength", 64);
            return new IdModel(type.Namespace(), type.Name, type.FullName(), type.Keyword(), backing, maxLength);
        }

        public static MessageModel? Message(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
        {
            if (context.TargetSymbol is not INamedTypeSymbol type || type.IsAbstract || type.IsGenericType)
            {
                return null;
            }

            AttributeData attribute = context.Attributes[0];
            if (attribute.ConstructorArguments.Length == 0 || attribute.ConstructorArguments[0].Value is not string name)
            {
                return null;
            }

            int version = attribute.ConstructorArguments.Length > 1 && attribute.ConstructorArguments[1].Value is int v ? v : 1;
            string? kind = KindOf(type);
            if (kind is null)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            string? target = type.FindInterface(KnownNames.CommandOf) is { } commandOf ? commandOf.TypeArguments[0].FullName() : null;
            return new MessageModel(type.FullName(), name, version, kind, target, Members(type).ToEquatableArray());
        }

        public static string? KindOf(ITypeSymbol type)
        {
            if (type.Implements(KnownNames.Command))
            {
                return "Command";
            }

            if (type.Implements(KnownNames.Event))
            {
                return "Event";
            }

            if (type.Implements(KnownNames.IntegrationEvent))
            {
                return "IntegrationEvent";
            }

            return type.Implements(KnownNames.Timeout) ? "Timeout" : null;
        }

        private static IEnumerable<MemberModel> Members(INamedTypeSymbol type)
        {
            IMethodSymbol? primary = type.InstanceConstructors
                .Where(c => c.Parameters.Length > 0)
                .OrderByDescending(c => c.Parameters.Length)
                .FirstOrDefault(c => c.Parameters.All(p => type.GetMembers(p.Name).OfType<IPropertySymbol>().Any()));

            foreach (IPropertySymbol property in type.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public || property.Name == "EqualityContract")
                {
                    continue;
                }

                bool marked = property.FindAttribute(KnownNames.AggregateIdAttribute) is not null
                    || (primary?.Parameters.FirstOrDefault(p => p.Name == property.Name)?.FindAttribute(KnownNames.AggregateIdAttribute) is not null);
                yield return new MemberModel(property.Name, property.Type.FullName(), marked);
            }
        }

        public static AggregateModel? Aggregate(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
        {
            if (context.TargetSymbol is not INamedTypeSymbol type || type.IsNested() || !type.IsRecord || type.IsValueType || !type.IsPartial())
            {
                return null;
            }

            INamedTypeSymbol? contract = type.FindInterface(KnownNames.Aggregate);
            AttributeData attribute = context.Attributes[0];
            if (contract is null || attribute.ConstructorArguments.Length == 0 || attribute.ConstructorArguments[0].Value is not string name)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var decided = new List<string>();
            var evolved = new List<string>();
            foreach (IMethodSymbol method in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (method.IsStatic || method.Parameters.Length == 0)
                {
                    continue;
                }

                if (method.Name == "Decide" && method.Parameters.Length == 2
                    && method.ReturnType.Is(KnownNames.Decision)
                    && method.Parameters[1].Type.Is(KnownNames.DecisionContext)
                    && method.Parameters[0].Type.Implements(KnownNames.Command))
                {
                    decided.Add(method.Parameters[0].Type.FullName());
                }
                else if (method.Name == "When" && method.Parameters.Length == 1
                    && SymbolEqualityComparer.Default.Equals(method.ReturnType, type)
                    && method.Parameters[0].Type.Implements(KnownNames.Event))
                {
                    evolved.Add(method.Parameters[0].Type.FullName());
                }
            }

            return new AggregateModel(
                type.Namespace(),
                type.Name,
                type.FullName(),
                type.Keyword(),
                name,
                attribute.Named("SnapshotEvery", 0),
                contract.TypeArguments[1].FullName(),
                decided.Distinct().OrderBy(x => x, System.StringComparer.Ordinal).ToEquatableArray(),
                evolved.Distinct().OrderBy(x => x, System.StringComparer.Ordinal).ToEquatableArray());
        }

        public static ProcessManagerModel? ProcessManager(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
        {
            if (context.TargetSymbol is not INamedTypeSymbol type || type.IsNested() || !type.IsRecord || type.IsValueType || !type.IsPartial())
            {
                return null;
            }

            INamedTypeSymbol? contract = type.FindInterface(KnownNames.ProcessManager);
            AttributeData attribute = context.Attributes[0];
            if (contract is null || attribute.ConstructorArguments.Length == 0 || attribute.ConstructorArguments[0].Value is not string name)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            ITypeSymbol idType = contract.TypeArguments[1];
            var reacted = new List<string>();
            var correlated = new List<string>();
            foreach (IMethodSymbol method in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (!method.IsStatic && method.Name == "React" && method.Parameters.Length == 2
                    && method.ReturnType.Is(KnownNames.Reaction)
                    && method.Parameters[1].Type.Is(KnownNames.ReactionContext)
                    && method.Parameters[0].Type.Implements(KnownNames.Message)
                    && !method.Parameters[0].Type.Is(KnownNames.Message))
                {
                    reacted.Add(method.Parameters[0].Type.FullName());
                }
                else if (method.IsStatic && method.Name == "CorrelateBy" && method.Parameters.Length == 1
                    && SymbolEqualityComparer.Default.Equals(method.ReturnType, idType))
                {
                    correlated.Add(method.Parameters[0].Type.FullName());
                }
            }

            return new ProcessManagerModel(
                type.Namespace(),
                type.Name,
                type.FullName(),
                type.Keyword(),
                name,
                idType.FullName(),
                reacted.Distinct().OrderBy(x => x, System.StringComparer.Ordinal).ToEquatableArray(),
                correlated.Distinct().OrderBy(x => x, System.StringComparer.Ordinal).ToEquatableArray());
        }

        public static ProjectionModel? Projection(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
        {
            if (context.TargetSymbol is not INamedTypeSymbol type || type.IsNested() || type.IsValueType || type.IsAbstract || !type.IsPartial())
            {
                return null;
            }

            INamedTypeSymbol? contract = type.FindInterface(KnownNames.DocumentProjection);
            AttributeData attribute = context.Attributes[0];
            if (contract is null || attribute.ConstructorArguments.Length == 0 || attribute.ConstructorArguments[0].Value is not string name)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            ITypeSymbol document = contract.TypeArguments[0];
            ITypeSymbol key = contract.TypeArguments[1];
            KeyKind keyKind = key.SpecialType == SpecialType.System_String
                ? KeyKind.String
                : IsStronglyTypedId(key) ? KeyKind.StronglyTypedId : KeyKind.Other;

            var keyOf = new HashSet<string>();
            foreach (IMethodSymbol method in type.GetMembers("KeyOf").OfType<IMethodSymbol>())
            {
                if (method.IsStatic && method.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(method.ReturnType, key))
                {
                    keyOf.Add(method.Parameters[0].Type.FullName());
                }
            }

            var events = new List<ProjectedEvent>();
            foreach (IMethodSymbol method in type.GetMembers("When").OfType<IMethodSymbol>())
            {
                if (method.Parameters.Length != 3
                    || !method.Parameters[2].Type.Is(KnownNames.EventContext)
                    || !SymbolEqualityComparer.Default.Equals(method.Parameters[1].Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated), document.WithNullableAnnotation(NullableAnnotation.NotAnnotated))
                    || !SymbolEqualityComparer.Default.Equals(method.ReturnType.WithNullableAnnotation(NullableAnnotation.NotAnnotated), document.WithNullableAnnotation(NullableAnnotation.NotAnnotated))
                    || !method.Parameters[0].Type.Implements(KnownNames.Event))
                {
                    continue;
                }

                ITypeSymbol eventType = method.Parameters[0].Type;
                string eventName = eventType.FullName();
                if (keyOf.Contains(eventName))
                {
                    events.Add(new ProjectedEvent(eventName, KeySource.KeyOf, null));
                    continue;
                }

                IPropertySymbol[] keyMembers = eventType.GetMembers().OfType<IPropertySymbol>()
                    .Where(p => !p.IsStatic && p.DeclaredAccessibility == Accessibility.Public
                        && SymbolEqualityComparer.Default.Equals(p.Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated), key.WithNullableAnnotation(NullableAnnotation.NotAnnotated)))
                    .ToArray();
                events.Add(keyMembers.Length == 1
                    ? new ProjectedEvent(eventName, KeySource.Member, keyMembers[0].Name)
                    : new ProjectedEvent(eventName, KeySource.Fallback, null));
            }

            return new ProjectionModel(
                type.Namespace(),
                type.Name,
                type.FullName(),
                type.Keyword(),
                name,
                attribute.Named("Version", 1),
                document.FullName(),
                key.FullName(),
                keyKind,
                events.OrderBy(e => e.EventType, System.StringComparer.Ordinal).ToEquatableArray());
        }

        /// <summary>
        /// True for ids that implement IStronglyTypedId or carry [StronglyTypedId]. The attribute check matters because the
        /// interface is added by this generator's own output, which the input compilation does not contain yet.
        /// </summary>
        public static bool IsStronglyTypedId(ITypeSymbol type)
        {
            return type.FindInterface(KnownNames.StronglyTypedId) is not null || type.FindAttribute(KnownNames.StronglyTypedIdAttribute) is not null;
        }

        public static IEnumerable<HandlerModel> Handlers(INamedTypeSymbol type)
        {
            if (type.IsAbstract || type.IsGenericType || type.IsNested() || type.TypeKind != TypeKind.Class)
            {
                yield break;
            }

            foreach (INamedTypeSymbol contract in type.AllInterfaces)
            {
                string name = SymbolExtensions.MetadataName(contract);
                if (name == KnownNames.CommandHandler)
                {
                    yield return new HandlerModel(HandlerKind.Command, type.FullName(), contract.TypeArguments[0].FullName(), contract.TypeArguments[1].FullName());
                }
                else if (name == KnownNames.EventHandler)
                {
                    yield return new HandlerModel(HandlerKind.Event, type.FullName(), contract.TypeArguments[0].FullName(), null);
                }
                else if (name == KnownNames.Upcaster && type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
                {
                    yield return new HandlerModel(HandlerKind.Upcaster, type.FullName(), contract.TypeArguments[0].FullName(), contract.TypeArguments[1].FullName());
                }
            }
        }

        public static ModuleModel? Module(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
        {
            if (context.TargetSymbol is not INamedTypeSymbol type || type.IsNested() || type.IsValueType || !type.IsPartial())
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            AttributeData attribute = context.Attributes[0];
            string? json = attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is INamedTypeSymbol ctx
                ? ctx.FullName()
                : null;
            return new ModuleModel(type.Namespace(), type.Name, type.FullName(), json);
        }
    }
}

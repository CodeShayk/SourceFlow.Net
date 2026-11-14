using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace SourceFlow.Generators
{
    // The static part of the service manifest (doc 15 §3, WP-S.1): emitted as [assembly: SourceFlowManifest(json)].
    // Routes, publications, subscriptions and replyOnReject are configuration and are merged by
    // `dotnet sourceflow manifest export` (WP-S.2).

    internal sealed record ManifestField(string Name, string Type, bool Nullable, bool PersonalData, EquatableArray<ManifestField> Fields);

    internal sealed record ManifestMessage(string Ref, string Name, int Version, string Kind, string ClrType, string? Aggregate, EquatableArray<ManifestField> Fields);

    internal sealed record ManifestCommand(string Ref, string Name, int Version, EquatableArray<string> Emits, EquatableArray<string> Rejects);

    internal sealed record ManifestAggregate(string Name, string ClrType, EquatableArray<ManifestCommand> Commands, EquatableArray<string> Events);

    internal sealed record ManifestHandler(string AggregateName, string AggregateClrType, ManifestCommand Command);

    internal sealed record ManifestProcess(string Name, string ClrType, EquatableArray<string> Handles, EquatableArray<string> Sends, EquatableArray<string> Schedules);

    internal sealed record ManifestProjection(string Name, int Version, string ClrType, EquatableArray<string> Handles);

    internal sealed record ManifestAssembly(string Name, string SourceFlowVersion, bool CanEmit);

    internal static class ManifestBuilder
    {
        public const string ManifestAttribute = "SourceFlow.SourceFlowManifestAttribute";

        public static ManifestAssembly Assembly(Compilation compilation, CancellationToken cancellationToken)
        {
            AssemblyIdentity? core = compilation.ReferencedAssemblyNames.FirstOrDefault(a => a.Name == "SourceFlow.Net");
            string version = core is null ? string.Empty : core.Version.ToString(3);
            bool canEmit = compilation.GetTypeByMetadataName(ManifestAttribute) is not null;
            return new ManifestAssembly(compilation.AssemblyName ?? string.Empty, version, canEmit);
        }

        public static ManifestMessage? Message(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
        {
            if (context.TargetSymbol is not INamedTypeSymbol type || type.IsAbstract || type.IsGenericType
                || ModelBuilder.KindOf(type) is not { } kind || NameVersion(type) is not { } id)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            string? aggregate = type.FindInterface(KnownNames.CommandOf)?.TypeArguments[0].FindAttribute(KnownNames.AggregateAttribute) is { ConstructorArguments.Length: > 0 } a
                ? a.ConstructorArguments[0].Value as string
                : null;
            string manifestKind = kind switch
            {
                "Command" => "command",
                "Event" => "event",
                "IntegrationEvent" => "integration-event",
                _ => "timeout",
            };
            return new ManifestMessage(Ref(id), id.Name, id.Version, manifestKind, Clr(type), aggregate, Fields(type).ToEquatableArray());
        }

        public static ManifestAggregate? Aggregate(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
        {
            if (context.TargetSymbol is not INamedTypeSymbol type || type.FindInterface(KnownNames.Aggregate) is null
                || context.Attributes[0].ConstructorArguments is not { Length: > 0 } args || args[0].Value is not string name)
            {
                return null;
            }

            Compilation compilation = context.SemanticModel.Compilation;
            var commands = new List<ManifestCommand>();
            var events = new SortedSet<string>(StringComparer.Ordinal);
            foreach (IMethodSymbol method in type.GetMembers().OfType<IMethodSymbol>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (method.IsStatic || method.Parameters.Length == 0)
                {
                    continue;
                }

                if (method.Name == "Decide" && method.Parameters.Length == 2 && method.ReturnType.Is(KnownNames.Decision)
                    && method.Parameters[1].Type.Is(KnownNames.DecisionContext)
                    && method.Parameters[0].Type.Implements(KnownNames.Command)
                    && Command(method.Parameters[0].Type, ManifestAnalysis.Decide(method, compilation, cancellationToken)) is { } command)
                {
                    commands.Add(command);
                }
                else if (method.Name == "When" && method.Parameters.Length == 1 && method.Parameters[0].Type.Implements(KnownNames.Event)
                    && ManifestAnalysis.Ref(method.Parameters[0].Type) is { } evolved)
                {
                    events.Add(evolved);
                }
            }

            return new ManifestAggregate(
                name,
                Clr(type),
                commands.GroupBy(c => c.Ref).Select(g => g.First()).OrderBy(c => c.Ref, StringComparer.Ordinal).ToEquatableArray(),
                events.ToEquatableArray());
        }

        public static IEnumerable<ManifestHandler> Handlers(INamedTypeSymbol type, Compilation compilation, CancellationToken cancellationToken)
        {
            if (type.IsAbstract)
            {
                yield break;
            }

            foreach (INamedTypeSymbol contract in type.AllInterfaces)
            {
                if (SymbolExtensions.MetadataName(contract) != KnownNames.CommandHandler
                    || contract.TypeArguments[1] is not INamedTypeSymbol aggregate
                    || aggregate.FindAttribute(KnownNames.AggregateAttribute) is not { ConstructorArguments.Length: > 0 } attribute
                    || attribute.ConstructorArguments[0].Value is not string aggregateName)
                {
                    continue;
                }

                ITypeSymbol commandType = contract.TypeArguments[0];
                IMethodSymbol? handle = type.GetMembers("HandleAsync").OfType<IMethodSymbol>()
                    .FirstOrDefault(m => m.Parameters.Length >= 1 && SymbolEqualityComparer.Default.Equals(m.Parameters[0].Type, commandType));
                if (handle is not null && Command(commandType, ManifestAnalysis.Decide(handle, compilation, cancellationToken)) is { } command)
                {
                    yield return new ManifestHandler(aggregateName, Clr(aggregate), command);
                }
            }
        }

        public static ManifestProcess? Process(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
        {
            if (context.TargetSymbol is not INamedTypeSymbol type || type.FindInterface(KnownNames.ProcessManager) is null
                || context.Attributes[0].ConstructorArguments is not { Length: > 0 } args || args[0].Value is not string name)
            {
                return null;
            }

            var handles = new SortedSet<string>(StringComparer.Ordinal);
            foreach (IMethodSymbol method in type.GetMembers("React").OfType<IMethodSymbol>())
            {
                if (method.IsStatic || method.Parameters.Length != 2)
                {
                    continue;
                }

                ITypeSymbol message = method.Parameters[0].Type;
                if (message is INamedTypeSymbol { IsGenericType: true } reply && reply.Is(KnownNames.CommandRejected)
                    && NameVersion(reply.TypeArguments[0]) is { } rejected)
                {
                    handles.Add(rejected.Name + ".rejected.v" + rejected.Version.ToString(CultureInfo.InvariantCulture));
                }
                else if (ManifestAnalysis.Ref(message) is { } handled)
                {
                    handles.Add(handled);
                }
            }

            ReactOutcome outcome = ManifestAnalysis.React(type, context.SemanticModel.Compilation, cancellationToken);
            return new ManifestProcess(name, Clr(type), handles.ToEquatableArray(), outcome.Sends, outcome.Schedules);
        }

        public static ManifestProjection? Projection(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
        {
            if (context.TargetSymbol is not INamedTypeSymbol type
                || context.Attributes[0].ConstructorArguments is not { Length: > 0 } args || args[0].Value is not string name)
            {
                return null;
            }

            var handles = new SortedSet<string>(StringComparer.Ordinal);
            foreach (IMethodSymbol method in type.GetMembers("When").OfType<IMethodSymbol>())
            {
                if (!method.IsStatic && method.Parameters.Length >= 1 && method.Parameters[0].Type.Implements(KnownNames.Event)
                    && ManifestAnalysis.Ref(method.Parameters[0].Type) is { } handled)
                {
                    handles.Add(handled);
                }
            }

            return new ManifestProjection(name, context.Attributes[0].Named("Version", 1), Clr(type), handles.ToEquatableArray());
        }

        private static ManifestCommand? Command(ITypeSymbol command, DecideOutcome outcome)
        {
            return NameVersion(command) is { } id ? new ManifestCommand(Ref(id), id.Name, id.Version, outcome.Emits, outcome.Rejects) : null;
        }

        private static (string Name, int Version)? NameVersion(ITypeSymbol type)
        {
            if (type.FindAttribute(KnownNames.MessageTypeAttribute) is not { ConstructorArguments.Length: > 0 } attribute
                || attribute.ConstructorArguments[0].Value is not string name)
            {
                return null;
            }

            return (name, attribute.ConstructorArguments.Length > 1 && attribute.ConstructorArguments[1].Value is int v ? v : 1);
        }

        private static string Ref((string Name, int Version) id)
        {
            return id.Name + ".v" + id.Version.ToString(CultureInfo.InvariantCulture);
        }

        private static string Clr(ITypeSymbol type)
        {
            return type.FullName().Replace("global::", string.Empty);
        }

        // Public instance properties in declaration order, with their JSON names (camelCase unless [JsonPropertyName]).
        // An object member of the service's own types lists its members too, so a nested `correlate` path can be checked
        // (CR-35); NestedType has the limits.
        private static IEnumerable<ManifestField> Fields(INamedTypeSymbol type, ImmutableStack<INamedTypeSymbol>? outer = null)
        {
            outer = (outer ?? ImmutableStack<INamedTypeSymbol>.Empty).Push(type);
            foreach (IPropertySymbol property in type.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public || property.Name == "EqualityContract")
                {
                    continue;
                }

                string name = property.FindAttribute("System.Text.Json.Serialization.JsonPropertyNameAttribute") is { ConstructorArguments.Length: 1 } renamed
                    && renamed.ConstructorArguments[0].Value is string explicitName
                        ? explicitName
                        : CamelCase(property.Name);
                bool personal = property.FindMemberAttribute(KnownNames.PersonalDataAttribute) is not null;
                string kind = FieldType(property.Type, out bool nullable);
                EquatableArray<ManifestField> nested = kind == "object" && NestedType(property.Type, outer) is { } inner
                    ? Fields(inner, outer).ToEquatableArray()
                    : EquatableArray<ManifestField>.Empty;
                yield return new ManifestField(name, kind, nullable, personal, nested);
            }
        }

        /// <summary>
        /// The type whose members an object member lists: a non-generic class, record or struct outside <c>System</c>, not
        /// abstract, at most three levels down and not one of the types it is nested in (no cycles).
        /// </summary>
        internal static INamedTypeSymbol? NestedType(ITypeSymbol type, ImmutableStack<INamedTypeSymbol> outer)
        {
            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } wrapped)
            {
                type = wrapped.TypeArguments[0];
            }

            if (type is not INamedTypeSymbol named || named.IsAbstract || named.IsGenericType
                || named.TypeKind is not (TypeKind.Class or TypeKind.Struct)
                || named.SpecialType != SpecialType.None
                || named.FindAttribute(KnownNames.StronglyTypedIdAttribute) is not null
                || named.ContainingNamespace?.ToDisplayString() is not { } ns || ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal))
            {
                return null;
            }

            int depth = 0;
            foreach (INamedTypeSymbol o in outer)
            {
                if (SymbolEqualityComparer.Default.Equals(o, named))
                {
                    return null;
                }

                depth++;
            }

            return depth > 3 ? null : named;
        }

        private static string FieldType(ITypeSymbol type, out bool nullable)
        {
            nullable = false;
            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } wrapped)
            {
                nullable = true;
                type = wrapped.TypeArguments[0];
            }
            else if (type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated)
            {
                nullable = true;
            }

            switch (type.SpecialType)
            {
                case SpecialType.System_String:
                case SpecialType.System_Char:
                    return "string";
                case SpecialType.System_Boolean:
                    return "boolean";
                case SpecialType.System_Byte:
                case SpecialType.System_SByte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                    return "integer";
                case SpecialType.System_Decimal:
                case SpecialType.System_Double:
                case SpecialType.System_Single:
                    return "number";
                case SpecialType.System_DateTime:
                    return "date-time";
            }

            if (type.TypeKind == TypeKind.Enum)
            {
                return "integer";
            }

            switch (SymbolExtensions.MetadataName(type))
            {
                case "System.DateTimeOffset": return "date-time";
                case "System.DateOnly": return "date";
                case "System.TimeOnly": return "time";
                case "System.TimeSpan": return "duration";
                case "System.Guid": return "uuid";
                case "System.Uri": return "uri";
            }

            if (type.FindAttribute(KnownNames.StronglyTypedIdAttribute) is not null
                && type.GetMembers("Value").OfType<IPropertySymbol>().FirstOrDefault(p => !p.IsStatic) is { } value)
            {
                return FieldType(value.Type, out _);
            }

            if (type is IArrayTypeSymbol || type.AllInterfaces.Any(i => i.Is("System.Collections.IEnumerable")))
            {
                return "array";
            }

            return "object";
        }

        // System.Text.Json's camel-case policy, so the names match the wire.
        internal static string CamelCase(string name)
        {
            if (name.Length == 0 || !char.IsUpper(name[0]))
            {
                return name;
            }

            char[] chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (i == 1 && !char.IsUpper(chars[i]))
                {
                    break;
                }

                bool hasNext = i + 1 < chars.Length;
                if (i > 0 && hasNext && !char.IsUpper(chars[i + 1]))
                {
                    if (char.IsSeparator(chars[i + 1]))
                    {
                        chars[i] = char.ToLowerInvariant(chars[i]);
                    }

                    break;
                }

                chars[i] = char.ToLowerInvariant(chars[i]);
            }

            return new string(chars);
        }
    }

    internal static class ManifestEmitter
    {
        private static void Field(StringBuilder fb, ManifestField f)
        {
            fb.Append("{\"name\":").Append(Str(f.Name)).Append(",\"type\":").Append(Str(f.Type));
            if (f.Nullable)
            {
                fb.Append(",\"nullable\":true");
            }

            if (f.PersonalData)
            {
                fb.Append(",\"personalData\":true");
            }

            if (f.Fields.Count > 0)
            {
                fb.Append(",\"fields\":[");
                Join(fb, f.Fields, Field);
                fb.Append(']');
            }

            fb.Append('}');
        }

        public static bool HasContent(ImmutableArray<ManifestMessage> messages, ImmutableArray<ManifestAggregate> aggregates, ImmutableArray<ManifestHandler> handlers, ImmutableArray<ManifestProcess> processes, ImmutableArray<ManifestProjection> projections)
        {
            return messages.Length + aggregates.Length + handlers.Length + processes.Length + projections.Length > 0;
        }

        public static string Json(
            ManifestAssembly assembly,
            ImmutableArray<ManifestMessage> messages,
            ImmutableArray<ManifestAggregate> aggregates,
            ImmutableArray<ManifestHandler> handlers,
            ImmutableArray<ManifestProcess> processes,
            ImmutableArray<ManifestProjection> projections,
            ImmutableArray<WorkflowManifestEntry> workflows = default)
        {
            var json = new StringBuilder();
            json.Append("{\"manifestVersion\":1,\"assembly\":").Append(Str(assembly.Name))
                .Append(",\"sourceflowVersion\":").Append(Str(assembly.SourceFlowVersion));

            json.Append(",\"messages\":[");
            Join(json, messages.GroupBy(m => m.Ref).Select(g => g.OrderBy(m => m.ClrType, StringComparer.Ordinal).First()).OrderBy(m => m.Ref, StringComparer.Ordinal), (b, m) =>
            {
                b.Append("{\"type\":").Append(Str(m.Name)).Append(",\"version\":").Append(m.Version.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"kind\":").Append(Str(m.Kind)).Append(",\"clrType\":").Append(Str(m.ClrType));
                if (m.Aggregate is not null)
                {
                    b.Append(",\"aggregate\":").Append(Str(m.Aggregate));
                }

                b.Append(",\"fields\":[");
                Join(b, m.Fields, Field);
                b.Append("]}");
            });

            // Commands owned through an ICommandHandler join their aggregate's entry (or one of their own).
            var merged = aggregates.GroupBy(a => a.ClrType).Select(g => g.First())
                .ToDictionary(a => a.ClrType, a => (a.Name, Commands: a.Commands.ToList(), Events: a.Events.ToList()), StringComparer.Ordinal);
            foreach (ManifestHandler handler in handlers)
            {
                if (!merged.TryGetValue(handler.AggregateClrType, out var entry))
                {
                    entry = (handler.AggregateName, new List<ManifestCommand>(), new List<string>());
                    merged[handler.AggregateClrType] = entry;
                }

                if (!entry.Commands.Any(c => c.Ref == handler.Command.Ref))
                {
                    entry.Commands.Add(handler.Command);
                }
            }

            json.Append("],\"aggregates\":[");
            Join(json, merged.OrderBy(p => p.Value.Name, StringComparer.Ordinal).ThenBy(p => p.Key, StringComparer.Ordinal), (b, pair) =>
            {
                b.Append("{\"name\":").Append(Str(pair.Value.Name)).Append(",\"clrType\":").Append(Str(pair.Key)).Append(",\"commands\":[");
                Join(b, pair.Value.Commands.OrderBy(c => c.Ref, StringComparer.Ordinal), (cb, c) =>
                {
                    cb.Append("{\"type\":").Append(Str(c.Name)).Append(",\"version\":").Append(c.Version.ToString(CultureInfo.InvariantCulture))
                        .Append(",\"emits\":");
                    Strings(cb, c.Emits);
                    cb.Append(",\"rejects\":");
                    Strings(cb, c.Rejects);
                    cb.Append('}');
                });
                b.Append("],\"events\":");
                Strings(b, pair.Value.Events);
                b.Append('}');
            });

            json.Append("],\"processManagers\":[");
            Join(json, processes.OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.ClrType, StringComparer.Ordinal), (b, p) =>
            {
                b.Append("{\"name\":").Append(Str(p.Name)).Append(",\"clrType\":").Append(Str(p.ClrType)).Append(",\"handles\":");
                Strings(b, p.Handles);
                b.Append(",\"sends\":");
                Strings(b, p.Sends);
                b.Append(",\"schedules\":");
                Strings(b, p.Schedules);
                b.Append('}');
            });

            json.Append("],\"projections\":[");
            Join(json, projections.GroupBy(p => p.ClrType).Select(g => g.First()).OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.ClrType, StringComparer.Ordinal), (b, p) =>
            {
                b.Append("{\"name\":").Append(Str(p.Name)).Append(",\"version\":").Append(p.Version.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"clrType\":").Append(Str(p.ClrType)).Append(",\"handles\":");
                Strings(b, p.Handles);
                b.Append('}');
            });

            json.Append(']');
            if (!workflows.IsDefaultOrEmpty)
            {
                json.Append(",\"workflows\":[");
                Join(json, workflows.OrderBy(w => w.Name, StringComparer.Ordinal).ThenBy(w => w.Version), (b, w) =>
                    b.Append("{\"name\":").Append(Str(w.Name)).Append(",\"version\":").Append(w.Version.ToString(CultureInfo.InvariantCulture))
                        .Append(",\"file\":").Append(Str(w.File)).Append(",\"fingerprint\":").Append(Str(w.Fingerprint)).Append('}'));
                json.Append(']');
            }

            return json.Append('}').ToString();
        }

        public static string Source(string json)
        {
            return new SourceWriter()
                .Line("[assembly: global::SourceFlow.SourceFlowManifestAttribute(" + Literal.Of(json) + ")]")
                .ToString();
        }

        private static void Strings(StringBuilder builder, IEnumerable<string> values)
        {
            builder.Append('[');
            Join(builder, values.Distinct().OrderBy(v => v, StringComparer.Ordinal), (b, v) => b.Append(Str(v)));
            builder.Append(']');
        }

        private static void Join<T>(StringBuilder builder, IEnumerable<T> items, Action<StringBuilder, T> write)
        {
            bool first = true;
            foreach (T item in items)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                write(builder, item);
            }
        }

        private static string Str(string value)
        {
            var builder = new StringBuilder(value.Length + 2).Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            return builder.Append('"').ToString();
        }
    }
}

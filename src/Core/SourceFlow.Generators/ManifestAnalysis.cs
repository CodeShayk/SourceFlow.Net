using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace SourceFlow.Generators
{
    /// <summary>
    /// What a <c>Decide</c> (or a command handler's <c>HandleAsync</c>) can return, found by static analysis
    /// (doc 15 §3.2). <see cref="Unresolved"/> says what analysis couldn't decide and the author hasn't declared.
    /// </summary>
    internal sealed record DecideOutcome(EquatableArray<string> Emits, EquatableArray<string> Rejects, string? Unresolved);

    /// <summary>Commands sent and messages scheduled by a process manager's <c>React</c> methods.</summary>
    internal sealed record ReactOutcome(EquatableArray<string> Sends, EquatableArray<string> Schedules);

    /// <summary>
    /// Static analysis shared by the manifest emission and SF0019. Only the method's own body is read: events built
    /// in helpers, codes computed at run time and decisions returned by helpers are "unresolved" and need
    /// <c>[Emits]</c> / <c>[Rejects]</c>.
    /// </summary>
    internal static class ManifestAnalysis
    {
        private const string ErrorType = "SourceFlow.Error";
        private const string EmitsAttribute = "SourceFlow.EmitsAttribute";
        private const string RejectsAttribute = "SourceFlow.RejectsAttribute";
        private const int MaxDepth = 4;

        /// <summary>The CloudEvents-style reference of a message type, <c>{name}.v{version}</c>, or null without <c>[MessageType]</c>.</summary>
        public static string? Ref(ITypeSymbol? type)
        {
            if (type?.FindAttribute(KnownNames.MessageTypeAttribute) is not { ConstructorArguments.Length: > 0 } attribute
                || attribute.ConstructorArguments[0].Value is not string name)
            {
                return null;
            }

            int version = attribute.ConstructorArguments.Length > 1 && attribute.ConstructorArguments[1].Value is int v ? v : 1;
            return name + ".v" + version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public static DecideOutcome Decide(IMethodSymbol method, Compilation compilation, CancellationToken cancellationToken)
        {
            var emits = new SortedSet<string>(StringComparer.Ordinal);
            var rejects = new SortedSet<string>(StringComparer.Ordinal);
            bool unknownEvents = false, unknownCodes = false, opaque = false;

            foreach (IOperation operation in Bodies(method, compilation, cancellationToken))
            {
                foreach (IInvocationOperation invocation in operation.Descendants().OfType<IInvocationOperation>())
                {
                    IMethodSymbol target = invocation.TargetMethod;
                    if (target.ContainingType.Is(KnownNames.Decision) && target.Name == "Accept")
                    {
                        foreach (IArgumentOperation argument in invocation.Arguments)
                        {
                            unknownEvents |= !Events(argument.Value, emits);
                        }
                    }
                    else if (target.ContainingType.Is(KnownNames.Decision) && target.Name == "Reject")
                    {
                        unknownCodes |= invocation.Arguments.Length != 1 || !Codes(invocation.Arguments[0].Value, rejects, compilation, 0, cancellationToken);
                    }
                    else if (ReturnsDecision(target.ReturnType) && !IsFramework(target.ContainingType))
                    {
                        opaque = true;
                    }
                }
            }

            bool declaresEmits = false, declaresRejects = false;
            foreach (AttributeData attribute in method.GetAttributes())
            {
                if (attribute.AttributeClass.Is(EmitsAttribute) && attribute.ConstructorArguments.Length == 1)
                {
                    declaresEmits = true;
                    if (Ref(attribute.ConstructorArguments[0].Value as ITypeSymbol) is { } declared)
                    {
                        emits.Add(declared);
                    }
                }
                else if (attribute.AttributeClass.Is(RejectsAttribute) && attribute.ConstructorArguments.Length == 1)
                {
                    declaresRejects = true;
                    if (attribute.ConstructorArguments[0].Value is string code && code.Length > 0)
                    {
                        rejects.Add(code);
                    }
                }
            }

            string? unresolved = null;
            if (unknownEvents && !declaresEmits)
            {
                unresolved = "passes events to Decision.Accept that aren't created inline; declare them with [Emits(typeof(...))]";
            }
            else if (unknownCodes && !declaresRejects)
            {
                unresolved = "rejects with an error whose code isn't a constant; declare it with [Rejects(\"code\")]";
            }
            else if (opaque && !declaresEmits && !declaresRejects)
            {
                unresolved = "returns a decision built by a helper; declare its outcomes with [Emits(typeof(...))] and [Rejects(\"code\")]";
            }

            return new DecideOutcome(emits.ToEquatableArray(), rejects.ToEquatableArray(), unresolved);
        }

        public static ReactOutcome React(INamedTypeSymbol process, Compilation compilation, CancellationToken cancellationToken)
        {
            var sends = new SortedSet<string>(StringComparer.Ordinal);
            var schedules = new SortedSet<string>(StringComparer.Ordinal);
            foreach (IMethodSymbol method in process.GetMembers("React").OfType<IMethodSymbol>())
            {
                foreach (IOperation operation in Bodies(method, compilation, cancellationToken))
                {
                    foreach (IOperation descendant in operation.Descendants())
                    {
                        if (descendant is IObjectCreationOperation { Type: { } created } && created.Implements(KnownNames.Command) && Ref(created) is { } command)
                        {
                            sends.Add(command);
                        }
                        else if (descendant is IInvocationOperation { TargetMethod.Name: "Schedule" } schedule
                            && schedule.TargetMethod.ContainingType.Is(KnownNames.Reaction)
                            && schedule.Arguments.Length > 0
                            && Ref(Unwrap(schedule.Arguments[0].Value).Type) is { } scheduled)
                        {
                            schedules.Add(scheduled);
                        }
                    }
                }
            }

            return new ReactOutcome(sends.ToEquatableArray(), schedules.ToEquatableArray());
        }

        private static IEnumerable<IOperation> Bodies(IMethodSymbol method, Compilation compilation, CancellationToken cancellationToken)
        {
            foreach (SyntaxReference reference in method.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax(cancellationToken) is not MethodDeclarationSyntax declaration
                    || (declaration.Body is null && declaration.ExpressionBody is null))
                {
                    continue;
                }

                SemanticModel model = compilation.GetSemanticModel(reference.SyntaxTree);
                if (model.GetOperation(declaration, cancellationToken) is { } operation)
                {
                    yield return operation;
                }
            }
        }

        // Events created inline: new X(...), possibly behind conditionals, switch arms or a params collection.
        private static bool Events(IOperation value, ISet<string> emits)
        {
            switch (value)
            {
                case IConversionOperation conversion:
                    return Events(conversion.Operand, emits);
                case IObjectCreationOperation { Type: { } created }:
                    if (Ref(created) is { } reference)
                    {
                        emits.Add(reference);
                    }

                    return true;
                case IConditionalOperation conditional:
                    return Events(conditional.WhenTrue, emits) & (conditional.WhenFalse is null || Events(conditional.WhenFalse, emits));
                case ISwitchExpressionOperation @switch:
                    return @switch.Arms.Aggregate(true, (ok, arm) => Events(arm.Value, emits) & ok);
                case IArrayCreationOperation array:
                    return array.Initializer is { } initializer && initializer.ElementValues.Aggregate(true, (ok, element) => Events(element, emits) & ok);
                case ICollectionExpressionOperation collection:
                    return collection.Elements.Aggregate(true, (ok, element) => Events(element, emits) & ok);
                case ILiteralOperation or IDefaultValueOperation:
                    return true;
                default:
                    return false;
            }
        }

        // Error codes: new Error("code", ...), a static Error field or property initialised that way, Error built-ins.
        private static bool Codes(IOperation value, ISet<string> rejects, Compilation compilation, int depth, CancellationToken cancellationToken)
        {
            if (depth > MaxDepth)
            {
                return false;
            }

            switch (value)
            {
                case IConversionOperation conversion:
                    return Codes(conversion.Operand, rejects, compilation, depth, cancellationToken);
                case IWithOperation with:
                    return Codes(with.Operand, rejects, compilation, depth, cancellationToken);
                case IObjectCreationOperation creation when creation.Type.Is(ErrorType):
                    if (creation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0) is { Value.ConstantValue: { HasValue: true, Value: string code } } && code.Length > 0)
                    {
                        rejects.Add(code);
                        return true;
                    }

                    return false;
                case IInvocationOperation invocation when invocation.TargetMethod.ContainingType.Is(ErrorType) && invocation.TargetMethod.Name == "InvalidCommand":
                    rejects.Add("sourceflow.invalid_command");
                    return true;
                case IFieldReferenceOperation { Field: { IsStatic: true } field }:
                    return Member(field, rejects, compilation, depth, cancellationToken);
                case IPropertyReferenceOperation { Property: { IsStatic: true } property }:
                    return Member(property, rejects, compilation, depth, cancellationToken);
                case IConditionalOperation conditional:
                    return Codes(conditional.WhenTrue, rejects, compilation, depth, cancellationToken)
                        & (conditional.WhenFalse is null || Codes(conditional.WhenFalse, rejects, compilation, depth, cancellationToken));
                case ISwitchExpressionOperation @switch:
                    return @switch.Arms.Aggregate(true, (ok, arm) => Codes(arm.Value, rejects, compilation, depth, cancellationToken) & ok);
                default:
                    return false;
            }
        }

        private static bool Member(ISymbol member, ISet<string> rejects, Compilation compilation, int depth, CancellationToken cancellationToken)
        {
            if (member.ContainingType.Is(ErrorType))
            {
                switch (member.Name)
                {
                    case "VersionMismatch":
                        rejects.Add("sourceflow.version_mismatch");
                        return true;
                    case "ConcurrencyConflict":
                        rejects.Add("sourceflow.concurrency_conflict");
                        return true;
                }
            }

            bool resolved = false;
            foreach (SyntaxReference reference in member.DeclaringSyntaxReferences)
            {
                ExpressionSyntax? initializer = reference.GetSyntax(cancellationToken) switch
                {
                    VariableDeclaratorSyntax variable => variable.Initializer?.Value,
                    PropertyDeclarationSyntax property => property.Initializer?.Value ?? property.ExpressionBody?.Expression,
                    _ => null,
                };
                if (initializer is null)
                {
                    return false;
                }

                if (compilation.GetSemanticModel(reference.SyntaxTree).GetOperation(initializer, cancellationToken) is not { } operation
                    || !Codes(operation, rejects, compilation, depth + 1, cancellationToken))
                {
                    return false;
                }

                resolved = true;
            }

            return resolved;
        }

        private static IOperation Unwrap(IOperation value)
        {
            while (value is IConversionOperation conversion)
            {
                value = conversion.Operand;
            }

            return value;
        }

        private static bool ReturnsDecision(ITypeSymbol type)
        {
            if (type.Is(KnownNames.Decision))
            {
                return true;
            }

            return type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } generic
                && (generic.Is("System.Threading.Tasks.ValueTask`1") || generic.Is("System.Threading.Tasks.Task`1"))
                && generic.TypeArguments[0].Is(KnownNames.Decision);
        }

        private static bool IsFramework(INamedTypeSymbol? type)
        {
            string? ns = type?.ContainingNamespace?.ToDisplayString();
            return ns is not null && (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal));
        }
    }
}

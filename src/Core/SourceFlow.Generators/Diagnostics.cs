using Microsoft.CodeAnalysis;

namespace SourceFlow.Generators
{
    /// <summary>SourceFlow diagnostics (design doc 03 §9). Ids are stable; never reuse a retired id.</summary>
    internal static class Diagnostics
    {
        private const string HelpBase = "https://github.com/CodeShayk/SourceFlow.Net/blob/v3.0.0/docs/v3.0.0/03-Domain-Programming-Model.md#9-analyzers";

        public static readonly DiagnosticDescriptor MissingMessageType = new(
            "SF0001", "Message type has no [MessageType]",
            "'{0}' implements a SourceFlow message interface but has no [MessageType(\"name\", version)] attribute",
            "SourceFlow.Messages", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpBase);

        public static readonly DiagnosticDescriptor DuplicateMessageType = new(
            "SF0002", "Duplicate message type name and version",
            "Message type ('{0}', {1}) is declared by both '{2}' and '{3}'",
            "SourceFlow.Messages", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpBase);

        public static readonly DiagnosticDescriptor CommandTarget = new(
            "SF0003", "Command target id is missing or ambiguous",
            "Command '{0}' targets '{1}' but {2}; give it exactly one member of type '{3}', or mark one with [AggregateId]",
            "SourceFlow.Commands", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpBase);

        public static readonly DiagnosticDescriptor ImpureWhen = new(
            "SF0004", "When method has side effects",
            "'When' in '{0}' must be pure: {1}",
            "SourceFlow.Purity", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpBase);

        public static readonly DiagnosticDescriptor EventWithoutWhen = new(
            "SF0005", "Event produced by Decide has no When",
            "'{0}' produces '{1}' in Decide but has no 'When({1})'; the state ignores it",
            "SourceFlow.Shape", DiagnosticSeverity.Warning, isEnabledByDefault: true, helpLinkUri: HelpBase);

        public static readonly DiagnosticDescriptor OwnedAndRouted = new(
            "SF0014", "Command is both owned and routed",
            "Command '{0}' is handled in this service by '{1}' and also routed to an endpoint; a command is owned or routed, never both (ADR-007)",
            "SourceFlow.Commands", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpBase);

        public static readonly DiagnosticDescriptor NoOwner = new(
            "SF0006", "Command has no Decide or handler",
            "Command '{0}' targets '{1}' but has no Decide method and no ICommandHandler in this compilation; add one or route it (startup fails otherwise)",
            "SourceFlow.Commands", DiagnosticSeverity.Warning, isEnabledByDefault: true, helpLinkUri: HelpBase, customTags: WellKnownDiagnosticTags.CompilationEnd);

        public static readonly DiagnosticDescriptor MissingJsonType = new(
            "SF0010", "Type missing from the module's JSON context",
            "'{0}' is missing from JSON context '{1}'; add [JsonSerializable(typeof({0}))] (DC-1)",
            "SourceFlow.Serialization", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpBase, customTags: WellKnownDiagnosticTags.CompilationEnd);

        public static readonly DiagnosticDescriptor AsyncDecide = new(
            "SF0007", "Decide must be synchronous",
            "'Decide' in '{0}' must return Decision synchronously; use an ICommandHandler for I/O",
            "SourceFlow.Purity", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpBase);

        public static readonly DiagnosticDescriptor StateShape = new(
            "SF0008", "Aggregate or process manager shape",
            "'{0}' must be {1}",
            "SourceFlow.Shape", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpBase);

        public static readonly DiagnosticDescriptor NonDeterministic = new(
            "SF0009", "Non-deterministic API in a pure method",
            "'{0}' is non-deterministic and must not be used in '{1}'; use the context (Now, ids) instead",
            "SourceFlow.Purity", DiagnosticSeverity.Warning, isEnabledByDefault: true, helpLinkUri: HelpBase);
    }
}

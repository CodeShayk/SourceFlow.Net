using Microsoft.CodeAnalysis;

namespace SourceFlow.Generators
{
    /// <summary>SourceFlow diagnostics (design doc 03 §9). Ids are stable; never reuse a retired id.</summary>
    internal static class Diagnostics
    {
        private const string HelpBase = "https://github.com/CodeShayk/SourceFlow.Net/blob/v3.0.0/docs/v3.0.0/Design/03-Domain-Programming-Model.md#9-analyzers";

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

        public static readonly DiagnosticDescriptor UnresolvableSubject = new(
            "SF0011", "Personal data has no resolvable subject",
            "[PersonalData(Subject = \"{0}\")] on '{1}.{2}' names no member of '{1}'; the subject key must be a member of the event (doc 10 §4)",
            "SourceFlow.Security", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpBase);

        public static readonly DiagnosticDescriptor PersonalDataPublishedAsIs = new(
            "SF0013", "AsIs() publishes personal data",
            "'.AsIs()' publishes '{0}', which holds [PersonalData] ('{1}'); map it to an integration event without personal data (doc 10 §4)",
            "SourceFlow.Security", DiagnosticSeverity.Warning, isEnabledByDefault: true, helpLinkUri: HelpBase);

        public static readonly DiagnosticDescriptor QueryablePersonalData = new(
            "SF0017", "[Queryable] on personal data has no effect",
            "'{0}.{1}' is both [Queryable] and [PersonalData]; ciphertext can't be indexed or filtered, so [Queryable] has no effect",
            "SourceFlow.Security", DiagnosticSeverity.Warning, isEnabledByDefault: true, helpLinkUri: HelpBase);

        public static readonly DiagnosticDescriptor OwnedAndRouted = new(
            "SF0014", "Command is both owned and routed",
            "Command '{0}' is handled in this service by '{1}' and also routed to an endpoint; a command is owned or routed, never both (ADR-007)",
            "SourceFlow.Commands", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpBase);

        public static readonly DiagnosticDescriptor QueryInWriteModel = new(
            "SF0015", "The query side is used in write-model code",
            "'{0}' uses {1}; Decide, When, React and projections get state only from their own stream (Q-R1)",
            "SourceFlow.Query", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpBase);

        public static readonly DiagnosticDescriptor QueryInCommandHandler = new(
            "SF0016", "A command handler reads through the query side",
            "'{0}' uses {1}: decisions based on eventually consistent reads (Q-R1)",
            "SourceFlow.Query", DiagnosticSeverity.Warning, isEnabledByDefault: true, helpLinkUri: HelpBase);

        public static readonly DiagnosticDescriptor DomainQueryShape = new(
            "SF0018", "Domain query is not valid",
            "Domain query '{0}': {1}",
            "SourceFlow.Query", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpBase);

        public static readonly DiagnosticDescriptor UndeclaredOutcome = new(
            "SF0019", "Decide outcome can't be determined",
            "'{0}' for '{1}' {2}, so the service manifest can't list it (doc 15 §3.2)",
            "SourceFlow.Manifest", DiagnosticSeverity.Warning, isEnabledByDefault: true, helpLinkUri: HelpBase);

        public static readonly DiagnosticDescriptor WorkflowInvalid = new(
            "SF0020", "Workflow DSL validation failure",
            "{0}",
            "SourceFlow.Workflows", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: "https://github.com/CodeShayk/SourceFlow.Net/blob/v3.0.0/docs/v3.0.0/Design/15-Workflow-DSL-and-Studio.md#54-validation-rules-wfd-28");

        public static readonly DiagnosticDescriptor WorkflowHookMissing = new(
            "SF0021", "Workflow hook has no implementation",
            "{0}",
            "SourceFlow.Workflows", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: "https://github.com/CodeShayk/SourceFlow.Net/blob/v3.0.0/docs/v3.0.0/Design/15-Workflow-DSL-and-Studio.md#55-code-generation-wfd-29");

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

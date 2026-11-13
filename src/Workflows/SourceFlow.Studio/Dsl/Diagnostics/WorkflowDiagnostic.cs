using System;
using System.Collections.Generic;
using SourceFlow.Studio.Dsl.Model;

namespace SourceFlow.Studio.Dsl.Diagnostics
{
    /// <summary>How serious a diagnostic is.</summary>
    public enum WorkflowSeverity
    {
        /// <summary>The workflow can't be generated.</summary>
        Error = 0,

        /// <summary>The workflow works but probably not as intended.</summary>
        Warning = 1,
    }

    /// <summary>A reader or validator finding, with a stable id (doc 15 §5.4) and a position in the workflow file.</summary>
    public sealed class WorkflowDiagnostic : IEquatable<WorkflowDiagnostic>
    {
        /// <summary>Creates a diagnostic.</summary>
        public WorkflowDiagnostic(string id, WorkflowSeverity severity, string message, SourceLocation location)
        {
            Id = id;
            Severity = severity;
            Message = message;
            Location = location;
        }

        /// <summary>The stable rule id, for example <c>WF-V06</c> or <c>WF-S02</c>.</summary>
        public string Id { get; }

        /// <summary>The severity.</summary>
        public WorkflowSeverity Severity { get; }

        /// <summary>The message.</summary>
        public string Message { get; }

        /// <summary>Where in the workflow file.</summary>
        public SourceLocation Location { get; }

        /// <summary>Whether this is an error.</summary>
        public bool IsError => Severity == WorkflowSeverity.Error;

        /// <inheritdoc />
        public bool Equals(WorkflowDiagnostic? other)
        {
            return other is not null && Id == other.Id && Severity == other.Severity && Message == other.Message && Location.Equals(other.Location);
        }

        /// <inheritdoc />
        public override bool Equals(object? obj)
        {
            return Equals(obj as WorkflowDiagnostic);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return StringComparer.Ordinal.GetHashCode(Id) ^ Location.GetHashCode();
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return Location + ": " + (IsError ? "error" : "warning") + " " + Id + ": " + Message;
        }
    }

    /// <summary>
    /// The diagnostic ids. <c>WF-Sxx</c> are structural (the reader; they match <c>schemas/workflow-1.json</c>);
    /// <c>WF-Vxx</c> are the validation rules of doc 15 §5.4. Ids are stable: never reuse a retired one.
    /// </summary>
    public static class WorkflowRules
    {
        /// <summary>The file is not valid YAML, is not a mapping, or uses anchors or aliases.</summary>
        public const string Syntax = "WF-S01";

        /// <summary>A key the DSL doesn't define.</summary>
        public const string UnknownKey = "WF-S02";

        /// <summary>A required key is missing or a value has the wrong shape.</summary>
        public const string Shape = "WF-S03";

        /// <summary>An unsupported <c>sourceflow.workflow</c> version.</summary>
        public const string SchemaVersion = "WF-S04";

        /// <summary>A duration that is not ISO-8601 (<c>P[nW][nD][T[nH][nM][nS]]</c>).</summary>
        public const string Duration = "WF-S05";

        /// <summary>A name, step id, message reference or path in the wrong format.</summary>
        public const string Format = "WF-S06";

        /// <summary>WF-V01: an unknown message type.</summary>
        public const string UnknownType = "WF-V01";

        /// <summary>WF-V02: a command without a single owner, or not routed to it.</summary>
        public const string CommandOwner = "WF-V02";

        /// <summary>WF-V03: an awaited event that can't arrive.</summary>
        public const string Unreachable = "WF-V03";

        /// <summary>WF-V04: an awaited message that can't be correlated.</summary>
        public const string Correlation = "WF-V04";

        /// <summary>WF-V05: an unreachable step, or no path to <c>Completed</c>.</summary>
        public const string Reachability = "WF-V05";

        /// <summary>WF-V06: a wait without a timeout.</summary>
        public const string Timeout = "WF-V06";

        /// <summary>WF-V07: <c>then: fail</c> or <c>compensate</c> at or after the pivot.</summary>
        public const string Pivot = "WF-V07";

        /// <summary>WF-V08: a rejection the command can't produce, or that isn't replied to.</summary>
        public const string Rejection = "WF-V08";

        /// <summary>WF-V09: a step before the pivot that changes another service without compensation.</summary>
        public const string MissingCompensation = "WF-V09";

        /// <summary>WF-V10: an unbounded or invalid retry.</summary>
        public const string Retry = "WF-V10";

        /// <summary>WF-V11: a missing <c>goto</c>/<c>next</c> target, or a loop with no wait.</summary>
        public const string Goto = "WF-V11";

        /// <summary>WF-V12: a mapped field that doesn't exist, has the wrong type, or a required field that isn't mapped.</summary>
        public const string Map = "WF-V12";

        /// <summary>WF-V13: a guard that doesn't parse or bind.</summary>
        public const string Guard = "WF-V13";

        /// <summary>WF-V14: personal data in instance data that isn't marked personal.</summary>
        public const string PersonalData = "WF-V14";

        /// <summary>WF-V15: a duplicate <c>name@version</c>, or a published version changed without a new version.</summary>
        public const string Version = "WF-V15";

        /// <summary>WF-V16: a compensation that is itself compensated or branches.</summary>
        public const string NestedCompensation = "WF-V16";

        /// <summary>The default severity of each rule.</summary>
        public static WorkflowSeverity SeverityOf(string id)
        {
            return id == MissingCompensation || id == PersonalData ? WorkflowSeverity.Warning : WorkflowSeverity.Error;
        }

        /// <summary>Every rule id, in order.</summary>
        public static IReadOnlyList<string> All { get; } = new[]
        {
            Syntax, UnknownKey, Shape, SchemaVersion, Duration, Format,
            UnknownType, CommandOwner, Unreachable, Correlation, Reachability, Timeout, Pivot, Rejection,
            MissingCompensation, Retry, Goto, Map, Guard, PersonalData, Version, NestedCompensation,
        };
    }
}

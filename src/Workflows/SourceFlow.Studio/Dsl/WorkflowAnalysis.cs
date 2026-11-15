using System;
using System.Collections.Generic;
using System.Linq;
using SourceFlow.Studio.Dsl.Diagnostics;
using SourceFlow.Studio.Dsl.Model;
using SourceFlow.Studio.Dsl.Reading;
using SourceFlow.Studio.Dsl.Validation;

namespace SourceFlow.Studio.Dsl
{
    /// <summary>A read and validated workflow.</summary>
    public sealed class WorkflowAnalysisResult
    {
        internal WorkflowAnalysisResult(WorkflowDefinition? workflow, IReadOnlyList<WorkflowDiagnostic> diagnostics)
        {
            Workflow = workflow;
            Diagnostics = diagnostics;
        }

        /// <summary>The workflow, or null when the file isn't a readable YAML mapping.</summary>
        public WorkflowDefinition? Workflow { get; }

        /// <summary>Structural and validation diagnostics, sorted by position.</summary>
        public IReadOnlyList<WorkflowDiagnostic> Diagnostics { get; }

        /// <summary>Whether any diagnostic is an error.</summary>
        public bool HasErrors => Diagnostics.Any(d => d.IsError);
    }

    /// <summary>Reads and validates a workflow file in one call (the Studio, CLI and generator entry point).</summary>
    public static class WorkflowAnalysis
    {
        /// <summary>Reads <paramref name="text"/> and, when it is a workflow, validates it against <paramref name="environment"/>.</summary>
        public static WorkflowAnalysisResult Analyze(string text, WorkflowEnvironment environment)
        {
            if (environment is null)
            {
                throw new ArgumentNullException(nameof(environment));
            }

            ReadResult read = WorkflowReader.Read(text);
            if (read.Workflow is null)
            {
                return new WorkflowAnalysisResult(null, read.Diagnostics);
            }

            IReadOnlyList<WorkflowDiagnostic> validation = WorkflowValidator.Validate(read.Workflow, environment);
            List<WorkflowDiagnostic> all = read.Diagnostics.Concat(validation)
                .Distinct()
                .OrderBy(d => d.Location.Line)
                .ThenBy(d => d.Location.Column)
                .ThenBy(d => d.Id, StringComparer.Ordinal)
                .ThenBy(d => d.Message, StringComparer.Ordinal)
                .ToList();
            return new WorkflowAnalysisResult(read.Workflow, all);
        }
    }
}

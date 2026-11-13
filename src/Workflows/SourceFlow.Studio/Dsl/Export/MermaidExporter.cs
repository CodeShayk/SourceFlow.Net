using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SourceFlow.Studio.Dsl.Model;

namespace SourceFlow.Studio.Dsl.Export
{
    /// <summary>
    /// Mermaid <c>stateDiagram-v2</c> export (doc 15 §6.3). Each path kind has a symbol and a text label, never colour
    /// alone (WFD-NFR-4): ✓ success, ✕ failure, ↻ retry, ⏱ timeout, ⟲ compensation (numbered in the order it runs),
    /// ◆ pivot, ✋ manual intervention.
    /// </summary>
    public static class MermaidExporter
    {
        /// <summary>Exports <paramref name="workflow"/>.</summary>
        public static string Export(WorkflowDefinition workflow)
        {
            if (workflow is null)
            {
                throw new ArgumentNullException(nameof(workflow));
            }

            var text = new StringBuilder();
            text.Append("---\ntitle: ").Append(workflow.ProcessName).Append(" (").Append(workflow.Owner).Append(")\n---\n");
            text.Append("stateDiagram-v2\n");
            Line(text, "state \"Compensating\" as Compensating");
            Line(text, "state \"Manual intervention\" as ManualIntervention");
            foreach (WorkflowStep step in workflow.Steps)
            {
                Line(text, "state \"" + step.Id + "\" as " + Id(step.Id));
                var notes = new List<string>();
                if (step.Pivot)
                {
                    notes.Add("◆ pivot");
                }

                if (step.Send is { } send)
                {
                    notes.Add("send " + send.Command);
                }

                notes.Add("await " + step.Await?.Success.On);
                foreach (string note in notes)
                {
                    Line(text, Id(step.Id) + " : " + note);
                }
            }

            if (workflow.Steps.Count > 0)
            {
                Line(text, "[*] --> " + Id(workflow.Steps[0].Id) + " : " + workflow.Start.On);
            }

            bool compensates = workflow.Steps.Any(s => s.Compensate is { None: false });
            for (int i = 0; i < workflow.Steps.Count; i++)
            {
                WorkflowStep step = workflow.Steps[i];
                string from = Id(step.Id);
                string following = i + 1 < workflow.Steps.Count ? Id(workflow.Steps[i + 1].Id) : "Completed";
                string success = step.Next == WorkflowStep.CompleteKeyword ? "Completed" : step.Next is { } next ? Id(next) : following;
                Line(text, from + " --> " + success + " : ✓ " + step.Await?.Success.On);
                if (step.When is { } when)
                {
                    Line(text, from + " --> " + following + " : skip unless " + Escape(when));
                }

                foreach (FailureSpec failure in step.Await?.Failures ?? Array.Empty<FailureSpec>())
                {
                    string label = "✕ " + (failure.Rejected ?? failure.On);
                    if (failure.Retry is { } retry)
                    {
                        Line(text, from + " --> " + from + " : ↻ " + Retry(retry) + " on " + (failure.Rejected ?? failure.On));
                    }

                    Line(text, from + " --> " + Target(failure.Then) + " : " + label);
                }

                if (step.Timeout is { None: false, After: { } after } timeout)
                {
                    if (timeout.Retry is { } retry)
                    {
                        Line(text, from + " --> " + from + " : ↻ " + Retry(retry) + " on timeout");
                    }

                    Line(text, from + " --> " + Target(timeout.Then) + " : ⏱ " + IsoDuration.Format(after));
                }
            }

            if (compensates)
            {
                Line(text, "state Compensating {");
                List<WorkflowStep> undo = workflow.Steps.Where(s => s.Compensate is { None: false }).Reverse().ToList();
                for (int i = 0; i < undo.Count; i++)
                {
                    WorkflowStep step = undo[i];
                    string id = "undo_" + Id(step.Id);
                    Line(text, "    state \"⟲ " + (i + 1) + " " + step.Id + "\" as " + id);
                    Line(text, "    " + id + " : send " + step.Compensate!.Send?.Command);
                    if (step.Compensate.Retry is { } retry)
                    {
                        Line(text, "    " + id + " --> " + id + " : ↻ " + Retry(retry));
                    }
                }

                Line(text, "    [*] --> " + "undo_" + Id(undo[0].Id) + " : most recent first");
                for (int i = 0; i < undo.Count; i++)
                {
                    string target = i + 1 < undo.Count ? "undo_" + Id(undo[i + 1].Id) : "[*]";
                    Line(text, "    undo_" + Id(undo[i].Id) + " --> " + target + " : ✓ " + undo[i].Compensate!.Await?.Success.On);
                }

                Line(text, "}");
                Line(text, "Compensating --> Compensated : ⟲ all undone");
                Line(text, "Compensating --> ManualIntervention : ✋ a compensation failed");
            }
            else
            {
                Line(text, "Compensating --> Compensated : nothing to undo");
            }

            Line(text, "Completed --> [*]");
            Line(text, "Compensated --> [*]");
            Line(text, "ManualIntervention --> [*] : ✋ operator resumes or aborts");
            return text.ToString();
        }

        private static string Target(ThenSpec then)
        {
            switch (then.Kind)
            {
                case ThenKind.Fail: return "Compensating";
                case ThenKind.Manual: return "ManualIntervention";
                case ThenKind.Complete: return "Completed";
                default: return Id(then.Target ?? string.Empty);
            }
        }

        private static string Retry(RetrySpec retry)
        {
            string text = retry.Max + "×";
            if (retry.Initial > TimeSpan.Zero)
            {
                text += " " + (retry.Backoff == Backoff.Exponential ? "exponential from " : "every ") + IsoDuration.Format(retry.Initial);
            }

            return text;
        }

        /// <summary>A Mermaid state id: letters, digits and underscores.</summary>
        internal static string Id(string stepId)
        {
            var id = new StringBuilder();
            foreach (char c in stepId)
            {
                id.Append(char.IsLetterOrDigit(c) ? c : '_');
            }

            return id.ToString();
        }

        private static string Escape(string text)
        {
            return text.Replace(":", "#58;").Replace(";", "#59;");
        }

        private static void Line(StringBuilder text, string line)
        {
            text.Append("    ").Append(line).Append('\n');
        }
    }
}

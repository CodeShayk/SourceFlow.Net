using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SourceFlow.Studio.Dsl.Model;

namespace SourceFlow.Studio.Dsl.Export
{
    /// <summary>A node of the workflow statechart (doc 15 §6.1).</summary>
    /// <param name="Id">The node id: the step id, or <c>start</c>, <c>Completed</c>, <c>Compensating</c>, <c>Compensated</c>, <c>ManualIntervention</c>, <c>undo:{step}</c>.</param>
    /// <param name="Kind">One of the <see cref="GraphNode"/> kind constants.</param>
    /// <param name="Label">The visible name.</param>
    public sealed record GraphNode(string Id, string Kind, string Label)
    {
        /// <summary>Where the workflow starts (the start message).</summary>
        public const string Start = "start";

        /// <summary>A step.</summary>
        public const string Step = "step";

        /// <summary>The point of no return: a step drawn as a diamond (◆).</summary>
        public const string Pivot = "pivot";

        /// <summary>A compensation, inside <c>Compensating</c> (⟲ n, in the order it runs).</summary>
        public const string Compensation = "compensation";

        /// <summary>The group of compensations.</summary>
        public const string Compensating = "compensating";

        /// <summary>A terminal status: Completed or Compensated.</summary>
        public const string Terminal = "terminal";

        /// <summary>Waiting for an operator (✋).</summary>
        public const string Manual = "manual";

        /// <summary>The command the step sends, or null for a step that only waits.</summary>
        public string? Sends { get; init; }

        /// <summary>The event the step waits for.</summary>
        public string? Awaits { get; init; }

        /// <summary>The step's CESQL guard (<c>when</c>), when it can be skipped.</summary>
        public string? When { get; init; }

        /// <summary>The step's timeout.</summary>
        public string? Timeout { get; init; }

        /// <summary>Whether a completed step is undone on failure.</summary>
        public bool Compensable { get; init; }

        /// <summary>For a compensation: its position in the undo order (1 runs first).</summary>
        public int? Order { get; init; }

        /// <summary>The containing node (<c>Compensating</c> for compensations).</summary>
        public string? Parent { get; init; }
    }

    /// <summary>An edge of the workflow statechart, with its kind's symbol and label (never colour alone, WFD-NFR-4).</summary>
    /// <param name="Id">A stable id.</param>
    /// <param name="Source">The source node.</param>
    /// <param name="Target">The target node.</param>
    /// <param name="Kind">One of the <see cref="GraphEdge"/> kind constants.</param>
    /// <param name="Label">The visible label, starting with the kind's symbol.</param>
    /// <param name="Description">A full sentence for assistive technology.</param>
    public sealed record GraphEdge(string Id, string Source, string Target, string Kind, string Label, string Description)
    {
        /// <summary>The start message starts the instance.</summary>
        public const string Start = "start";

        /// <summary>✓ the step succeeded.</summary>
        public const string Success = "success";

        /// <summary>✕ a rejection or failure event.</summary>
        public const string Failure = "failure";

        /// <summary>↻ the step (or compensation) is retried.</summary>
        public const string Retry = "retry";

        /// <summary>⏱ the step timed out.</summary>
        public const string Timeout = "timeout";

        /// <summary>⟲ compensation runs.</summary>
        public const string Compensation = "compensation";

        /// <summary>✋ an operator is needed.</summary>
        public const string Manual = "manual";

        /// <summary>⤼ a guarded step is skipped.</summary>
        public const string Skip = "skip";

        /// <summary>The symbol of a kind.</summary>
        public static string Symbol(string kind)
        {
            switch (kind)
            {
                case Success: return "✓";
                case Failure: return "✕";
                case Retry: return "↻";
                case Timeout: return "⏱";
                case Compensation: return "⟲";
                case Manual: return "✋";
                case Skip: return "⤼";
                default: return "▶";
            }
        }
    }

    /// <summary>A workflow's statechart: what the designer draws (doc 15 §6.1).</summary>
    public sealed class WorkflowGraph
    {
        private WorkflowGraph(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
        {
            Nodes = nodes;
            Edges = edges;
        }

        /// <summary>The nodes, steps in file order.</summary>
        public IReadOnlyList<GraphNode> Nodes { get; }

        /// <summary>The edges, in a stable order.</summary>
        public IReadOnlyList<GraphEdge> Edges { get; }

        /// <summary>Builds the statechart of <paramref name="workflow"/>: the same transitions as the Mermaid export.</summary>
        public static WorkflowGraph Build(WorkflowDefinition workflow)
        {
            if (workflow is null)
            {
                throw new ArgumentNullException(nameof(workflow));
            }

            var nodes = new List<GraphNode> { new GraphNode(GraphNode.Start, GraphNode.Start, workflow.Start.On) };
            foreach (WorkflowStep step in workflow.Steps)
            {
                nodes.Add(new GraphNode(step.Id, step.Pivot ? GraphNode.Pivot : GraphNode.Step, step.Id)
                {
                    Sends = step.Send?.Command,
                    Awaits = step.Await?.Success.On,
                    When = step.When,
                    Timeout = step.Timeout is { None: false, After: { } after } ? IsoDuration.Format(after) : null,
                    Compensable = step.Compensate is { None: false },
                });
            }

            nodes.Add(new GraphNode("Completed", GraphNode.Terminal, "Completed"));
            nodes.Add(new GraphNode("Compensating", GraphNode.Compensating, "Compensating"));
            List<WorkflowStep> undo = workflow.Steps.Where(s => s.Compensate is { None: false }).Reverse().ToList();
            for (int i = 0; i < undo.Count; i++)
            {
                nodes.Add(new GraphNode(Undo(undo[i].Id), GraphNode.Compensation, "⟲ " + (i + 1).ToString(CultureInfo.InvariantCulture) + " " + undo[i].Id)
                {
                    Sends = undo[i].Compensate!.Send?.Command,
                    Awaits = undo[i].Compensate!.Await?.Success.On,
                    Order = i + 1,
                    Parent = "Compensating",
                });
            }

            nodes.Add(new GraphNode("Compensated", GraphNode.Terminal, "Compensated"));
            nodes.Add(new GraphNode("ManualIntervention", GraphNode.Manual, "✋ Manual intervention"));

            var edges = new List<GraphEdge>();
            void Edge(string source, string target, string kind, string text, string description)
            {
                string id = source + "->" + target + ":" + kind + ":" + edges.Count(e => e.Source == source && e.Target == target && e.Kind == kind).ToString(CultureInfo.InvariantCulture);
                edges.Add(new GraphEdge(id, source, target, kind, GraphEdge.Symbol(kind) + " " + text, description));
            }

            if (workflow.Steps.Count > 0)
            {
                Edge(GraphNode.Start, workflow.Steps[0].Id, GraphEdge.Start, workflow.Start.On, workflow.Start.On + " starts the workflow at " + workflow.Steps[0].Id);
            }

            for (int i = 0; i < workflow.Steps.Count; i++)
            {
                WorkflowStep step = workflow.Steps[i];
                string following = i + 1 < workflow.Steps.Count ? workflow.Steps[i + 1].Id : "Completed";
                string success = step.Next == WorkflowStep.CompleteKeyword ? "Completed" : step.Next ?? following;
                string on = step.Await?.Success.On ?? string.Empty;
                Edge(step.Id, success, GraphEdge.Success, on, "Success: " + on + " moves from " + step.Id + " to " + success);
                if (step.When is { } when)
                {
                    Edge(step.Id, following, GraphEdge.Skip, "unless " + when, step.Id + " is skipped to " + following + " unless " + when);
                }

                foreach (FailureSpec failure in step.Await?.Failures ?? Array.Empty<FailureSpec>())
                {
                    string cause = failure.Rejected ?? failure.On ?? string.Empty;
                    if (failure.Retry is { } retry)
                    {
                        Edge(step.Id, step.Id, GraphEdge.Retry, Retry(retry) + " on " + cause, "Retry: " + step.Id + " is retried " + Retry(retry) + " on " + cause);
                    }

                    string target = Target(failure.Then);
                    Edge(step.Id, target, GraphEdge.Failure, cause, "Failure: " + cause + " at " + step.Id + " leads to " + target);
                }

                if (step.Timeout is { None: false, After: { } after } timeout)
                {
                    if (timeout.Retry is { } retry)
                    {
                        Edge(step.Id, step.Id, GraphEdge.Retry, Retry(retry) + " on timeout", "Retry: " + step.Id + " is retried " + Retry(retry) + " when it times out");
                    }

                    string target = Target(timeout.Then);
                    Edge(step.Id, target, GraphEdge.Timeout, IsoDuration.Format(after), "Timeout: after " + IsoDuration.Format(after) + " " + step.Id + " leads to " + target);
                }
            }

            if (undo.Count > 0)
            {
                Edge("Compensating", Undo(undo[0].Id), GraphEdge.Compensation, "most recent first", "Compensation starts with " + undo[0].Id + ", the most recent completed step");
                for (int i = 0; i < undo.Count; i++)
                {
                    CompensateSpec compensate = undo[i].Compensate!;
                    if (compensate.Retry is { } retry)
                    {
                        Edge(Undo(undo[i].Id), Undo(undo[i].Id), GraphEdge.Retry, Retry(retry), "Retry: the compensation of " + undo[i].Id + " is retried " + Retry(retry));
                    }

                    string next = i + 1 < undo.Count ? Undo(undo[i + 1].Id) : "Compensated";
                    string done = compensate.Await?.Success.On ?? "sent";
                    Edge(Undo(undo[i].Id), next, GraphEdge.Compensation, done, "Compensation: " + undo[i].Id + " is undone (" + done + "), then " + next);
                }

                Edge("Compensating", "ManualIntervention", GraphEdge.Manual, "a compensation failed", "Manual: a compensation that keeps failing needs an operator");
            }
            else
            {
                Edge("Compensating", "Compensated", GraphEdge.Compensation, "nothing to undo", "Compensation: nothing to undo, the workflow is compensated");
            }

            return new WorkflowGraph(nodes, edges);
        }

        private static string Undo(string step)
        {
            return "undo:" + step;
        }

        private static string Target(ThenSpec then)
        {
            switch (then.Kind)
            {
                case ThenKind.Fail: return "Compensating";
                case ThenKind.Manual: return "ManualIntervention";
                case ThenKind.Complete: return "Completed";
                default: return then.Target ?? string.Empty;
            }
        }

        private static string Retry(RetrySpec retry)
        {
            string text = retry.Max.ToString(CultureInfo.InvariantCulture) + "×";
            if (retry.Initial > TimeSpan.Zero)
            {
                text += " " + (retry.Backoff == Backoff.Exponential ? "exponential from " : "every ") + IsoDuration.Format(retry.Initial);
            }

            return text;
        }
    }
}

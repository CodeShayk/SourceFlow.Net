using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SourceFlow.Studio.Dsl.Model;

namespace SourceFlow.Studio.Dsl.Simulation
{
    /// <summary>One move of a path: an input, or a guard decision.</summary>
    public sealed record PathMove(string Step, string Outcome)
    {
        /// <summary>The input to replay; null for a guard decision (applied through <see cref="WorkflowPath.Guards"/>).</summary>
        public SimulationInput? Input { get; init; }

        /// <summary>The edge of the statechart the move covers, for example <c>charge-payment:rejected:payments.declined</c>.</summary>
        public string Edge { get; init; } = string.Empty;

        /// <summary>The retry loop edge this move also covers (the outcome triggered a retry), or null.</summary>
        public string? RetryEdge { get; init; }

        /// <summary>Whether the move covers <paramref name="edge"/>.</summary>
        public bool Covers(string edge) => Edge == edge || RetryEdge == edge;

        /// <inheritdoc />
        public override string ToString() => Step + ": " + Outcome;
    }

    /// <summary>A distinct path from the start to a terminal or manual state.</summary>
    public sealed class WorkflowPath
    {
        internal WorkflowPath(int number, IReadOnlyList<PathMove> moves, IReadOnlyDictionary<string, bool> guards, WorkflowStatus end)
        {
            Number = number;
            Moves = moves;
            Guards = guards;
            End = end;
        }

        /// <summary>The 1-based path number (stable for a given workflow).</summary>
        public int Number { get; }

        /// <summary>The id, for example <c>P07</c>.</summary>
        public string Id => "P" + Number.ToString("00", CultureInfo.InvariantCulture);

        /// <summary>The moves, in order.</summary>
        public IReadOnlyList<PathMove> Moves { get; }

        /// <summary>The guard decision per guarded step on the path (true: the step ran).</summary>
        public IReadOnlyDictionary<string, bool> Guards { get; }

        /// <summary>Where the path ends: Completed, Compensated or ManualIntervention.</summary>
        public WorkflowStatus End { get; }

        /// <summary>A one-line description.</summary>
        public override string ToString() => Id + " " + string.Join(" → ", Moves.Select(m => m.ToString())) + " ⇒ " + End;
    }

    /// <summary>The enumerated paths and which statechart edges they cover.</summary>
    public sealed class PathSet
    {
        internal PathSet(IReadOnlyList<WorkflowPath> paths, IReadOnlyList<string> edges, bool truncated)
        {
            Paths = paths;
            Edges = edges;
            Truncated = truncated;
        }

        /// <summary>The paths, in a stable order.</summary>
        public IReadOnlyList<WorkflowPath> Paths { get; }

        /// <summary>Every edge of the workflow's statechart.</summary>
        public IReadOnlyList<string> Edges { get; }

        /// <summary>True when the limit stopped the enumeration.</summary>
        public bool Truncated { get; }

        /// <summary>The paths covering <paramref name="edge"/>.</summary>
        public IEnumerable<WorkflowPath> Covering(string edge) => Paths.Where(p => p.Moves.Any(m => m.Covers(edge)));

        /// <summary>Edges no path covers (reachable only through operator actions, or unreachable).</summary>
        public IReadOnlyList<string> Uncovered => Edges.Where(e => !Paths.Any(p => p.Moves.Any(m => m.Covers(e)))).ToList();

        /// <summary>
        /// A small subset of the paths that covers every covered edge (greedy set cover, in path order): the default
        /// selection for generated specs.
        /// </summary>
        public IReadOnlyList<WorkflowPath> MinimalCover()
        {
            var remaining = new HashSet<string>(Edges.Where(e => Paths.Any(p => p.Moves.Any(m => m.Covers(e)))), StringComparer.Ordinal);
            var chosen = new List<WorkflowPath>();
            while (remaining.Count > 0)
            {
                WorkflowPath best = Paths
                    .OrderByDescending(p => remaining.Count(e => p.Moves.Any(m => m.Covers(e))))
                    .ThenBy(p => p.Moves.Count)
                    .ThenBy(p => p.Number)
                    .First();
                int gained = remaining.RemoveWhere(e => best.Moves.Any(m => m.Covers(e)));
                if (gained == 0)
                {
                    break;
                }

                chosen.Add(best);
            }

            // Every end state at least once.
            foreach (WorkflowStatus end in Paths.Select(p => p.End).Distinct())
            {
                if (!chosen.Any(p => p.End == end))
                {
                    chosen.Add(Paths.Where(p => p.End == end).OrderBy(p => p.Moves.Count).ThenBy(p => p.Number).First());
                }
            }

            return chosen.OrderBy(p => p.Number).ToList();
        }
    }

    /// <summary>Limits of the enumeration.</summary>
    public sealed class PathOptions
    {
        /// <summary>The defaults: 2 entries per step, 2,000 paths, 200 moves per path.</summary>
        public static PathOptions Default { get; } = new PathOptions();

        /// <summary>How often a path may enter the same step (loops at 0 and 1 extra pass).</summary>
        public int MaxStepEntries { get; set; } = 2;

        /// <summary>The most paths to return.</summary>
        public int MaxPaths { get; set; } = 2000;

        /// <summary>The longest path.</summary>
        public int MaxMoves { get; set; } = 200;
    }

    /// <summary>
    /// Enumerates the distinct paths of a workflow with the simulator (doc 15 §6.1 path explorer, WFD-34): every
    /// outcome at every wait, guarded steps both taken and skipped, retry loops at 0, 1 and max retries, and loops
    /// through <c>goto</c>/<c>next</c> at most <see cref="PathOptions.MaxStepEntries"/> times. Paths end in a terminal
    /// status or in <c>ManualIntervention</c> (operator actions are not enumerated).
    /// </summary>
    public static class PathEnumerator
    {
        private static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        /// <summary>Enumerates the paths of <paramref name="workflow"/>.</summary>
        public static PathSet Enumerate(WorkflowDefinition workflow, PathOptions? options = null)
        {
            if (workflow is null)
            {
                throw new ArgumentNullException(nameof(workflow));
            }

            options ??= PathOptions.Default;
            var found = new List<(List<PathMove> Moves, Dictionary<string, bool> Guards, WorkflowStatus End)>();
            var pending = new Stack<Node>();
            pending.Push(new Node(new List<PathMove>(), new Dictionary<string, bool>(StringComparer.Ordinal)));
            bool truncated = false;
            while (pending.Count > 0)
            {
                if (found.Count >= options.MaxPaths)
                {
                    truncated = true;
                    break;
                }

                Node node = pending.Pop();
                Probe replay = Probe.Run(workflow, node);
                if (replay.UndecidedGuard is { } guard)
                {
                    // Branch: the step is skipped, or it runs (the run branch is explored first).
                    var skip = new Dictionary<string, bool>(node.Guards, StringComparer.Ordinal) { [guard] = false };
                    var run = new Dictionary<string, bool>(node.Guards, StringComparer.Ordinal) { [guard] = true };
                    pending.Push(new Node(node.Moves, skip));
                    pending.Push(new Node(node.Moves, run));
                    continue;
                }

                if (replay.Pruned)
                {
                    continue;
                }

                SimulationState state = replay.State!;
                if (state.IsTerminal || state.Status == WorkflowStatus.ManualIntervention)
                {
                    found.Add((node.Moves, node.Guards, state.Status));
                    continue;
                }

                if (node.Moves.Count >= options.MaxMoves)
                {
                    truncated = true;
                    continue;
                }

                List<List<PathMove>> next = Choices(workflow, state, node.Moves);
                for (int i = next.Count - 1; i >= 0; i--)
                {
                    var moves = new List<PathMove>(node.Moves);
                    moves.AddRange(next[i]);
                    if (Entries(moves) <= options.MaxStepEntries)
                    {
                        pending.Push(new Node(moves, node.Guards));
                    }
                }
            }

            var paths = new List<WorkflowPath>();
            foreach (var (moves, guards, end) in found)
            {
                var all = new List<PathMove>();
                foreach (PathMove move in WithGuards(workflow, moves, guards))
                {
                    all.Add(move);
                }

                paths.Add(new WorkflowPath(paths.Count + 1, all, guards, end));
            }

            return new PathSet(paths, Edges(workflow), truncated);
        }

        /// <summary>Replays a path with the simulator; the states after each move.</summary>
        public static IReadOnlyList<SimulationResult> Replay(WorkflowDefinition workflow, WorkflowPath path, IReadOnlyDictionary<string, object?>? startData = null)
        {
            if (workflow is null)
            {
                throw new ArgumentNullException(nameof(workflow));
            }

            if (path is null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            var options = new SimulationOptions { Guard = step => step == SimulationOptions.StartGuard || step.StartsWith(SimulationOptions.SuccessGuard, StringComparison.Ordinal) ? true : path.Guards.TryGetValue(step, out bool run) ? run : (bool?)null };
            var results = new List<SimulationResult> { WorkflowSimulator.Start(workflow, startData ?? StartData(workflow), Epoch, options) };
            foreach (PathMove move in path.Moves.Where(m => m.Input is not null))
            {
                results.Add(WorkflowSimulator.Step(results[results.Count - 1].State, move.Input!));
            }

            return results;
        }

        /// <summary>Placeholder start data: the correlation value and nothing else.</summary>
        internal static IReadOnlyDictionary<string, object?> StartData(WorkflowDefinition workflow)
        {
            return CorrelationPath.Data(CorrelationPath.IsValid(workflow.Start.Correlate) ? workflow.Start.Correlate : CorrelationPath.Prefix + "id", "instance-1");
        }

        // The choices at a wait: each outcome once, and each retryable outcome repeated until its retries run out.
        private static List<List<PathMove>> Choices(WorkflowDefinition workflow, SimulationState state, List<PathMove> moves)
        {
            var choices = new List<List<PathMove>>();
            string step = state.Step!;
            if (state.Phase == WorkflowPhase.Retrying)
            {
                choices.Add(new List<PathMove> { Move(step, "retry", SimulationInput.FireTimer(), state.Status == WorkflowStatus.Compensating ? "compensate:" + step + ":retry" : step + ":retry") });
                return choices;
            }

            HashSet<string> tried = TriedInThisVisit(moves, step);
            if (state.Status == WorkflowStatus.Compensating)
            {
                CompensateSpec compensate = workflow.Step(step)!.Compensate!;
                choices.Add(new List<PathMove> { Move(step, "compensated", SimulationInput.Succeed(), "compensate:" + step + ":success") });
                if (state.Timer is { Kind: SimulationTimer.CompensateTimeout } && !tried.Contains("compensate:" + step + ":timeout"))
                {
                    choices.AddRange(Repeated(step, "compensation timeout", SimulationInput.FireTimer(), "compensate:" + step + ":timeout", compensate.Retry));
                }

                return choices;
            }

            WorkflowStep current = workflow.Step(step)!;
            choices.Add(new List<PathMove> { Move(step, "success", SimulationInput.Succeed(), step + ":success") });
            for (int i = 0; i < current.Await!.Failures.Count; i++)
            {
                FailureSpec failure = current.Await.Failures[i];
                string edge = step + (failure.Rejected is { } code ? ":rejected:" + code : ":event:" + failure.On);
                if (tried.Contains(edge))
                {
                    continue;
                }

                SimulationInput input = failure.Rejected is { } rejected ? SimulationInput.Reject(rejected) : SimulationInput.FailWith(failure.On!);
                string label = failure.Rejected is { } r ? "rejected " + r : "failed " + failure.On;
                choices.AddRange(Repeated(step, label, input, edge, failure.Retry));
            }

            if (state.Timer is { Kind: SimulationTimer.Timeout } && !tried.Contains(step + ":timeout"))
            {
                choices.AddRange(Repeated(step, "timeout", SimulationInput.FireTimer(), step + ":timeout", current.Timeout?.Retry));
            }

            return choices;
        }

        // An outcome with R retries: once (a retry follows when R > 0) and R + 1 times in a row (retries exhausted).
        // A retry with a delay waits for its timer (a "retry" move); one without re-sends at once.
        private static IEnumerable<List<PathMove>> Repeated(string step, string label, SimulationInput input, string edge, RetrySpec? retry)
        {
            int retries = retry?.Max ?? 0;
            string retryEdge = edge.StartsWith("compensate:", StringComparison.Ordinal) ? "compensate:" + step + ":retry" : step + ":retry";
            yield return new List<PathMove> { Move(step, label, input, edge) with { RetryEdge = retries >= 1 ? retryEdge : null } };
            if (retries >= 1)
            {
                var exhausted = new List<PathMove>();
                for (int i = 0; i <= retries; i++)
                {
                    if (i > 0 && retry!.Delay(i) > TimeSpan.Zero)
                    {
                        exhausted.Add(Move(step, "retry", SimulationInput.FireTimer(), retryEdge));
                    }

                    exhausted.Add(Move(step, label + (i == retries ? " (retries exhausted)" : string.Empty), input, edge) with { RetryEdge = i < retries ? retryEdge : null });
                }

                yield return exhausted;
            }
        }

        private static PathMove Move(string step, string outcome, SimulationInput input, string edge)
        {
            return new PathMove(step, outcome) { Input = input, Edge = edge };
        }

        // Outcomes already taken since the path last entered this step, so "once" and "exhausted" don't repeat.
        private static HashSet<string> TriedInThisVisit(List<PathMove> moves, string step)
        {
            var tried = new HashSet<string>(StringComparer.Ordinal);
            for (int i = moves.Count - 1; i >= 0; i--)
            {
                PathMove move = moves[i];
                if (move.Step != step)
                {
                    break;
                }

                if (move.Outcome != "retry")
                {
                    tried.Add(move.Edge);
                }
            }

            return tried;
        }

        private static int Entries(List<PathMove> moves)
        {
            // Visits per step: a new visit starts when the step follows a different one.
            var visits = new Dictionary<string, int>(StringComparer.Ordinal);
            string? previous = null;
            foreach (PathMove move in moves)
            {
                if (move.Step != previous)
                {
                    visits[move.Step] = (visits.TryGetValue(move.Step, out int n) ? n : 0) + 1;
                    previous = move.Step;
                }
            }

            return visits.Count == 0 ? 0 : visits.Values.Max();
        }

        // Inserts the skipped guarded steps where the simulator skipped them, for display and coverage.
        private static IEnumerable<PathMove> WithGuards(WorkflowDefinition workflow, List<PathMove> moves, Dictionary<string, bool> guards)
        {
            var options = new SimulationOptions { Guard = step => step == SimulationOptions.StartGuard || step.StartsWith(SimulationOptions.SuccessGuard, StringComparison.Ordinal) ? true : guards.TryGetValue(step, out bool run) ? run : (bool?)null };
            SimulationResult result = WorkflowSimulator.Start(workflow, StartData(workflow), Epoch, options);
            foreach (PathMove skipped in Skips(result))
            {
                yield return skipped;
            }

            foreach (PathMove move in moves)
            {
                yield return move;
                result = WorkflowSimulator.Step(result.State, move.Input!);
                foreach (PathMove skipped in Skips(result))
                {
                    yield return skipped;
                }
            }
        }

        private static IEnumerable<PathMove> Skips(SimulationResult result)
        {
            return result.Effects.Where(e => e.Kind == SimulationEffect.Skip).Select(e => new PathMove(e.Text, "skipped") { Edge = e.Text + ":skip" });
        }

        /// <summary>Every edge of the statechart, in workflow order.</summary>
        internal static IReadOnlyList<string> Edges(WorkflowDefinition workflow)
        {
            var edges = new List<string>();
            foreach (WorkflowStep step in workflow.Steps)
            {
                edges.Add(step.Id + ":success");
                if (step.When is not null)
                {
                    edges.Add(step.Id + ":skip");
                }

                foreach (FailureSpec failure in step.Await?.Failures ?? Array.Empty<FailureSpec>())
                {
                    edges.Add(step.Id + (failure.Rejected is { } code ? ":rejected:" + code : ":event:" + failure.On));
                    if (failure.Retry is not null && !edges.Contains(step.Id + ":retry"))
                    {
                        edges.Add(step.Id + ":retry");
                    }
                }

                if (step.Timeout is { None: false })
                {
                    edges.Add(step.Id + ":timeout");
                    if (step.Timeout.Retry is not null && !edges.Contains(step.Id + ":retry"))
                    {
                        edges.Add(step.Id + ":retry");
                    }
                }

                if (step.Compensate is { None: false } compensate)
                {
                    edges.Add("compensate:" + step.Id + ":success");
                    if (compensate.Timeout is { None: false })
                    {
                        edges.Add("compensate:" + step.Id + ":timeout");
                    }

                    if (compensate.Retry is not null)
                    {
                        edges.Add("compensate:" + step.Id + ":retry");
                    }
                }
            }

            return edges;
        }

        private sealed class Node
        {
            public Node(List<PathMove> moves, Dictionary<string, bool> guards)
            {
                Moves = moves;
                Guards = guards;
            }

            public List<PathMove> Moves { get; }

            public Dictionary<string, bool> Guards { get; }
        }

        private sealed class Probe
        {
            public SimulationState? State { get; private set; }

            public string? UndecidedGuard { get; private set; }

            public bool Pruned { get; private set; }

            public static Probe Run(WorkflowDefinition workflow, Node node)
            {
                var replay = new Probe();
                var options = new SimulationOptions
                {
                    Guard = step =>
                    {
                        if (step == SimulationOptions.StartGuard || step.StartsWith(SimulationOptions.SuccessGuard, StringComparison.Ordinal))
                        {
                            return true;   // an instance starts, and the awaited event matches
                        }

                        if (node.Guards.TryGetValue(step, out bool decided))
                        {
                            return decided;
                        }

                        replay.UndecidedGuard ??= step;
                        return true;
                    },
                };

                SimulationResult result = WorkflowSimulator.Start(workflow, StartData(workflow), Epoch, options);
                foreach (PathMove move in node.Moves)
                {
                    if (replay.UndecidedGuard is not null)
                    {
                        return replay;
                    }

                    result = WorkflowSimulator.Step(result.State, move.Input!);
                    if (result.Effects.Any(e => e.Kind == SimulationEffect.Ignored))
                    {
                        replay.Pruned = true;   // a move that no longer applies
                        return replay;
                    }
                }

                replay.State = result.State;
                return replay;
            }
        }
    }
}

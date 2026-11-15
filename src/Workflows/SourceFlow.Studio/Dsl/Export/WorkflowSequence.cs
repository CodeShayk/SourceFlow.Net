using System;
using System.Collections.Generic;
using System.Linq;
using SourceFlow.Studio.Dsl.Manifests;
using SourceFlow.Studio.Dsl.Model;
using SourceFlow.Studio.Dsl.Simulation;
using SourceFlow.Studio.Dsl.Validation;

namespace SourceFlow.Studio.Dsl.Export
{
    /// <summary>A lane of the swimlane view: the workflow, a service of the domain, or messages from outside it.</summary>
    /// <param name="Id">The lane id: <c>workflow</c>, a service name, or <c>external</c>.</param>
    /// <param name="Kind"><c>workflow</c>, <c>service</c> or <c>external</c>.</param>
    /// <param name="Label">The visible name.</param>
    public sealed record SequenceLane(string Id, string Kind, string Label);

    /// <summary>One message (or note) of a path, between two lanes, in order.</summary>
    /// <param name="From">The sending lane.</param>
    /// <param name="To">The receiving lane (the same as <paramref name="From"/> for a note).</param>
    /// <param name="Kind">
    /// <c>start</c>, <c>command</c>, <c>event</c>, <c>rejection</c>, <c>failure</c> (a failure event), <c>timer</c>,
    /// <c>compensation</c> (an undo command), <c>status</c> or <c>manual</c>.
    /// </param>
    /// <param name="Label">The visible label, with the kind's symbol.</param>
    /// <param name="Step">The step it belongs to, when there is one.</param>
    public sealed record SequenceMessage(string From, string To, string Kind, string Label, string? Step)
    {
        /// <summary>The message type, for commands and events.</summary>
        public string? Type { get; init; }

        /// <summary>The attempt of the step or compensation.</summary>
        public int? Attempt { get; init; }
    }

    /// <summary>
    /// The swimlane sequence of one path (doc 15 §6.1, WFD-31): the commands and events between the workflow and the
    /// domain's services in order, including retries and compensations, from a simulator replay of the path. Commands
    /// go to their owner (from the manifests); events come from the service that emits or publishes them.
    /// </summary>
    public sealed class WorkflowSequence
    {
        /// <summary>The workflow's own lane.</summary>
        public const string WorkflowLane = "workflow";

        /// <summary>The lane of messages no manifest accounts for.</summary>
        public const string ExternalLane = "external";

        private WorkflowSequence(WorkflowPath path, IReadOnlyList<SequenceLane> lanes, IReadOnlyList<SequenceMessage> messages)
        {
            Path = path;
            Lanes = lanes;
            Messages = messages;
        }

        /// <summary>The path.</summary>
        public WorkflowPath Path { get; }

        /// <summary>The lanes: the workflow first, then services in order of appearance.</summary>
        public IReadOnlyList<SequenceLane> Lanes { get; }

        /// <summary>The messages, in order.</summary>
        public IReadOnlyList<SequenceMessage> Messages { get; }

        /// <summary>Builds the sequence of <paramref name="path"/>.</summary>
        public static WorkflowSequence Build(WorkflowDefinition workflow, WorkflowPath path, WorkflowEnvironment environment)
        {
            if (workflow is null)
            {
                throw new ArgumentNullException(nameof(workflow));
            }

            if (path is null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            if (environment is null)
            {
                throw new ArgumentNullException(nameof(environment));
            }

            var messages = new List<SequenceMessage>();
            IReadOnlyList<SimulationResult> results = PathEnumerator.Replay(workflow, path);
            PathMove[] moves = path.Moves.Where(m => m.Input is not null).ToArray();
            messages.Add(new SequenceMessage(Emitter(environment, workflow.Start.On), WorkflowLane, "start", "▶ " + workflow.Start.On, null) { Type = workflow.Start.On });
            Effects(workflow, environment, results[0], compensating: false, messages);
            for (int i = 0; i < moves.Length && i + 1 < results.Count; i++)
            {
                SimulationState before = results[i].State;
                Input(workflow, environment, before, moves[i], messages);
                Effects(workflow, environment, results[i + 1], before.Status == WorkflowStatus.Compensating, messages);
            }

            var lanes = new List<SequenceLane> { new SequenceLane(WorkflowLane, "workflow", workflow.ProcessName + " (" + workflow.Owner + ")") };
            foreach (string lane in messages.SelectMany(m => new[] { m.From, m.To }).Where(l => l != WorkflowLane).Distinct(StringComparer.Ordinal))
            {
                lanes.Add(lane == ExternalLane ? new SequenceLane(lane, "external", "outside the domain") : new SequenceLane(lane, "service", lane));
            }

            return new WorkflowSequence(path, lanes, messages);
        }

        // What arrived at the workflow for a move: the awaited event, a rejection, a failure event, or a timer.
        private static void Input(WorkflowDefinition workflow, WorkflowEnvironment environment, SimulationState before, PathMove move, List<SequenceMessage> messages)
        {
            string? stepId = before.Step;
            WorkflowStep? step = stepId is null ? null : workflow.Step(stepId);
            bool compensating = before.Status == WorkflowStatus.Compensating;
            switch (move.Input)
            {
                case SimulationInput.Outcome { Kind: "success" }:
                {
                    string? type = compensating ? step?.Compensate?.Await?.Success.On : step?.Await?.Success.On;
                    string? command = compensating ? step?.Compensate?.Send?.Command : step?.Send?.Command;
                    if (type is not null)
                    {
                        string from = command is not null ? Owner(environment, command) : Emitter(environment, type);
                        messages.Add(new SequenceMessage(from, WorkflowLane, "event", "✓ " + type, stepId) { Type = type, Attempt = before.Attempt });
                    }

                    break;
                }

                case SimulationInput.Outcome { Kind: "rejected" } rejected:
                {
                    string command = (compensating ? step?.Compensate?.Send?.Command : step?.Send?.Command) ?? string.Empty;
                    messages.Add(new SequenceMessage(command.Length > 0 ? Owner(environment, command) : ExternalLane, WorkflowLane, "rejection", "✕ rejected " + rejected.Value, stepId)
                    {
                        Type = command,
                        Attempt = before.Attempt,
                    });
                    break;
                }

                case SimulationInput.Outcome failed:
                    messages.Add(new SequenceMessage(Emitter(environment, failed.Value ?? string.Empty), WorkflowLane, "failure", "✕ " + failed.Value, stepId)
                    {
                        Type = failed.Value,
                        Attempt = before.Attempt,
                    });
                    break;
                case SimulationInput.TimerInput:
                    string label = before.Timer is { Kind: SimulationTimer.Retry } ? "↻ retry delay over" : "⏱ timed out";
                    messages.Add(new SequenceMessage(WorkflowLane, WorkflowLane, "timer", label + (stepId is null ? string.Empty : " (" + stepId + ")"), stepId) { Attempt = before.Attempt });
                    break;
            }
        }

        // What the workflow did: commands to their owners, compensations, status changes and alerts.
        private static void Effects(WorkflowDefinition workflow, WorkflowEnvironment environment, SimulationResult result, bool compensating, List<SequenceMessage> messages)
        {
            foreach (SimulationEffect effect in result.Effects)
            {
                switch (effect.Kind)
                {
                    case SimulationEffect.Status:
                        compensating = effect.Text.StartsWith(nameof(WorkflowStatus.Compensating), StringComparison.Ordinal);
                        messages.Add(new SequenceMessage(WorkflowLane, WorkflowLane, "status", effect.Text, result.State.Step));
                        break;
                    case SimulationEffect.Send when effect.Command is { } sent:
                        bool undo = compensating && workflow.Steps.Any(s => s.Compensate?.Send?.Command == sent.Command);
                        messages.Add(new SequenceMessage(WorkflowLane, Owner(environment, sent.Command), undo ? "compensation" : "command", (undo ? "⟲ " : "→ ") + sent.Command, result.State.Step)
                        {
                            Type = sent.Command,
                            Attempt = result.State.Attempt,
                        });
                        break;
                    case SimulationEffect.Alert:
                        messages.Add(new SequenceMessage(WorkflowLane, WorkflowLane, "manual", "✋ " + effect.Text, result.State.Step));
                        break;
                }
            }
        }

        private static string Owner(WorkflowEnvironment environment, string command)
        {
            return environment.Owners(command).FirstOrDefault()?.Service.Service ?? ExternalLane;
        }

        // The service that publishes or emits an event (a command's emits, or an aggregate's event), else outside.
        private static string Emitter(WorkflowEnvironment environment, string type)
        {
            if (environment.Publications(type).FirstOrDefault() is { Service: { Service: { } publisher } })
            {
                return publisher;
            }

            foreach (ServiceManifest manifest in environment.Manifests)
            {
                if (manifest.Aggregates.Any(a => a.Commands.Any(c => c.Emits.Contains(type))) && manifest.Service is { } emitter)
                {
                    return emitter;
                }
            }

            foreach (ServiceManifest manifest in environment.Manifests)
            {
                if (manifest.FindMessage(type) is { Kind: "event", Aggregate: not null } && manifest.Service is { } owner)
                {
                    return owner;
                }
            }

            return ExternalLane;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SourceFlow.Studio.Dsl.Diagnostics;
using SourceFlow.Studio.Dsl.Export;
using SourceFlow.Studio.Dsl.Json;
using SourceFlow.Studio.Dsl.Manifests;
using SourceFlow.Studio.Dsl.Model;
using SourceFlow.Studio.Dsl.Reading;
using SourceFlow.Studio.Dsl.Simulation;
using SourceFlow.Studio.Dsl.Specs;
using SourceFlow.Studio.Dsl.Validation;

namespace SourceFlow.Studio.Dsl.Api
{
    /// <summary>
    /// The Studio core as a JSON API (ADR-025, WP-S.7). <c>SourceFlow.Studio.Wasm</c> exports each method unchanged
    /// (<c>[JSExport]</c>) and the TypeScript client <c>@sourceflow/core-client</c> calls them through a Web Worker. Every
    /// method takes one JSON request and returns <c>{"ok":true,"result":…}</c> or <c>{"ok":false,"error":"…"}</c> (with
    /// <c>diagnostics</c> when the workflow doesn't read); it never throws. The operations are pure: the same request
    /// always gives the same response, so the client may cache them.
    /// </summary>
    public static class StudioApi
    {
        /// <summary>The API version; the client checks it on load.</summary>
        public const int ApiVersion = 1;

        // The last workflow read, and the last manifest set parsed: an editor sends the same text many times.
        private static readonly object Gate = new object();
        private static (string Text, ReadResult Read)? _lastRead;
        private static (string Key, IReadOnlyList<ServiceManifest> Manifests)? _lastManifests;

        /// <summary>The API and DSL versions: <c>{}</c> → <c>{api, dsl}</c>.</summary>
        public static string Version(string request)
        {
            return Ok(w => w.Begin().Prop("api", ApiVersion).Prop("dsl", "sourceflow.workflow/1").End());
        }

        /// <summary>Reads a workflow: <c>{yaml}</c> → <c>{workflow, diagnostics}</c> (structural diagnostics only; <c>workflow</c> is null when the text isn't a workflow).</summary>
        public static string Parse(string request)
        {
            return Run(request, r =>
            {
                ReadResult read = Read(Text(r, "yaml"));
                return Ok(w =>
                {
                    w.Begin().Key("workflow");
                    if (read.Workflow is { } workflow)
                    {
                        w.Raw(WorkflowJson.Write(workflow));
                    }
                    else
                    {
                        w.Raw("null");
                    }

                    w.Key("diagnostics");
                    Diagnostics(w, read.Diagnostics);
                    w.End();
                });
            });
        }

        /// <summary>
        /// Reads and validates a workflow against the domain's manifests: <c>{yaml, manifests: [json…]}</c> →
        /// <c>{valid, fingerprint, diagnostics}</c> (WF-S*, WF-V*; the same as <c>WorkflowAnalysis.Analyze</c>).
        /// </summary>
        public static string Validate(string request)
        {
            return Run(request, r =>
            {
                WorkflowAnalysisResult analysis = WorkflowAnalysis.Analyze(Text(r, "yaml"), Environment(r));
                return Ok(w =>
                {
                    w.Begin().Key("valid").Raw(analysis.Workflow is not null && !analysis.HasErrors ? "true" : "false");
                    w.Prop("fingerprint", analysis.Workflow is { } workflow ? WorkflowFingerprint.Compute(workflow) : null);
                    w.Key("diagnostics");
                    Diagnostics(w, analysis.Diagnostics);
                    w.End();
                });
            });
        }

        /// <summary>
        /// One simulator move (doc 15 §5.3): <c>{yaml, state?, input, guards?, now?}</c> → the new state and the move's
        /// effects and transitions. <c>state</c> is the opaque string a previous response returned; without it the input
        /// must be <c>{kind:"start", data}</c>. Inputs: <c>start</c>, <c>succeed</c>, <c>fail</c> (event), <c>reject</c>
        /// (code), <c>rejected</c> (command, code), <c>receive</c> (type), <c>timer</c> (the pending one, or kind/step/attempt),
        /// <c>resume</c> (step?), <c>retry-step</c>, <c>abort</c> (reason, compensate?).
        /// </summary>
        public static string SimulateStep(string request)
        {
            return Run(request, r =>
            {
                WorkflowDefinition workflow = Workflow(r);
                JsonValue input = r["input"] ?? throw new ApiException("'input' is required.");
                string kind = input["kind"]?.AsString ?? throw new ApiException("'input.kind' is required.");
                var options = new SimulationOptions { Guard = Guards(r["guards"]) };
                DateTimeOffset? now = Time(r["now"]?.AsString);
                SimulationResult result;
                if (kind == "start")
                {
                    result = WorkflowSimulator.Start(workflow, Data(input["data"]), now ?? DateTimeOffset.UnixEpoch, options);
                }
                else
                {
                    string state = r["state"]?.AsString ?? throw new ApiException("'state' is required after the start.");
                    SimulationState current = SimulationStateJson.Read(workflow, state, options);
                    if (now is { } at && at > current.Now)
                    {
                        current.Now = at;
                    }

                    result = WorkflowSimulator.Step(current, Input(kind, input));
                }

                return Ok(w => Simulation(w, result));
            });
        }

        /// <summary>
        /// Every distinct path of a workflow (doc 15 §6.1): <c>{yaml, maxStepEntries?, maxPaths?, maxMoves?}</c> →
        /// <c>{paths, edges, uncovered, truncated, minimalCover}</c>.
        /// </summary>
        public static string EnumeratePaths(string request)
        {
            return Run(request, r =>
            {
                WorkflowDefinition workflow = Workflow(r);
                PathSet paths = PathEnumerator.Enumerate(workflow, PathOptions(r));
                HashSet<string> cover = new HashSet<string>(paths.MinimalCover().Select(p => p.Id), StringComparer.Ordinal);
                return Ok(w =>
                {
                    w.Begin().Key("paths").Array(paths.Paths, (a, p) =>
                    {
                        a.Begin().Prop("id", p.Id).Prop("number", p.Number).Prop("text", p.ToString()).Prop("end", p.End.ToString());
                        a.Key("moves").Array(p.Moves, (m, move) => m.Begin().Prop("step", move.Step).Prop("outcome", move.Outcome).Prop("edge", move.Edge).Prop("retryEdge", move.RetryEdge).End());
                        a.Key("guards").Begin();
                        foreach (KeyValuePair<string, bool> guard in p.Guards.OrderBy(g => g.Key, StringComparer.Ordinal))
                        {
                            a.Key(guard.Key).Raw(guard.Value ? "true" : "false");
                        }

                        a.End().End();
                    });
                    w.Key("edges").Array(paths.Edges, (a, e) => a.Value(e));
                    w.Key("uncovered").Array(paths.Uncovered, (a, e) => a.Value(e));
                    w.Key("truncated").Raw(paths.Truncated ? "true" : "false");
                    w.Key("minimalCover").Array(paths.Paths.Where(p => cover.Contains(p.Id)), (a, p) => a.Value(p.Id));
                    w.End();
                });
            });
        }

        /// <summary>The Mermaid <c>stateDiagram-v2</c> of a workflow: <c>{yaml}</c> → <c>{text}</c>.</summary>
        public static string ToMermaid(string request)
        {
            return Run(request, r => Text(MermaidExporter.Export(Workflow(r))));
        }

        /// <summary>The BPMN 2.0 XML of a workflow: <c>{yaml}</c> → <c>{text}</c>.</summary>
        public static string ToBpmn(string request)
        {
            return Run(request, r => Text(BpmnExporter.Export(Workflow(r))));
        }

        /// <summary>
        /// <c>WorkflowSpec</c> tests for a workflow's paths: <c>{yaml, manifests, namespace?, moduleType?, workflowType?,
        /// allPaths?, pathIds?}</c> → <c>{text}</c> (C#, for the owner's test project).
        /// </summary>
        public static string GenerateSpecs(string request)
        {
            return Run(request, r =>
            {
                var options = new SpecOptions
                {
                    AllPaths = r["allPaths"]?.AsBoolean ?? false,
                    WorkflowType = r["workflowType"]?.AsString,
                };
                if (r["namespace"]?.AsString is { } ns)
                {
                    options.Namespace = ns;
                }

                if (r["moduleType"]?.AsString is { } module)
                {
                    options.ModuleType = module;
                }

                if (r["pathIds"] is { IsArray: true } ids)
                {
                    options.PathIds = ids.Items.Select(i => i.AsString ?? string.Empty).ToArray();
                }

                return Text(SpecGenerator.Generate(Workflow(r), Environment(r), options));
            });
        }

        /// <summary>
        /// The statechart of a workflow (doc 15 §6.1): <c>{yaml}</c> → <c>{nodes, edges}</c>, with each edge's kind (success,
        /// failure, retry, timeout, compensation, manual, skip, start), symbol label and description.
        /// </summary>
        public static string Graph(string request)
        {
            return Run(request, r =>
            {
                WorkflowGraph graph = WorkflowGraph.Build(Workflow(r));
                return Ok(w =>
                {
                    w.Begin().Key("nodes").Array(graph.Nodes, (a, n) =>
                    {
                        a.Begin().Prop("id", n.Id).Prop("kind", n.Kind).Prop("label", n.Label).Prop("sends", n.Sends).Prop("awaits", n.Awaits)
                            .Prop("when", n.When).Prop("timeout", n.Timeout).Prop("parent", n.Parent);
                        a.Key("compensable").Raw(n.Compensable ? "true" : "false");
                        if (n.Order is { } order)
                        {
                            a.Prop("order", order);
                        }

                        a.End();
                    });
                    w.Key("edges").Array(graph.Edges, (a, e) => a.Begin().Prop("id", e.Id).Prop("source", e.Source).Prop("target", e.Target).Prop("kind", e.Kind)
                        .Prop("label", e.Label).Prop("description", e.Description).End());
                    w.End();
                });
            });
        }

        /// <summary>
        /// The swimlane sequence of one path (WFD-31): <c>{yaml, manifests, path: "P03", maxStepEntries?, maxPaths?, maxMoves?}</c>
        /// → <c>{path, lanes, messages}</c>.
        /// </summary>
        public static string Sequence(string request)
        {
            return Run(request, r =>
            {
                WorkflowDefinition workflow = Workflow(r);
                string id = Text(r, "path");
                PathSet paths = PathEnumerator.Enumerate(workflow, PathOptions(r));
                WorkflowPath path = paths.Paths.FirstOrDefault(p => p.Id == id) ?? throw new ApiException("No path '" + id + "' (the workflow has " + paths.Paths.Count.ToString(CultureInfo.InvariantCulture) + ").");
                WorkflowSequence sequence = WorkflowSequence.Build(workflow, path, Environment(r));
                return Ok(w =>
                {
                    w.Begin().Key("path").Begin().Prop("id", path.Id).Prop("text", path.ToString()).Prop("end", path.End.ToString()).End();
                    w.Key("lanes").Array(sequence.Lanes, (a, l) => a.Begin().Prop("id", l.Id).Prop("kind", l.Kind).Prop("label", l.Label).End());
                    w.Key("messages").Array(sequence.Messages, (a, m) =>
                    {
                        a.Begin().Prop("from", m.From).Prop("to", m.To).Prop("kind", m.Kind).Prop("label", m.Label).Prop("step", m.Step).Prop("type", m.Type);
                        if (m.Attempt is { } attempt)
                        {
                            a.Prop("attempt", attempt);
                        }

                        a.End();
                    });
                    w.End();
                });
            });
        }

        // ------------------------------------------------------------------ requests

        private static string Run(string request, Func<JsonValue, string> handle)
        {
            try
            {
                JsonValue parsed = JsonLite.Parse(string.IsNullOrWhiteSpace(request) ? "{}" : request);
                if (!parsed.IsObject)
                {
                    throw new ApiException("The request must be a JSON object.");
                }

                return handle(parsed);
            }
            catch (WorkflowUnreadableException e)
            {
                return Error(e.Message, e.Diagnostics);
            }
            catch (Exception e) when (e is ApiException or FormatException or ArgumentException or InvalidOperationException)
            {
                return Error(e.Message, null);
            }
        }

        private static string Text(JsonValue request, string name)
        {
            return request[name]?.AsString ?? throw new ApiException("'" + name + "' is required.");
        }

        private static ReadResult Read(string text)
        {
            lock (Gate)
            {
                if (_lastRead is { } last && string.Equals(last.Text, text, StringComparison.Ordinal))
                {
                    return last.Read;
                }
            }

            ReadResult read = WorkflowReader.Read(text);
            lock (Gate)
            {
                _lastRead = (text, read);
            }

            return read;
        }

        // The workflow of an operation that needs a readable one (simulate, paths, exports, specs).
        private static WorkflowDefinition Workflow(JsonValue request)
        {
            ReadResult read = Read(Text(request, "yaml"));
            if (read.Workflow is null || read.Diagnostics.Any(d => d.IsError))
            {
                throw new WorkflowUnreadableException(read.Diagnostics);
            }

            return read.Workflow;
        }

        private static WorkflowEnvironment Environment(JsonValue request)
        {
            string[] texts = (request["manifests"]?.Items ?? Array.Empty<JsonValue>()).Select(m => m.AsString ?? throw new ApiException("'manifests' holds manifest JSON strings.")).ToArray();
            string key = string.Join("\u0001", texts);
            lock (Gate)
            {
                if (_lastManifests is { } last && string.Equals(last.Key, key, StringComparison.Ordinal))
                {
                    return new WorkflowEnvironment(last.Manifests);
                }
            }

            IReadOnlyList<ServiceManifest> manifests = texts.Select(ServiceManifest.Parse).ToArray();
            lock (Gate)
            {
                _lastManifests = (key, manifests);
            }

            return new WorkflowEnvironment(manifests);
        }

        private static PathOptions PathOptions(JsonValue request)
        {
            var options = new PathOptions();
            if (request["maxStepEntries"]?.AsInteger is { } entries)
            {
                options.MaxStepEntries = (int)entries;
            }

            if (request["maxPaths"]?.AsInteger is { } paths)
            {
                options.MaxPaths = (int)paths;
            }

            if (request["maxMoves"]?.AsInteger is { } moves)
            {
                options.MaxMoves = (int)moves;
            }

            return options;
        }

        private static Func<string, bool?>? Guards(JsonValue? guards)
        {
            if (guards is not { IsObject: true })
            {
                return null;
            }

            var forced = guards.Members.Where(m => m.Value.AsBoolean is not null).ToDictionary(m => m.Key, m => m.Value.AsBoolean!.Value, StringComparer.Ordinal);
            return key => forced.TryGetValue(key, out bool value) ? value : null;
        }

        private static Dictionary<string, object?> Data(JsonValue? data)
        {
            return SimulationStateJson.FromJson(data) as Dictionary<string, object?> ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        private static DateTimeOffset? Time(string? text)
        {
            if (text is null)
            {
                return null;
            }

            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset time)
                ? time
                : throw new ApiException("'" + text + "' is not an ISO-8601 time.");
        }

        private static SimulationInput Input(string kind, JsonValue input)
        {
            string Required(string name) => input[name]?.AsString ?? throw new ApiException("'input." + name + "' is required for " + kind + ".");
            switch (kind)
            {
                case "succeed":
                    return SimulationInput.Succeed(Data(input["data"]));
                case "fail":
                    return SimulationInput.FailWith(Required("event"), Data(input["data"]));
                case "reject":
                    return SimulationInput.Reject(Required("code"), input["message"]?.AsString);
                case "rejected":
                    return SimulationInput.RejectedFor(Required("command"), Required("code"), input["message"]?.AsString);
                case "receive":
                    return SimulationInput.Receive(Required("type"), Data(input["data"]));
                case "timer":
                    return input["timer"]?.AsString is { } timerKind
                        ? SimulationInput.Timer(timerKind, Required("step"), (int)(input["attempt"]?.AsInteger ?? 1))
                        : SimulationInput.FireTimer();
                case "resume":
                    return SimulationInput.Resume(input["step"]?.AsString);
                case "retry-step":
                    return SimulationInput.RetryStep();
                case "abort":
                    return SimulationInput.Abort(input["reason"]?.AsString ?? "aborted in the simulator", input["compensate"]?.AsBoolean ?? false);
                default:
                    throw new ApiException("Unknown input kind '" + kind + "'.");
            }
        }

        // ------------------------------------------------------------------ responses

        private static string Ok(Action<WorkflowJson.Writer> result)
        {
            var w = new WorkflowJson.Writer();
            w.Begin().Key("ok").Raw("true");
            w.Key("result");
            result(w);
            return w.End().ToString();
        }

        private static string Text(string text)
        {
            return Ok(w => w.Begin().Prop("text", text).End());
        }

        private static string Error(string message, IReadOnlyList<WorkflowDiagnostic>? diagnostics)
        {
            var w = new WorkflowJson.Writer();
            w.Begin().Key("ok").Raw("false");
            w.Prop("error", message);
            if (diagnostics is not null)
            {
                w.Key("diagnostics");
                Diagnostics(w, diagnostics);
            }

            return w.End().ToString();
        }

        private static void Diagnostics(WorkflowJson.Writer w, IEnumerable<WorkflowDiagnostic> diagnostics)
        {
            w.Array(diagnostics, (a, d) => a.Begin()
                .Prop("id", d.Id)
                .Prop("severity", d.IsError ? "error" : "warning")
                .Prop("line", d.Location.Line)
                .Prop("column", d.Location.Column)
                .Prop("message", d.Message)
                .End());
        }

        private static void Simulation(WorkflowJson.Writer w, SimulationResult result)
        {
            SimulationState s = result.State;
            w.Begin()
                .Prop("state", SimulationStateJson.Write(s))
                .Prop("instance", s.Instance)
                .Prop("status", s.Status.ToString())
                .Prop("step", s.Step)
                .Prop("phase", s.Phase.ToString())
                .Prop("attempt", s.Attempt)
                .Prop("now", s.Now.ToString("O", CultureInfo.InvariantCulture));
            w.Key("terminal").Raw(s.IsTerminal ? "true" : "false");
            if (s.Timer is { } timer)
            {
                w.Key("timer");
                Timer(w, timer);
            }

            if (s.Failure is { } failure)
            {
                w.Key("failure").Begin().Prop("code", failure.Code).Prop("message", failure.Message).Prop("step", failure.Step).End();
            }

            w.Key("compensationStack").Array(s.CompensationStack, (a, step) => a.Value(step));
            w.Key("data").Raw(SimulationStateJson.ToJson(s.Data));
            w.Key("effects").Array(result.Effects, (a, e) =>
            {
                a.Begin().Prop("kind", e.Kind).Prop("text", e.Text);
                if (e.Command is { } command)
                {
                    a.Key("command").Begin().Prop("type", command.Command).Prop("messageId", command.MessageId.ToString("D"));
                    a.Key("fields").Begin();
                    foreach (KeyValuePair<string, object?> field in command.Fields)
                    {
                        a.Key(field.Key).Raw(SimulationStateJson.ToJson(field.Value));
                    }

                    a.End().End();
                }

                if (e.Timer is { } t)
                {
                    a.Key("timer");
                    Timer(a, t);
                }

                a.End();
            });
            w.Key("transitions").Array(result.Transitions, (a, t) => a.Begin().Prop("kind", t.Kind).Prop("step", t.Step).Prop("attempt", t.Attempt).End());
            w.End();
        }

        private static void Timer(WorkflowJson.Writer w, SimulationTimer timer)
        {
            w.Begin().Prop("kind", timer.Kind).Prop("step", timer.Step).Prop("attempt", timer.Attempt).Prop("due", timer.Due.ToString("O", CultureInfo.InvariantCulture)).End();
        }

        private sealed class ApiException : Exception
        {
            public ApiException(string message)
                : base(message)
            {
            }
        }

        private sealed class WorkflowUnreadableException : Exception
        {
            public WorkflowUnreadableException(IReadOnlyList<WorkflowDiagnostic> diagnostics)
                : base("The workflow has errors: fix them before simulating, enumerating or exporting it.")
            {
                Diagnostics = diagnostics;
            }

            public IReadOnlyList<WorkflowDiagnostic> Diagnostics { get; }
        }
    }
}

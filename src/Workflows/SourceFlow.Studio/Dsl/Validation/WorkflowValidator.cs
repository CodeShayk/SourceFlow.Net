using System;
using System.Collections.Generic;
using System.Linq;
using SourceFlow.Studio.Dsl.Diagnostics;
using SourceFlow.Studio.Dsl.Expressions;
using SourceFlow.Studio.Dsl.Manifests;
using SourceFlow.Studio.Dsl.Model;

namespace SourceFlow.Studio.Dsl.Validation
{
    /// <summary>
    /// The validation rules WF-V01…V16 (doc 15 §5.4) over a read workflow and the domain's manifests. One
    /// implementation for Studio, the CLI and the generator (WFD-NFR-2).
    /// </summary>
    public static class WorkflowValidator
    {
        /// <summary>Validates <paramref name="workflow"/>; diagnostics are sorted by position, then id.</summary>
        public static IReadOnlyList<WorkflowDiagnostic> Validate(WorkflowDefinition workflow, WorkflowEnvironment environment)
        {
            if (workflow is null)
            {
                throw new ArgumentNullException(nameof(workflow));
            }

            if (environment is null)
            {
                throw new ArgumentNullException(nameof(environment));
            }

            var run = new Run(workflow, environment);
            run.All();
            return run.Diagnostics
                .Distinct()
                .OrderBy(d => d.Location.Line)
                .ThenBy(d => d.Location.Column)
                .ThenBy(d => d.Id, StringComparer.Ordinal)
                .ThenBy(d => d.Message, StringComparer.Ordinal)
                .ToList();
        }

        private sealed class Run
        {
            private readonly WorkflowDefinition _workflow;
            private readonly WorkflowEnvironment _env;
            private readonly ServiceManifest? _owner;
            private readonly Dictionary<string, string> _dataTypes = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Dictionary<string, bool> _dataPersonal = new Dictionary<string, bool>(StringComparer.Ordinal);
            private readonly string? _correlationField;

            public Run(WorkflowDefinition workflow, WorkflowEnvironment environment)
            {
                _workflow = workflow;
                _env = environment;
                _owner = environment.Service(workflow.Owner)
                    ?? (environment.CompilingService == workflow.Owner || environment.Partial ? environment.Manifests.FirstOrDefault(m => m.Service is null) : null);
                int dot = workflow.Start.Correlate.LastIndexOf('.');
                _correlationField = dot >= 0 ? workflow.Start.Correlate.Substring(dot + 1) : null;
            }

            public List<WorkflowDiagnostic> Diagnostics { get; } = new List<WorkflowDiagnostic>();

            private bool Partial => _env.Partial;

            public void All()
            {
                Owner();
                Start();
                Data();
                for (int i = 0; i < _workflow.Steps.Count; i++)
                {
                    Step(_workflow.Steps[i], i);
                }

                Terminal();
                Reachability();
                Versions();
            }

            private void Report(string id, string message, SourceLocation at)
            {
                Diagnostics.Add(new WorkflowDiagnostic(id, WorkflowRules.SeverityOf(id), message, at));
            }

            // ------------------------------------------------------------ owner and start

            private void Owner()
            {
                if (_workflow.Owner.Length == 0)
                {
                    return;
                }

                if (_env.CompilingService is { } compiling && compiling != _workflow.Owner)
                {
                    Report(WorkflowRules.CommandOwner, "The workflow's owner is '" + _workflow.Owner + "' but it is compiled by '" + compiling + "'; workflows live in, and are generated into, their owner (ADR-007).", _workflow.At("owner"));
                }
                else if (_owner is null && !Partial)
                {
                    Report(WorkflowRules.CommandOwner, "The owner '" + _workflow.Owner + "' is not a service of the workspace.", _workflow.At("owner"));
                }
            }

            private void Start()
            {
                StartSpec start = _workflow.Start;
                if (start.On.Length == 0)
                {
                    return;
                }

                ManifestMessage? message = Known(start.On, start.At("on"), "event", "start.on");
                if (message is null)
                {
                    return;
                }

                if (!Deliverable(start.On, out string why))
                {
                    Report(WorkflowRules.Unreachable, "The owner never receives the start message " + start.On + ": " + why + ".", start.At("on"));
                }

                if (_correlationField is { } key && message.Field(key) is null)
                {
                    Report(WorkflowRules.Correlation, "'start.correlate' reads '" + key + "', which " + start.On + " doesn't have.", start.At("correlate"));
                }

                if (start.When is { } when)
                {
                    Guard(when, start.At("when"), path => Event(path, message), "event.data.* of " + start.On);
                }
            }

            private void Data()
            {
                ManifestMessage? startMessage = _env.Message(_workflow.Start.On);
                foreach (DataField field in _workflow.Data)
                {
                    if (field.From is { } from)
                    {
                        string name = from.Substring("start.data.".Length);
                        ManifestField? source = startMessage?.Field(name.Split('.')[0]);
                        if (startMessage is not null && source is null)
                        {
                            Report(WorkflowRules.Map, "Data field '" + field.Name + "' reads '" + name + "', which " + _workflow.Start.On + " doesn't have.", field.At("from"));
                        }

                        _dataTypes[field.Name] = source is not null && name.IndexOf('.') < 0 ? source.Type : "object";
                        if (source is { PersonalData: true } && !field.Personal)
                        {
                            Report(WorkflowRules.PersonalData, "Data field '" + field.Name + "' copies personal data (" + _workflow.Start.On + "." + name + "); mark it 'personal: true', or don't keep it.", field.Location);
                        }
                    }
                    else
                    {
                        _dataTypes[field.Name] = field.Type ?? "object";
                    }

                    _dataPersonal[field.Name] = field.Personal;
                }

                // Captures add data fields typed from their event.
                foreach (WorkflowStep step in _workflow.Steps)
                {
                    if (step.Await?.Success is not { } success || _env.Message(success.On) is not { } message)
                    {
                        continue;
                    }

                    foreach (CaptureSpec capture in success.Capture)
                    {
                        string name = capture.Path.StartsWith("event.data.", StringComparison.Ordinal) ? capture.Path.Substring("event.data.".Length) : capture.Path;
                        ManifestField? source = message.Field(name.Split('.')[0]);
                        if (source is null)
                        {
                            Report(WorkflowRules.Map, "The capture '" + capture.Field + "' reads '" + name + "', which " + success.On + " doesn't have.", capture.At("path"));
                            continue;
                        }

                        string type = name.IndexOf('.') < 0 ? source.Type : "object";
                        if (_dataTypes.TryGetValue(capture.Field, out string? existing) && !Compatible(existing, type))
                        {
                            Report(WorkflowRules.Map, "The capture '" + capture.Field + "' is " + type + " but data field '" + capture.Field + "' is " + existing + ".", capture.At("path"));
                        }
                        else if (!_dataTypes.ContainsKey(capture.Field))
                        {
                            _dataTypes[capture.Field] = type;
                        }

                        if (source.PersonalData && !(_dataPersonal.TryGetValue(capture.Field, out bool personal) && personal))
                        {
                            Report(WorkflowRules.PersonalData, "The capture '" + capture.Field + "' copies personal data (" + success.On + "." + name + "); declare it in 'data' with 'personal: true'.", capture.Location);
                        }
                    }
                }
            }

            // ------------------------------------------------------------ steps

            private void Step(WorkflowStep step, int index)
            {
                int pivot = _workflow.PivotIndex;
                bool afterPivot = pivot >= 0 && index >= pivot;
                if (step.Pivot && pivot != index)
                {
                    Report(WorkflowRules.Pivot, "A workflow has one pivot; '" + _workflow.Steps[pivot].Id + "' is already the pivot.", step.At("pivot"));
                }

                CommandOwner? owner = null;
                if (step.Send is { } send)
                {
                    owner = Send(send, index == 0 ? _env.Message(_workflow.Start.On) : null, compensated: false);
                }

                if (step.When is { } when)
                {
                    Guard(when, step.At("when"), path => DataPath(path), "$data.*");
                }

                if (step.Await is { } wait)
                {
                    Success(step, wait.Success, step.Send, owner);
                    foreach (FailureSpec failure in wait.Failures)
                    {
                        Failure(step, failure, owner, afterPivot);
                    }
                }

                // WF-V06: every wait has a timeout.
                if (step.Timeout is null)
                {
                    Report(WorkflowRules.Timeout, "Step '" + step.Id + "' waits without a timeout; add 'timeout: { after: PT…, then: … }', or 'timeout: { none: true, reason: … }'.", step.Location);
                }
                else
                {
                    Timeout(step.Timeout, "step '" + step.Id + "'");
                    if (afterPivot && step.Timeout.Then.Kind == ThenKind.Fail && !step.Timeout.None)
                    {
                        Report(WorkflowRules.Pivot, "Step '" + step.Id + "' is at or after the pivot, so its timeout can't 'fail' (no compensation after the point of no return); use manual or goto.", step.Timeout.Then.Location.Line > 0 ? step.Timeout.Then.Location : step.At("timeout"));
                    }

                    GotoTarget(step.Timeout.Then);
                }

                if (step.Next is { } next && next != WorkflowStep.CompleteKeyword && _workflow.Step(next) is null)
                {
                    Report(WorkflowRules.Goto, "'next: " + next + "' names no step.", step.At("next"));
                }

                Compensation(step, owner, afterPivot);
            }

            private CommandOwner? Send(SendSpec send, ManifestMessage? startEvent, bool compensated)
            {
                if (send.Command.Length == 0)
                {
                    return null;
                }

                ManifestMessage? command = Known(send.Command, send.At("command"), "command", "send.command");
                CommandOwner? owner = null;
                if (command is not null)
                {
                    IReadOnlyList<CommandOwner> owners = _env.Owners(send.Command);
                    if (owners.Count == 0)
                    {
                        if (!Partial)
                        {
                            Report(WorkflowRules.CommandOwner, "No service of the workspace owns " + send.Command + ".", send.At("command"));
                        }
                    }
                    else if (owners.Count > 1)
                    {
                        Report(WorkflowRules.CommandOwner, send.Command + " is owned by " + string.Join(" and ", owners.Select(o => o.Name ?? o.Aggregate.Name)) + "; a command has exactly one owner (ADR-007).", send.At("command"));
                    }
                    else
                    {
                        owner = owners[0];
                        if (!IsLocal(owner) && Route(send.Command) is not { } route)
                        {
                            Report(WorkflowRules.CommandOwner, _workflow.Owner + " doesn't route " + send.Command + " to its owner " + owner.Name + "; add routing.Command<…>().To(\"" + (owner.Service.Endpoint ?? owner.Name) + "\").", send.At("command"));
                        }
                        else if (!IsLocal(owner) && Route(send.Command) is { } r && owner.Service.Endpoint is { } endpoint && r.To != endpoint && r.To != owner.Name)
                        {
                            Report(WorkflowRules.CommandOwner, _workflow.Owner + " routes " + send.Command + " to '" + r.To + "', but its owner " + owner.Name + " listens on '" + endpoint + "'.", send.At("command"));
                        }
                    }
                }

                Map(send, command, startEvent, compensated);
                return owner;
            }

            private void Map(SendSpec send, ManifestMessage? command, ManifestMessage? startEvent, bool compensatedHandler)
            {
                foreach (MapEntry entry in send.Map)
                {
                    string? source = ValueType(entry, startEvent, compensatedHandler);
                    if (command is null)
                    {
                        continue;
                    }

                    ManifestField? target = command.Field(entry.Field);
                    if (target is null)
                    {
                        Report(WorkflowRules.Map, send.Command + " has no field '" + entry.Field + "'.", entry.Location);
                    }
                    else if (source is not null && !Assignable(target, source, entry.Value))
                    {
                        Report(WorkflowRules.Map, "'" + entry.Field + "' of " + send.Command + " is " + target.Type + (target.Nullable ? " (nullable)" : string.Empty) + "; the mapped value is " + source + ".", entry.At("value"));
                    }
                }

                if (command is null)
                {
                    return;
                }

                foreach (ManifestField field in command.Fields)
                {
                    if (!field.Nullable && !send.Map.Any(e => e.Field == field.Name))
                    {
                        Report(WorkflowRules.Map, send.Command + " needs '" + field.Name + "'; map it.", send.At("map"));
                    }
                }
            }

            // The type of a mapped value, or null when unknown (hooks, errors already reported).
            private string? ValueType(MapEntry entry, ManifestMessage? startEvent, bool compensatedHandler)
            {
                MapValue value = entry.Value;
                switch (value.Kind)
                {
                    case MapValueKind.Hook:
                        return null;
                    case MapValueKind.Literal:
                        return value.Literal!.Kind switch
                        {
                            ScalarKind.Null => "null",
                            ScalarKind.String => "string",
                            ScalarKind.Integer => "integer",
                            ScalarKind.Number => "number",
                            _ => "boolean",
                        };
                }

                string path = value.Text!;
                if (path.StartsWith("$data.", StringComparison.Ordinal))
                {
                    string? type = DataPath(path);
                    if (type is null)
                    {
                        Report(WorkflowRules.Map, "There is no data field '" + path.Substring(6) + "'; declare it in 'data' or capture it.", entry.At("value"));
                    }

                    return type;
                }

                if (path.StartsWith("$failure.", StringComparison.Ordinal))
                {
                    if (!compensatedHandler)
                    {
                        Report(WorkflowRules.Map, "'" + path + "' is only available in on.compensated.", entry.At("value"));
                        return null;
                    }

                    string member = path.Substring(9);
                    if (member != "code" && member != "message" && member != "step")
                    {
                        Report(WorkflowRules.Map, "'$failure' has code, message and step.", entry.At("value"));
                        return null;
                    }

                    return "string";
                }

                // event.data.x: only the start event, in the first step.
                if (startEvent is null)
                {
                    Report(WorkflowRules.Map, "'" + path + "' reads an event, which is only the start event in the first step; capture the value into data and use $data.", entry.At("value"));
                    return null;
                }

                string? eventType = Event(path, startEvent);
                if (eventType is null)
                {
                    Report(WorkflowRules.Map, startEvent.Ref + " has no field '" + path.Substring("event.data.".Length) + "'.", entry.At("value"));
                }

                return eventType;
            }

            private void Success(WorkflowStep step, SuccessSpec success, SendSpec? send, CommandOwner? owner)
            {
                if (success.On.Length == 0)
                {
                    return;
                }

                ManifestMessage? message = Known(success.On, success.At("on"), "event", "await.success");
                if (message is null)
                {
                    return;
                }

                Arrives(success.On, success.At("on"), send, owner, "step '" + step.Id + "'");
                if (send is null && _correlationField is { } key && message.Field(key) is null)
                {
                    Report(WorkflowRules.Correlation, "Step '" + step.Id + "' awaits " + success.On + " without sending a command, so it must carry the instance key '" + key + "'; it doesn't.", success.At("on"));
                }

                if (success.When is { } when)
                {
                    Guard(when, success.At("when"), path => path.StartsWith("event.", StringComparison.Ordinal) ? Event(path, message) : DataPath(path), "$data.* and event.data.* of " + success.On);
                }
            }

            private void Failure(WorkflowStep step, FailureSpec failure, CommandOwner? owner, bool afterPivot)
            {
                if (failure.Rejected is { } code)
                {
                    if (step.Send is null)
                    {
                        Report(WorkflowRules.Rejection, "'rejected: " + code + "' needs a command; step '" + step.Id + "' sends none.", failure.At("rejected"));
                    }
                    else if (owner is not null)
                    {
                        if (!owner.Command.Rejects.Contains(code))
                        {
                            Report(WorkflowRules.Rejection, step.Send.Command + " never rejects with '" + code + "'" + (owner.Command.Rejects.Count > 0 ? " (it rejects with " + string.Join(", ", owner.Command.Rejects) + ")" : string.Empty) + ".", failure.At("rejected"));
                        }

                        if (!IsLocal(owner) && Route(step.Send.Command) is { ReplyOnReject: false })
                        {
                            Report(WorkflowRules.Rejection, _workflow.Owner + " doesn't receive rejections of " + step.Send.Command + "; add .ReplyOnReject() to its route.", failure.At("rejected"));
                        }
                    }
                }

                if (failure.On is { } on && Known(on, failure.At("on"), "event", "failure.on") is { } message)
                {
                    Arrives(on, failure.At("on"), null, null, "step '" + step.Id + "'");
                    if (step.Send is null && _correlationField is { } key && message.Field(key) is null)
                    {
                        Report(WorkflowRules.Correlation, on + " can't be correlated to the instance: it has no '" + key + "'.", failure.At("on"));
                    }
                }

                if (failure.Retry is { } retry)
                {
                    Retry(retry);
                }

                if (afterPivot && failure.Then.Kind == ThenKind.Fail)
                {
                    Report(WorkflowRules.Pivot, "Step '" + step.Id + "' is at or after the pivot, so a failure can't 'fail' (no compensation after the point of no return); use manual, complete or goto.", failure.Then.Location.Line > 0 ? failure.Then.Location : failure.Location);
                }

                GotoTarget(failure.Then);
            }

            private void Compensation(WorkflowStep step, CommandOwner? owner, bool afterPivot)
            {
                CompensateSpec? compensate = step.Compensate;
                bool remote = owner is not null && !IsLocal(owner);
                if (compensate is null || compensate.None)
                {
                    if (compensate is { None: true, Reason: null } && !afterPivot)
                    {
                        Report(WorkflowRules.MissingCompensation, "'compensate: none' needs a 'reason'.", step.At("compensate"));
                    }
                    else if (compensate is null && remote && !afterPivot)
                    {
                        Report(WorkflowRules.MissingCompensation, "Step '" + step.Id + "' changes state in " + owner!.Name + " before the pivot but has no compensation; add 'compensate', or 'compensate: { none: true, reason: … }'.", step.Location);
                    }

                    return;
                }

                if (afterPivot)
                {
                    Report(WorkflowRules.Pivot, "Step '" + step.Id + "' is at or after the pivot, so it can't be compensated; recover forward instead.", step.At("compensate"));
                }

                CommandOwner? compensator = compensate.Send is { } send ? Send(send, null, compensated: false) : null;
                if (compensate.Await is { } wait && wait.Success.On.Length > 0 && Known(wait.Success.On, wait.Success.At("on"), "event", "compensate.await.success") is not null)
                {
                    Arrives(wait.Success.On, wait.Success.At("on"), compensate.Send, compensator, "the compensation of '" + step.Id + "'");
                }

                if (compensate.Retry is { } retry)
                {
                    Retry(retry);
                }

                if (compensate.Timeout is null)
                {
                    Report(WorkflowRules.Timeout, "The compensation of '" + step.Id + "' waits without a timeout; add 'timeout: { after: PT… }'.", step.At("compensate"));
                }
                else
                {
                    Timeout(compensate.Timeout, "the compensation of '" + step.Id + "'");
                }
            }

            private void Timeout(TimeoutSpec timeout, string what)
            {
                if (timeout.None && timeout.Reason is null)
                {
                    Report(WorkflowRules.Timeout, "'timeout: none' on " + what + " needs a 'reason'.", timeout.Location);
                }

                if (timeout.After is { } after && after <= TimeSpan.Zero)
                {
                    Report(WorkflowRules.Timeout, "The timeout of " + what + " must be longer than zero.", timeout.At("after"));
                }

                if (timeout.Retry is { } retry)
                {
                    Retry(retry);
                }
            }

            private void Retry(RetrySpec retry)
            {
                if (retry.Max < 1 || retry.Max > 20)
                {
                    Report(WorkflowRules.Retry, "'max' is between 1 and 20 (it is " + retry.Max + "); retries are bounded.", retry.At("max"));
                }

                if (retry.Backoff == Backoff.Exponential && retry.Initial <= TimeSpan.Zero)
                {
                    Report(WorkflowRules.Retry, "Exponential backoff needs an 'initial' delay longer than zero.", retry.Location);
                }

                if (retry.Cap is { } cap && cap < retry.Initial)
                {
                    Report(WorkflowRules.Retry, "'cap' is shorter than 'initial'.", retry.At("cap"));
                }
            }

            private void GotoTarget(ThenSpec then)
            {
                if (then.Kind == ThenKind.Goto && then.Target is { Length: > 0 } target && _workflow.Step(target) is null)
                {
                    Report(WorkflowRules.Goto, "'goto: " + target + "' names no step.", then.Location);
                }
            }

            private void Terminal()
            {
                if (_workflow.On.Completed is { } completed)
                {
                    Send(completed, null, compensated: false);
                }

                if (_workflow.On.Compensated is { } compensated)
                {
                    Send(compensated, null, compensated: true);
                }
            }

            // ------------------------------------------------------------ WF-V05 reachability

            private void Reachability()
            {
                IReadOnlyList<WorkflowStep> steps = _workflow.Steps;
                if (steps.Count == 0 || steps.Any(s => s.Id.Length == 0))
                {
                    return;
                }

                const int Completed = -1;
                var edges = new Dictionary<int, List<int>>();
                for (int i = 0; i < steps.Count; i++)
                {
                    var targets = new List<int>();
                    WorkflowStep step = steps[i];
                    int following = i + 1 < steps.Count ? i + 1 : Completed;
                    targets.Add(step.Next is { } next ? (next == WorkflowStep.CompleteKeyword ? Completed : Index(next)) : following);
                    if (step.When is not null)
                    {
                        targets.Add(following);   // skipped
                    }

                    foreach (ThenSpec then in Thens(step))
                    {
                        if (then.Kind == ThenKind.Goto && then.Target is { } target)
                        {
                            targets.Add(Index(target));
                        }
                        else if (then.Kind == ThenKind.Complete)
                        {
                            targets.Add(Completed);
                        }
                    }

                    edges[i] = targets.Where(t => t != int.MinValue).ToList();
                }

                var seen = new HashSet<int>();
                var queue = new Queue<int>();
                queue.Enqueue(0);
                seen.Add(0);
                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    if (current == Completed)
                    {
                        continue;
                    }

                    foreach (int target in edges[current])
                    {
                        if (seen.Add(target))
                        {
                            queue.Enqueue(target);
                        }
                    }
                }

                for (int i = 0; i < steps.Count; i++)
                {
                    if (!seen.Contains(i))
                    {
                        Report(WorkflowRules.Reachability, "Step '" + steps[i].Id + "' is never reached.", steps[i].Location);
                    }
                }

                if (!seen.Contains(Completed))
                {
                    Report(WorkflowRules.Reachability, "No path reaches Completed.", _workflow.At("steps"));
                }

                int Index(string id)
                {
                    for (int k = 0; k < steps.Count; k++)
                    {
                        if (steps[k].Id == id)
                        {
                            return k;
                        }
                    }

                    return int.MinValue;   // WF-V11 reports it
                }
            }

            private static IEnumerable<ThenSpec> Thens(WorkflowStep step)
            {
                if (step.Timeout is { None: false } timeout)
                {
                    yield return timeout.Then;
                }

                if (step.Await is { } wait)
                {
                    foreach (FailureSpec failure in wait.Failures)
                    {
                        yield return failure.Then;
                    }
                }
            }

            // ------------------------------------------------------------ WF-V15 versions

            private void Versions()
            {
                if (_workflow.Name.Length == 0 || _workflow.Version < 1)
                {
                    return;
                }

                foreach (WorkflowDefinition sibling in _env.Siblings)
                {
                    if (!ReferenceEquals(sibling, _workflow) && sibling.Owner == _workflow.Owner && sibling.Name == _workflow.Name && sibling.Version == _workflow.Version)
                    {
                        Report(WorkflowRules.Version, _workflow.ProcessName + " is declared twice in " + _workflow.Owner + ".", _workflow.At("version"));
                        break;
                    }
                }

                if (_owner?.Workflows.FirstOrDefault(w => w.Name == _workflow.Name && w.Version == _workflow.Version) is { Fingerprint: { } published }
                    && published != WorkflowFingerprint.Compute(_workflow))
                {
                    Report(WorkflowRules.Version, _workflow.ProcessName + " changed since it was published; in-flight instances run the published structure, so give the change 'version: " + (_workflow.Version + 1) + "' (doc 15 §5.6).", _workflow.At("version"));
                }
            }

            // ------------------------------------------------------------ helpers

            private ManifestMessage? Known(string reference, SourceLocation at, string expected, string where)
            {
                ManifestMessage? message = _env.Message(reference);
                if (message is null)
                {
                    if (!Partial || reference.Length > 0)
                    {
                        Report(WorkflowRules.UnknownType, reference + " (" + where + ") is not a message type of the workspace" + (Partial ? "; reference the owning service's contract package" : string.Empty) + ".", at);
                    }

                    return null;
                }

                bool ok = expected == "command" ? message.Kind == "command" : message.Kind == "event" || message.Kind == "integration-event";
                if (!ok)
                {
                    Report(WorkflowRules.UnknownType, reference + " is " + (message.Kind == "integration-event" ? "an integration event" : "a " + message.Kind) + ", but " + where + " needs " + (expected == "command" ? "a command" : "an event") + ".", at);
                    return null;
                }

                return message;
            }

            // WF-V03: an awaited event reaches the owner, and (after a command) comes from that command.
            private void Arrives(string reference, SourceLocation at, SendSpec? send, CommandOwner? owner, string what)
            {
                if (send is not null && owner is not null)
                {
                    bool emitted = owner.Command.Emits.Contains(reference);
                    bool published = owner.Service.Publishes.Any(p => p.As == reference && owner.Command.Emits.Contains(p.DomainEvent));
                    if (!emitted && !published)
                    {
                        Report(WorkflowRules.Unreachable, send.Command + " doesn't produce " + reference + " (" + what + ")" + (owner.Command.Emits.Count > 0 ? "; it produces " + string.Join(", ", owner.Command.Emits) : string.Empty) + ".", at);
                        return;
                    }

                    if (IsLocal(owner))
                    {
                        return;
                    }
                }
                else if (send is not null && Partial)
                {
                    return;   // the command's owner is not known at compile time
                }

                if (!Deliverable(reference, out string why))
                {
                    Report(WorkflowRules.Unreachable, _workflow.Owner + " never receives " + reference + " (" + what + "): " + why + ".", at);
                }
            }

            private bool Deliverable(string reference, out string why)
            {
                why = string.Empty;
                if (_owner is null)
                {
                    return true;   // reported by Owner()
                }

                if (_owner.Aggregates.Any(a => a.Events.Contains(reference) || a.Commands.Any(c => c.Emits.Contains(reference))))
                {
                    return true;   // a domain event of the owner
                }

                var publishers = _env.Publications(reference).ToList();
                if (publishers.Count == 0)
                {
                    if (Partial)
                    {
                        return true;
                    }

                    why = "it is not a domain event of " + _workflow.Owner + " and no service publishes it";
                    return false;
                }

                foreach ((ServiceManifest _, ManifestPublication publication) in publishers)
                {
                    if (_owner.Subscribes.Any(s => s.Topic == publication.Topic && (s.Only.Count == 0 || s.Only.Contains(reference))))
                    {
                        return true;
                    }
                }

                why = "it is published to " + string.Join(", ", publishers.Select(p => p.Publication.Topic).Distinct()) + ", which " + _workflow.Owner + " doesn't subscribe to (or filters out)";
                return false;
            }

            private bool IsLocal(CommandOwner owner)
            {
                // Without an owner (reported by the reader) nothing is "remote": no routing or compensation noise.
                return _workflow.Owner.Length == 0 || owner.Name == _workflow.Owner || (owner.Name is null && (ReferenceEquals(owner.Service, _owner) || Partial));
            }

            private ManifestCommandRoute? Route(string command)
            {
                return _owner?.CommandRoutes.FirstOrDefault(r => r.Command == command);
            }

            private string? DataPath(string path)
            {
                if (!path.StartsWith("$data.", StringComparison.Ordinal))
                {
                    return null;
                }

                string name = path.Substring(6);
                if (name.IndexOf('.') >= 0)
                {
                    return _dataTypes.TryGetValue(name.Split('.')[0], out string? parent) && parent == "object" ? "object" : null;
                }

                return _dataTypes.TryGetValue(name, out string? type) ? type : null;
            }

            private static string? Event(string path, ManifestMessage message)
            {
                if (!path.StartsWith("event.data.", StringComparison.Ordinal))
                {
                    return null;
                }

                string name = path.Substring("event.data.".Length);
                ManifestField? field = message.Field(name.Split('.')[0]);
                return field is null ? null : name.IndexOf('.') < 0 ? field.Type : "object";
            }

            private void Guard(string text, SourceLocation at, Func<string, string?> resolve, string scope)
            {
                GuardParseResult parsed = GuardParser.Parse(text);
                if (parsed.Expression is null)
                {
                    Report(WorkflowRules.Guard, "The guard '" + text + "' doesn't parse: " + parsed.Error + " (at character " + (parsed.ErrorPosition + 1) + ").", at);
                    return;
                }

                if (GuardBinder.Bind(parsed.Expression, resolve, scope) is { } error)
                {
                    Report(WorkflowRules.Guard, "The guard '" + text + "' doesn't bind: " + error + ".", at);
                }
            }

            private static bool Compatible(string a, string b)
            {
                return a == b || (Numeric(a) && Numeric(b)) || a == "object" || b == "object";
            }

            private static bool Numeric(string type)
            {
                return type == "integer" || type == "number";
            }

            private static bool Assignable(ManifestField target, string source, MapValue value)
            {
                if (source == "null")
                {
                    return target.Nullable;
                }

                if (target.Type == source || target.Type == "object" || source == "object")
                {
                    return true;
                }

                switch (target.Type)
                {
                    case "number":
                        return source == "integer";
                    case "integer":
                        return false;
                    case "string":
                        return source == "uuid" || source == "date-time" || source == "date" || source == "time" || source == "uri" || source == "duration";
                    case "uuid":
                        return source == "string" && (value.Kind != MapValueKind.Literal || Guid.TryParse(value.Literal!.Text, out _));
                    case "date-time":
                    case "date":
                    case "time":
                    case "uri":
                    case "duration":
                        return source == "string";
                    default:
                        return false;
                }
            }
        }
    }
}

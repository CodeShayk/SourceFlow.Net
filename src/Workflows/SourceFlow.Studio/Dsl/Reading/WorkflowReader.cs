using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using SourceFlow.Studio.Dsl.Diagnostics;
using SourceFlow.Studio.Dsl.Model;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SourceFlow.Studio.Dsl.Reading
{
    /// <summary>The result of reading a workflow file: the model (null when the file can't be read at all) and the structural diagnostics.</summary>
    public sealed class ReadResult
    {
        internal ReadResult(WorkflowDefinition? workflow, IReadOnlyList<WorkflowDiagnostic> diagnostics)
        {
            Workflow = workflow;
            Diagnostics = diagnostics;
        }

        /// <summary>The workflow, or null when the YAML is invalid or not a mapping.</summary>
        public WorkflowDefinition? Workflow { get; }

        /// <summary>Structural diagnostics (<c>WF-Sxx</c>), in file order.</summary>
        public IReadOnlyList<WorkflowDiagnostic> Diagnostics { get; }

        /// <summary>Whether there is a structural error.</summary>
        public bool HasErrors
        {
            get
            {
                foreach (WorkflowDiagnostic diagnostic in Diagnostics)
                {
                    if (diagnostic.IsError)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    /// <summary>
    /// Reads <c>*.sfflow.yaml</c> (DSL <c>sourceflow.workflow: 1</c>, doc 15 §5.2) into the model with source positions.
    /// Unknown keys, wrong shapes, anchors and aliases are reported, not thrown; the reader keeps going so one pass
    /// shows every structural problem. <c>schemas/workflow-1.json</c> describes the same structure.
    /// </summary>
    public static class WorkflowReader
    {
        private static readonly Regex Kebab = new Regex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
        private static readonly Regex ServiceName = new Regex("^[a-z][a-z0-9]*([.-][a-z0-9]+)*$", RegexOptions.CultureInvariant);
        private static readonly Regex MessageReference = new Regex("^[a-z0-9]+([._-][a-z0-9]+)*\\.v[1-9][0-9]*$", RegexOptions.CultureInvariant);
        private static readonly Regex Code = new Regex("^[a-z0-9]+([._-][a-z0-9]+)*$", RegexOptions.CultureInvariant);
        private static readonly Regex FieldName = new Regex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
        private static readonly Regex HookName = new Regex("^[A-Z][A-Za-z0-9]*$", RegexOptions.CultureInvariant);
        private static readonly Regex Segments = new Regex("^[A-Za-z_][A-Za-z0-9_]*(\\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.CultureInvariant);
        private static readonly Regex Integer = new Regex("^[-+]?[0-9]+$", RegexOptions.CultureInvariant);
        private static readonly Regex Float = new Regex("^[-+]?(\\.[0-9]+|[0-9]+(\\.[0-9]*)?)([eE][-+]?[0-9]+)?$", RegexOptions.CultureInvariant);

        /// <summary>The data types a typed data field can declare (the manifest field types).</summary>
        public static readonly IReadOnlyList<string> DataTypes = new[] { "string", "integer", "number", "boolean", "uuid", "date-time", "date", "time", "duration", "uri", "array", "object" };

        /// <summary>Reads a workflow.</summary>
        public static ReadResult Read(string text)
        {
            if (text is null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            var context = new Context();
            var stream = new YamlStream();
            try
            {
                stream.Load(new StringReader(text));
            }
            catch (YamlException ex)
            {
                context.Report(WorkflowRules.Syntax, "Invalid YAML: " + Clean(ex), new SourceLocation((int)ex.Start.Line, (int)ex.Start.Column));
                return new ReadResult(null, context.Diagnostics);
            }

            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                SourceLocation at = stream.Documents.Count > 0 ? Loc(stream.Documents[0].RootNode) : new SourceLocation(1, 1);
                context.Report(WorkflowRules.Syntax, "A workflow file holds one YAML mapping (sourceflow.workflow, name, version, owner, start, steps…).", at);
                return new ReadResult(null, context.Diagnostics);
            }

            CheckAnchors(context, root, new HashSet<string>(StringComparer.Ordinal));
            WorkflowDefinition workflow = context.Workflow(root);
            return new ReadResult(workflow, context.Diagnostics);
        }

        // An alias resolves to its anchor's node, so each anchor is reported once.
        private static void CheckAnchors(Context context, YamlNode node, HashSet<string> reported)
        {
            if (!node.Anchor.IsEmpty)
            {
                if (reported.Add(node.Anchor.Value))
                {
                    context.Report(WorkflowRules.Syntax, "Anchors and aliases are not allowed in workflow files; repeat the value instead (it keeps visual edits lossless).", Loc(node));
                }

                return;
            }

            switch (node)
            {
                case YamlMappingNode mapping:
                    foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
                    {
                        CheckAnchors(context, pair.Key, reported);
                        CheckAnchors(context, pair.Value, reported);
                    }

                    break;
                case YamlSequenceNode sequence:
                    foreach (YamlNode child in sequence.Children)
                    {
                        CheckAnchors(context, child, reported);
                    }

                    break;
            }
        }

        private static string Clean(YamlException exception)
        {
            string message = exception.Message;
            int index = message.IndexOf("): ", StringComparison.Ordinal);
            return message.StartsWith("(Line:", StringComparison.Ordinal) && index > 0 ? message.Substring(index + 3) : message;
        }

        internal static SourceLocation Loc(YamlNode node)
        {
            return new SourceLocation((int)node.Start.Line, (int)node.Start.Column);
        }

        /// <summary>Resolves a plain scalar with the YAML 1.2 core schema; quoted scalars are strings.</summary>
        internal static ScalarValue Scalar(YamlScalarNode node)
        {
            string text = node.Value ?? string.Empty;
            if (node.Style == ScalarStyle.SingleQuoted || node.Style == ScalarStyle.DoubleQuoted || node.Style == ScalarStyle.Literal || node.Style == ScalarStyle.Folded)
            {
                return new ScalarValue(ScalarKind.String, text);
            }

            switch (text)
            {
                case "":
                case "~":
                case "null":
                case "Null":
                case "NULL":
                    return ScalarValue.NullValue;
                case "true":
                case "True":
                case "TRUE":
                    return new ScalarValue(ScalarKind.Boolean, "true");
                case "false":
                case "False":
                case "FALSE":
                    return new ScalarValue(ScalarKind.Boolean, "false");
            }

            if (Integer.IsMatch(text) && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long whole))
            {
                return new ScalarValue(ScalarKind.Integer, whole.ToString(CultureInfo.InvariantCulture));
            }

            if (Float.IsMatch(text) && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number))
            {
                return new ScalarValue(ScalarKind.Number, number.ToString(CultureInfo.InvariantCulture));
            }

            return new ScalarValue(ScalarKind.String, text);
        }

        private sealed class Context
        {
            private readonly List<WorkflowDiagnostic> _diagnostics = new List<WorkflowDiagnostic>();

            public IReadOnlyList<WorkflowDiagnostic> Diagnostics => _diagnostics;

            public void Report(string id, string message, SourceLocation location)
            {
                _diagnostics.Add(new WorkflowDiagnostic(id, WorkflowRules.SeverityOf(id), message, location));
            }

            public WorkflowDefinition Workflow(YamlMappingNode root)
            {
                var workflow = new WorkflowDefinition { Location = Loc(root) };
                bool sawVersionKey = false;
                foreach (KeyValuePair<YamlNode, YamlNode> pair in Keys(root, "workflow", "sourceflow.workflow", "name", "version", "owner", "description", "start", "data", "steps", "on"))
                {
                    string key = ((YamlScalarNode)pair.Key).Value!;
                    YamlNode value = pair.Value;
                    workflow.Mark(key, Loc(value));
                    switch (key)
                    {
                        case "sourceflow.workflow":
                            sawVersionKey = true;
                            int? schema = Int(value, key);
                            if (schema is { } v && v != WorkflowDefinition.DslVersion)
                            {
                                Report(
                                    WorkflowRules.SchemaVersion,
                                    v > WorkflowDefinition.DslVersion
                                        ? "This workflow uses sourceflow.workflow " + v + "; this version of SourceFlow reads version 1. Update SourceFlow Studio and the SourceFlow.Net package."
                                        : "sourceflow.workflow " + v + " is not supported; version 1 is the first DSL version.",
                                    Loc(value));
                            }

                            workflow.SchemaVersion = schema ?? 0;
                            break;
                        case "name":
                            workflow.Name = Text(value, key, Kebab, "a kebab-case name such as 'order-fulfilment'") ?? string.Empty;
                            break;
                        case "version":
                            int? version = Int(value, key);
                            if (version is { } n && n < 1)
                            {
                                Report(WorkflowRules.Shape, "'version' starts at 1.", Loc(value));
                            }

                            workflow.Version = version ?? 0;
                            break;
                        case "owner":
                            workflow.Owner = Text(value, key, ServiceName, "a service name") ?? string.Empty;
                            break;
                        case "description":
                            workflow.Description = Text(value, key);
                            break;
                        case "start":
                            workflow.Start = Start(value);
                            break;
                        case "data":
                            workflow.Data = Data(value);
                            break;
                        case "steps":
                            workflow.Steps = Steps(value);
                            break;
                        case "on":
                            workflow.On = Terminal(value);
                            break;
                    }
                }

                if (!sawVersionKey)
                {
                    Report(WorkflowRules.Shape, "The first key of a workflow file is 'sourceflow.workflow: 1'.", Loc(root));
                }

                Require(root, "workflow", "name", "version", "owner", "start", "steps");
                return workflow;
            }

            private StartSpec Start(YamlNode node)
            {
                var start = new StartSpec { Location = Loc(node) };
                if (Mapping(node, "start") is not { } mapping)
                {
                    return start;
                }

                foreach (KeyValuePair<YamlNode, YamlNode> pair in Keys(mapping, "start", "on", "correlate", "when"))
                {
                    string key = ((YamlScalarNode)pair.Key).Value!;
                    start.Mark(key, Loc(pair.Value));
                    switch (key)
                    {
                        case "on":
                            start.On = Text(pair.Value, "start.on", MessageReference, "a message type such as 'orders.order-placed.v1'") ?? string.Empty;
                            break;
                        case "correlate":
                            string? path = Text(pair.Value, "start.correlate");
                            if (path is not null && !CorrelationPath.IsValid(path))
                            {
                                Report(WorkflowRules.Format, "'start.correlate' is a path into the start message, such as 'data.orderId'.", Loc(pair.Value));
                            }

                            start.Correlate = path ?? string.Empty;
                            break;
                        case "when":
                            start.When = Text(pair.Value, "start.when");
                            break;
                    }
                }

                Require(mapping, "start", "on", "correlate");
                return start;
            }

            private IReadOnlyList<DataField> Data(YamlNode node)
            {
                var fields = new List<DataField>();
                if (Mapping(node, "data") is not { } mapping)
                {
                    return fields;
                }

                foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
                {
                    if (pair.Key is not YamlScalarNode { Value: { } name })
                    {
                        Report(WorkflowRules.Shape, "Data field names are plain text.", Loc(pair.Key));
                        continue;
                    }

                    var field = new DataField { Name = name, Location = Loc(pair.Key) };
                    if (!FieldName.IsMatch(name))
                    {
                        Report(WorkflowRules.Format, "Data field '" + name + "' is not an identifier.", Loc(pair.Key));
                    }

                    if (Mapping(pair.Value, "data." + name) is { } spec)
                    {
                        foreach (KeyValuePair<YamlNode, YamlNode> entry in Keys(spec, "data." + name, "from", "type", "default", "personal"))
                        {
                            string key = ((YamlScalarNode)entry.Key).Value!;
                            field.Mark(key, Loc(entry.Value));
                            switch (key)
                            {
                                case "from":
                                    string? from = Text(entry.Value, "data." + name + ".from");
                                    if (from is not null && !(from.StartsWith("start.data.", StringComparison.Ordinal) && Segments.IsMatch(from)))
                                    {
                                        Report(WorkflowRules.Format, "'from' is a path into the start message, such as 'start.data.orderId'.", Loc(entry.Value));
                                    }

                                    field.From = from;
                                    break;
                                case "type":
                                    string? type = Text(entry.Value, "data." + name + ".type");
                                    if (type is not null && Array.IndexOf((string[])DataTypes, type) < 0)
                                    {
                                        Report(WorkflowRules.Shape, "Unknown data type '" + type + "'; use one of " + string.Join(", ", DataTypes) + ".", Loc(entry.Value));
                                    }

                                    field.Type = type;
                                    break;
                                case "default":
                                    if (entry.Value is YamlScalarNode scalar)
                                    {
                                        field.Default = Scalar(scalar);
                                    }
                                    else
                                    {
                                        Report(WorkflowRules.Shape, "A default value is a scalar.", Loc(entry.Value));
                                    }

                                    break;
                                case "personal":
                                    field.Personal = Bool(entry.Value, "personal") ?? false;
                                    break;
                            }
                        }

                        if (field.From is null && field.Type is null)
                        {
                            Report(WorkflowRules.Shape, "Data field '" + name + "' needs 'from' or 'type'.", Loc(pair.Key));
                        }
                        else if (field.From is not null && (field.Type is not null || field.Default is not null))
                        {
                            Report(WorkflowRules.Shape, "Data field '" + name + "' has 'from'; 'type' and 'default' are for fields without one.", Loc(pair.Key));
                        }
                    }

                    fields.Add(field);
                }

                return fields;
            }

            private IReadOnlyList<WorkflowStep> Steps(YamlNode node)
            {
                var steps = new List<WorkflowStep>();
                if (node is not YamlSequenceNode sequence)
                {
                    Report(WorkflowRules.Shape, "'steps' is a list of steps.", Loc(node));
                    return steps;
                }

                if (sequence.Children.Count == 0)
                {
                    Report(WorkflowRules.Shape, "'steps' lists no step.", Loc(node));
                }

                foreach (YamlNode item in sequence.Children)
                {
                    if (Mapping(item, "step") is { } mapping)
                    {
                        steps.Add(Step(mapping));
                    }
                }

                return steps;
            }

            private WorkflowStep Step(YamlMappingNode mapping)
            {
                var step = new WorkflowStep { Location = Loc(mapping) };
                foreach (KeyValuePair<YamlNode, YamlNode> pair in Keys(mapping, "step", "id", "send", "await", "timeout", "compensate", "pivot", "when", "next"))
                {
                    string key = ((YamlScalarNode)pair.Key).Value!;
                    step.Mark(key, Loc(pair.Value));
                    switch (key)
                    {
                        case "id":
                            step.Id = Text(pair.Value, "id", Kebab, "a kebab-case step id such as 'charge-payment'") ?? string.Empty;
                            if (step.Id == WorkflowStep.CompleteKeyword)
                            {
                                Report(WorkflowRules.Format, "'complete' is reserved ('next: complete' ends the workflow); choose another step id.", Loc(pair.Value));
                            }

                            break;
                        case "send":
                            step.Send = Send(pair.Value, "send");
                            break;
                        case "await":
                            step.Await = Await(pair.Value, "await", compensation: false);
                            break;
                        case "timeout":
                            step.Timeout = Timeout(pair.Value, "timeout");
                            break;
                        case "compensate":
                            step.Compensate = Compensate(pair.Value);
                            break;
                        case "pivot":
                            step.Pivot = Bool(pair.Value, "pivot") ?? false;
                            break;
                        case "when":
                            step.When = Text(pair.Value, "when");
                            break;
                        case "next":
                            step.Next = Text(pair.Value, "next", Kebab, "a step id or 'complete'");
                            break;
                    }
                }

                Require(mapping, "step", "id", "await");
                return step;
            }

            private SendSpec? Send(YamlNode node, string where)
            {
                if (Mapping(node, where) is not { } mapping)
                {
                    return null;
                }

                var send = new SendSpec { Location = Loc(node) };
                foreach (KeyValuePair<YamlNode, YamlNode> pair in Keys(mapping, where, "command", "map"))
                {
                    string key = ((YamlScalarNode)pair.Key).Value!;
                    send.Mark(key, Loc(pair.Value));
                    if (key == "command")
                    {
                        send.Command = Text(pair.Value, where + ".command", MessageReference, "a command type such as 'payments.charge.v1'") ?? string.Empty;
                    }
                    else
                    {
                        send.Map = Map(pair.Value, where + ".map");
                    }
                }

                Require(mapping, where, "command");
                return send;
            }

            private IReadOnlyList<MapEntry> Map(YamlNode node, string where)
            {
                var entries = new List<MapEntry>();
                if (Mapping(node, where) is not { } mapping)
                {
                    return entries;
                }

                foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
                {
                    if (pair.Key is not YamlScalarNode { Value: { } field } || !FieldName.IsMatch(field))
                    {
                        Report(WorkflowRules.Format, "Map keys are command field names.", Loc(pair.Key));
                        continue;
                    }

                    var entry = new MapEntry { Field = field, Location = Loc(pair.Key) };
                    entry.Mark("value", Loc(pair.Value));
                    entry.Value = Value(pair.Value, where + "." + field);
                    entries.Add(entry);
                }

                return entries;
            }

            private MapValue Value(YamlNode node, string where)
            {
                switch (node)
                {
                    case YamlScalarNode scalar when scalar.Style == ScalarStyle.Plain && scalar.Value is { } text && IsPath(text):
                        if (!Segments.IsMatch(text.TrimStart('$')))
                        {
                            Report(WorkflowRules.Format, "'" + text + "' is not a valid path.", Loc(node));
                        }

                        return new MapValue(MapValueKind.Path, text, null);
                    case YamlScalarNode scalar:
                        return new MapValue(MapValueKind.Literal, null, Scalar(scalar));
                    case YamlMappingNode mapping when mapping.Children.Count == 1
                        && mapping.Children[0].Key is YamlScalarNode { Value: "$hook" }:
                        string? hook = Text(mapping.Children[0].Value, where + ".$hook", HookName, "a PascalCase method name");
                        return new MapValue(MapValueKind.Hook, hook ?? string.Empty, null);
                    default:
                        Report(WorkflowRules.Shape, "'" + where + "' is a path ($data.x, event.data.x, $failure.code), a literal or { $hook: Name }.", Loc(node));
                        return MapValue.Null;
                }
            }

            private static bool IsPath(string text)
            {
                return text.StartsWith("$data.", StringComparison.Ordinal)
                    || text.StartsWith("event.data.", StringComparison.Ordinal)
                    || text.StartsWith("$failure.", StringComparison.Ordinal);
            }

            private AwaitSpec? Await(YamlNode node, string where, bool compensation)
            {
                if (Mapping(node, where) is not { } mapping)
                {
                    return null;
                }

                var spec = new AwaitSpec { Location = Loc(node) };
                foreach (KeyValuePair<YamlNode, YamlNode> pair in Keys(mapping, where, "success", "failure"))
                {
                    string key = ((YamlScalarNode)pair.Key).Value!;
                    spec.Mark(key, Loc(pair.Value));
                    if (key == "success")
                    {
                        spec.Success = Success(pair.Value, where + ".success");
                    }
                    else if (compensation)
                    {
                        Report(WorkflowRules.NestedCompensation, "A compensation awaits its success only; it can't branch on failures (its retry and timeout handle them).", Loc(pair.Key));
                    }
                    else
                    {
                        spec.Failures = Failures(pair.Value, where + ".failure");
                    }
                }

                Require(mapping, where, "success");
                return spec;
            }

            private SuccessSpec Success(YamlNode node, string where)
            {
                var success = new SuccessSpec { Location = Loc(node) };
                if (node is YamlScalarNode)
                {
                    success.On = Text(node, where, MessageReference, "an event type such as 'payments.payment-captured.v1'") ?? string.Empty;
                    success.Mark("on", Loc(node));
                    return success;
                }

                if (Mapping(node, where) is not { } mapping)
                {
                    return success;
                }

                foreach (KeyValuePair<YamlNode, YamlNode> pair in Keys(mapping, where, "on", "capture", "when"))
                {
                    string key = ((YamlScalarNode)pair.Key).Value!;
                    success.Mark(key, Loc(pair.Value));
                    switch (key)
                    {
                        case "on":
                            success.On = Text(pair.Value, where + ".on", MessageReference, "an event type") ?? string.Empty;
                            break;
                        case "capture":
                            success.Capture = Captures(pair.Value, where + ".capture");
                            break;
                        case "when":
                            success.When = Text(pair.Value, where + ".when");
                            break;
                    }
                }

                Require(mapping, where, "on");
                return success;
            }

            private IReadOnlyList<CaptureSpec> Captures(YamlNode node, string where)
            {
                var captures = new List<CaptureSpec>();
                if (Mapping(node, where) is not { } mapping)
                {
                    return captures;
                }

                foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
                {
                    if (pair.Key is not YamlScalarNode { Value: { } field } || !FieldName.IsMatch(field))
                    {
                        Report(WorkflowRules.Format, "Capture keys are data field names.", Loc(pair.Key));
                        continue;
                    }

                    string? path = Text(pair.Value, where + "." + field);
                    if (path is not null && !(path.StartsWith("event.data.", StringComparison.Ordinal) && Segments.IsMatch(path)))
                    {
                        Report(WorkflowRules.Format, "A capture reads the awaited event, such as 'event.data.paymentId'.", Loc(pair.Value));
                    }

                    var capture = new CaptureSpec { Field = field, Path = path ?? string.Empty, Location = Loc(pair.Key) };
                    capture.Mark("path", Loc(pair.Value));
                    captures.Add(capture);
                }

                return captures;
            }

            private IReadOnlyList<FailureSpec> Failures(YamlNode node, string where)
            {
                var failures = new List<FailureSpec>();
                if (node is not YamlSequenceNode sequence)
                {
                    Report(WorkflowRules.Shape, "'" + where + "' is a list of failures.", Loc(node));
                    return failures;
                }

                foreach (YamlNode item in sequence.Children)
                {
                    if (Mapping(item, where) is not { } mapping)
                    {
                        continue;
                    }

                    var failure = new FailureSpec { Location = Loc(mapping) };
                    foreach (KeyValuePair<YamlNode, YamlNode> pair in Keys(mapping, where, "rejected", "on", "retry", "then"))
                    {
                        string key = ((YamlScalarNode)pair.Key).Value!;
                        failure.Mark(key, Loc(pair.Value));
                        switch (key)
                        {
                            case "rejected":
                                failure.Rejected = Text(pair.Value, "rejected", Code, "a rejection code such as 'payments.declined'");
                                break;
                            case "on":
                                failure.On = Text(pair.Value, "on", MessageReference, "an event type");
                                break;
                            case "retry":
                                failure.Retry = Retry(pair.Value, "retry");
                                break;
                            case "then":
                                failure.Then = Then(pair.Value);
                                break;
                        }
                    }

                    if ((failure.Rejected is null) == (failure.On is null))
                    {
                        Report(WorkflowRules.Shape, "A failure has either 'rejected: <code>' or 'on: <event type>'.", Loc(mapping));
                    }

                    failures.Add(failure);
                }

                return failures;
            }

            private TimeoutSpec? Timeout(YamlNode node, string where)
            {
                var timeout = new TimeoutSpec { Location = Loc(node) };
                if (node is YamlScalarNode { Value: "none" })
                {
                    timeout.None = true;
                    return timeout;
                }

                if (Mapping(node, where) is not { } mapping)
                {
                    return null;
                }

                foreach (KeyValuePair<YamlNode, YamlNode> pair in Keys(mapping, where, "after", "none", "reason", "retry", "then"))
                {
                    string key = ((YamlScalarNode)pair.Key).Value!;
                    timeout.Mark(key, Loc(pair.Value));
                    switch (key)
                    {
                        case "after":
                            timeout.After = Duration(pair.Value, where + ".after");
                            break;
                        case "none":
                            timeout.None = Bool(pair.Value, "none") ?? false;
                            break;
                        case "reason":
                            timeout.Reason = Text(pair.Value, "reason");
                            break;
                        case "retry":
                            timeout.Retry = Retry(pair.Value, where + ".retry");
                            break;
                        case "then":
                            timeout.Then = Then(pair.Value);
                            break;
                    }
                }

                if (timeout.None && timeout.After is not null)
                {
                    Report(WorkflowRules.Shape, "A timeout has 'after' or 'none: true', not both.", Loc(mapping));
                }
                else if (!timeout.None && !Has(mapping, "after"))
                {
                    Report(WorkflowRules.Shape, "'" + where + "' needs 'after' (an ISO-8601 duration), or 'none: true' with a 'reason'.", Loc(mapping));
                }

                return timeout;
            }

            private RetrySpec? Retry(YamlNode node, string where)
            {
                if (Mapping(node, where) is not { } mapping)
                {
                    return null;
                }

                var retry = new RetrySpec { Location = Loc(node) };
                foreach (KeyValuePair<YamlNode, YamlNode> pair in Keys(mapping, where, "max", "backoff", "initial", "cap", "jitter"))
                {
                    string key = ((YamlScalarNode)pair.Key).Value!;
                    retry.Mark(key, Loc(pair.Value));
                    switch (key)
                    {
                        case "max":
                            retry.Max = Int(pair.Value, where + ".max") ?? 0;
                            break;
                        case "backoff":
                            string? backoff = Text(pair.Value, where + ".backoff");
                            if (backoff == "fixed")
                            {
                                retry.Backoff = Backoff.Fixed;
                            }
                            else if (backoff == "exponential")
                            {
                                retry.Backoff = Backoff.Exponential;
                            }
                            else if (backoff is not null)
                            {
                                Report(WorkflowRules.Shape, "'backoff' is 'fixed' or 'exponential'.", Loc(pair.Value));
                            }

                            break;
                        case "initial":
                            retry.Initial = Duration(pair.Value, where + ".initial") ?? TimeSpan.Zero;
                            break;
                        case "cap":
                            retry.Cap = Duration(pair.Value, where + ".cap");
                            break;
                        case "jitter":
                            retry.Jitter = Bool(pair.Value, "jitter") ?? true;
                            break;
                    }
                }

                Require(mapping, where, "max");
                return retry;
            }

            private CompensateSpec? Compensate(YamlNode node)
            {
                var compensate = new CompensateSpec { Location = Loc(node) };
                if (node is YamlScalarNode { Value: "none" })
                {
                    compensate.None = true;
                    return compensate;
                }

                if (Mapping(node, "compensate") is not { } mapping)
                {
                    return null;
                }

                foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
                {
                    if (pair.Key is YamlScalarNode { Value: "compensate" or "pivot" or "next" } nested)
                    {
                        Report(WorkflowRules.NestedCompensation, "A compensation can't declare '" + nested.Value + "': compensations are not compensated or redirected (no nested sagas).", Loc(pair.Key));
                    }
                }

                foreach (KeyValuePair<YamlNode, YamlNode> pair in Keys(mapping, "compensate", "send", "await", "retry", "timeout", "none", "reason", "compensate", "pivot", "next"))
                {
                    string key = ((YamlScalarNode)pair.Key).Value!;
                    compensate.Mark(key, Loc(pair.Value));
                    switch (key)
                    {
                        case "send":
                            compensate.Send = Send(pair.Value, "compensate.send");
                            break;
                        case "await":
                            compensate.Await = Await(pair.Value, "compensate.await", compensation: true);
                            break;
                        case "retry":
                            compensate.Retry = Retry(pair.Value, "compensate.retry");
                            break;
                        case "timeout":
                            compensate.Timeout = Timeout(pair.Value, "compensate.timeout");
                            break;
                        case "none":
                            compensate.None = Bool(pair.Value, "none") ?? false;
                            break;
                        case "reason":
                            compensate.Reason = Text(pair.Value, "reason");
                            break;
                    }
                }

                if (!compensate.None)
                {
                    Require(mapping, "compensate", "send", "await");
                }

                return compensate;
            }

            private ThenSpec Then(YamlNode node)
            {
                if (node is YamlScalarNode scalar)
                {
                    switch (scalar.Value)
                    {
                        case "fail":
                            return Located(new ThenSpec(ThenKind.Fail, null), node);
                        case "manual":
                            return Located(new ThenSpec(ThenKind.Manual, null), node);
                        case "complete":
                            return Located(new ThenSpec(ThenKind.Complete, null), node);
                    }
                }
                else if (node is YamlMappingNode mapping && mapping.Children.Count == 1 && mapping.Children[0].Key is YamlScalarNode { Value: "goto" })
                {
                    string? target = Text(mapping.Children[0].Value, "goto", Kebab, "a step id");
                    return Located(new ThenSpec(ThenKind.Goto, target ?? string.Empty), node);
                }

                Report(WorkflowRules.Shape, "'then' is fail, manual, complete or { goto: <step id> }.", Loc(node));
                return Located(new ThenSpec(ThenKind.Fail, null), node);
            }

            private static ThenSpec Located(ThenSpec then, YamlNode node)
            {
                then.Location = Loc(node);
                return then;
            }

            private TerminalActions Terminal(YamlNode node)
            {
                var on = new TerminalActions { Location = Loc(node) };
                if (Mapping(node, "on") is not { } mapping)
                {
                    return on;
                }

                foreach (KeyValuePair<YamlNode, YamlNode> pair in Keys(mapping, "on", "completed", "compensated", "manual"))
                {
                    string key = ((YamlScalarNode)pair.Key).Value!;
                    on.Mark(key, Loc(pair.Value));
                    if (Mapping(pair.Value, "on." + key) is not { } action)
                    {
                        continue;
                    }

                    if (key == "manual")
                    {
                        foreach (KeyValuePair<YamlNode, YamlNode> entry in Keys(action, "on.manual", "alert"))
                        {
                            on.ManualAlert = Bool(entry.Value, "alert") ?? true;
                        }

                        continue;
                    }

                    foreach (KeyValuePair<YamlNode, YamlNode> entry in Keys(action, "on." + key, "send"))
                    {
                        SendSpec? send = Send(entry.Value, "on." + key + ".send");
                        if (key == "completed")
                        {
                            on.Completed = send;
                        }
                        else
                        {
                            on.Compensated = send;
                        }
                    }
                }

                return on;
            }

            // ------------------------------------------------------------ helpers

            private IEnumerable<KeyValuePair<YamlNode, YamlNode>> Keys(YamlMappingNode mapping, string where, params string[] allowed)
            {
                foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
                {
                    if (pair.Key is not YamlScalarNode { Value: { } key })
                    {
                        Report(WorkflowRules.Shape, "Keys are plain text.", Loc(pair.Key));
                        continue;
                    }

                    if (Array.IndexOf(allowed, key) < 0)
                    {
                        Report(WorkflowRules.UnknownKey, "Unknown key '" + key + "' in " + where + "; expected " + string.Join(", ", Expected(allowed)) + ".", Loc(pair.Key));
                        continue;
                    }

                    yield return pair;
                }
            }

            private static IEnumerable<string> Expected(string[] allowed)
            {
                foreach (string key in allowed)
                {
                    if (key != "compensate" && key != "pivot" && key != "next")
                    {
                        yield return key;
                    }
                    else if (Array.IndexOf(allowed, "id") >= 0)
                    {
                        yield return key;   // allowed on steps
                    }
                }
            }

            private void Require(YamlMappingNode mapping, string where, params string[] keys)
            {
                foreach (string key in keys)
                {
                    if (!Has(mapping, key))
                    {
                        Report(WorkflowRules.Shape, "'" + where + "' needs '" + key + "'.", Loc(mapping));
                    }
                }
            }

            private static bool Has(YamlMappingNode mapping, string key)
            {
                foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
                {
                    if (pair.Key is YamlScalarNode { Value: { } k } && k == key)
                    {
                        return true;
                    }
                }

                return false;
            }

            private YamlMappingNode? Mapping(YamlNode node, string where)
            {
                if (node is YamlMappingNode mapping)
                {
                    return mapping;
                }

                Report(WorkflowRules.Shape, "'" + where + "' is a mapping.", Loc(node));
                return null;
            }

            private string? Text(YamlNode node, string where, Regex? format = null, string? expected = null)
            {
                if (node is not YamlScalarNode scalar || scalar.Value is null)
                {
                    Report(WorkflowRules.Shape, "'" + where + "' is a single value.", Loc(node));
                    return null;
                }

                if (format is not null && !format.IsMatch(scalar.Value))
                {
                    Report(WorkflowRules.Format, "'" + scalar.Value + "' is not " + expected + ".", Loc(node));
                }

                return scalar.Value;
            }

            private int? Int(YamlNode node, string where)
            {
                if (node is YamlScalarNode scalar && Scalar(scalar) is { Kind: ScalarKind.Integer } value
                    && int.TryParse(value.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int result))
                {
                    return result;
                }

                Report(WorkflowRules.Shape, "'" + where + "' is a whole number.", Loc(node));
                return null;
            }

            private bool? Bool(YamlNode node, string where)
            {
                if (node is YamlScalarNode scalar && Scalar(scalar) is { Kind: ScalarKind.Boolean } value)
                {
                    return value.Text == "true";
                }

                Report(WorkflowRules.Shape, "'" + where + "' is true or false.", Loc(node));
                return null;
            }

            private TimeSpan? Duration(YamlNode node, string where)
            {
                string? text = Text(node, where);
                if (text is null)
                {
                    return null;
                }

                if (!IsoDuration.TryParse(text, out TimeSpan duration))
                {
                    Report(WorkflowRules.Duration, "'" + text + "' is not an ISO-8601 duration such as PT30S, PT5M, PT1H or P1D (years and months are not supported).", Loc(node));
                    return null;
                }

                return duration;
            }
        }
    }
}

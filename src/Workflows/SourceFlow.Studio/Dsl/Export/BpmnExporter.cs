using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using SourceFlow.Studio.Dsl.Model;

namespace SourceFlow.Studio.Dsl.Export
{
    /// <summary>
    /// BPMN 2.0 export (doc 15 §6.3; export only, product WQ2): one process with a message start event, a send task per
    /// command, a receive task per wait with timer and message boundary events, an exclusive gateway per guard, and
    /// compensation tasks linked to their steps by compensation boundary events. Retries are BPMN documentation. The
    /// model has no diagram interchange (BPMNDI); modelling tools lay it out on import.
    /// </summary>
    public static class BpmnExporter
    {
        /// <summary>The BPMN 2.0 model namespace.</summary>
        public const string Namespace = "http://www.omg.org/spec/BPMN/20100524/MODEL";

        /// <summary>Exports <paramref name="workflow"/>.</summary>
        public static string Export(WorkflowDefinition workflow)
        {
            if (workflow is null)
            {
                throw new ArgumentNullException(nameof(workflow));
            }

            var model = new Model(workflow);
            model.Build();
            return model.Write();
        }

        private sealed class Model
        {
            private readonly WorkflowDefinition _workflow;
            private readonly SortedDictionary<string, string> _messages = new SortedDictionary<string, string>(StringComparer.Ordinal);
            private readonly List<Element> _elements = new List<Element>();
            private readonly List<(string Id, string From, string To, string? Name, string? Condition)> _flows = new List<(string, string, string, string?, string?)>();
            private readonly List<(string Id, string From, string To)> _associations = new List<(string, string, string)>();

            public Model(WorkflowDefinition workflow)
            {
                _workflow = workflow;
            }

            public void Build()
            {
                string start = Add("startEvent", "Start", _workflow.Start.On, message: _workflow.Start.On);
                Add("endEvent", "Completed", "Completed");
                bool compensates = _workflow.Steps.Any(s => s.Compensate is { None: false });
                Add("intermediateThrowEvent", "Compensate", "Compensate completed steps", compensate: true);
                Add("endEvent", "Compensated", "Compensated");
                Flow("Compensate", "Compensated", compensates ? "all undone" : "nothing to undo");
                Add("endEvent", "ManualIntervention", "Manual intervention", documentation: "An operator resumes, retries or aborts the instance.");

                if (_workflow.Steps.Count == 0)
                {
                    Flow(start, "Completed", null);
                    return;
                }

                Flow(start, Entry(_workflow.Steps[0]), null);
                for (int i = 0; i < _workflow.Steps.Count; i++)
                {
                    Step(_workflow.Steps[i], i);
                }
            }

            private string Entry(WorkflowStep step)
            {
                return step.When is not null ? "Gateway_" + Id(step.Id) : step.Send is not null ? "Send_" + Id(step.Id) : "Await_" + Id(step.Id);
            }

            private string After(int index)
            {
                return index + 1 < _workflow.Steps.Count ? Entry(_workflow.Steps[index + 1]) : "Completed";
            }

            private void Step(WorkflowStep step, int index)
            {
                string id = Id(step.Id);
                string await = "Await_" + id;
                if (step.When is { } when)
                {
                    Add("exclusiveGateway", "Gateway_" + id, step.Id + "?", @default: "Flow_skip_" + id);
                    Flow("Gateway_" + id, step.Send is not null ? "Send_" + id : await, "when", when);
                    _flows.Add(("Flow_skip_" + id, "Gateway_" + id, After(index), "skip", null));
                }

                if (step.Send is { } send)
                {
                    Add("sendTask", "Send_" + id, step.Id + ": " + send.Command, message: send.Command, documentation: step.Pivot ? "Pivot: the point of no return." : null);
                    Flow("Send_" + id, await, null);
                    if (step.Compensate is { None: false, Send: { } undo } compensate)
                    {
                        Add("boundaryEvent", "Undo_boundary_" + id, "undo " + step.Id, attachedTo: "Send_" + id, compensate: true);
                        Add("sendTask", "Undo_" + id, "undo " + step.Id + ": " + undo.Command, message: undo.Command, forCompensation: true,
                            documentation: Join("await " + compensate.Await?.Success.On, Retry(compensate.Retry), compensate.Timeout?.After is { } wait ? "timeout " + IsoDuration.Format(wait) + ", then manual intervention" : null));
                        _associations.Add(("Association_" + id, "Undo_boundary_" + id, "Undo_" + id));
                    }
                }

                Add("receiveTask", await, step.Id + ": await " + step.Await?.Success.On, message: step.Await?.Success.On);
                string success = step.Next == WorkflowStep.CompleteKeyword ? "Completed" : step.Next is { } next && _workflow.Step(next) is { } target ? Entry(target) : After(index);
                Flow(await, success, "✓ " + step.Await?.Success.On);

                IReadOnlyList<FailureSpec> failures = step.Await?.Failures ?? Array.Empty<FailureSpec>();
                for (int i = 0; i < failures.Count; i++)
                {
                    FailureSpec failure = failures[i];
                    string boundary = "Failure_" + id + "_" + (i + 1).ToString(CultureInfo.InvariantCulture);
                    string what = failure.Rejected is { } code ? "rejected " + code : failure.On!;
                    Add("boundaryEvent", boundary, what, attachedTo: await, message: failure.Rejected is { } c ? "rejected:" + c : failure.On,
                        documentation: Retry(failure.Retry));
                    Flow(boundary, Then(failure.Then), "✕ " + what);
                }

                if (step.Timeout is { None: false, After: { } after } timeout)
                {
                    string boundary = "Timeout_" + id;
                    Add("boundaryEvent", boundary, "timeout " + IsoDuration.Format(after), attachedTo: await, timer: IsoDuration.Format(after), documentation: Retry(timeout.Retry));
                    Flow(boundary, Then(timeout.Then), "⏱ " + IsoDuration.Format(after));
                }
            }

            private string Then(ThenSpec then)
            {
                switch (then.Kind)
                {
                    case ThenKind.Fail: return "Compensate";
                    case ThenKind.Manual: return "ManualIntervention";
                    case ThenKind.Complete: return "Completed";
                    default: return _workflow.Step(then.Target ?? string.Empty) is { } step ? Entry(step) : "ManualIntervention";
                }
            }

            private static string? Retry(RetrySpec? retry)
            {
                if (retry is null)
                {
                    return null;
                }

                return "retry " + retry.Max + "× (" + (retry.Backoff == Backoff.Exponential ? "exponential" : "fixed") + ", initial " + IsoDuration.Format(retry.Initial)
                    + (retry.Cap is { } cap ? ", cap " + IsoDuration.Format(cap) : string.Empty) + ")";
            }

            private static string? Join(params string?[] parts)
            {
                string text = string.Join("; ", parts.Where(p => !string.IsNullOrEmpty(p)));
                return text.Length == 0 ? null : text;
            }

            private string Add(string kind, string id, string name, string? message = null, string? attachedTo = null, string? timer = null,
                bool compensate = false, bool forCompensation = false, string? @default = null, string? documentation = null)
            {
                string? messageRef = null;
                if (message is not null)
                {
                    messageRef = "Message_" + Id(message);
                    _messages[messageRef] = message;
                }

                _elements.Add(new Element(kind, id, name, messageRef, attachedTo, timer, compensate, forCompensation, @default, documentation));
                return id;
            }

            private void Flow(string from, string to, string? name, string? condition = null)
            {
                _flows.Add(("Flow_" + (_flows.Count + 1).ToString(CultureInfo.InvariantCulture), from, to, name, condition));
            }

            public string Write()
            {
                var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", NewLineChars = "\n", Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false };
                using var output = new StringWriterUtf8();
                using (XmlWriter xml = XmlWriter.Create(output, settings))
                {
                    string process = Id(_workflow.Name) + "_" + _workflow.Version.ToString(CultureInfo.InvariantCulture);
                    xml.WriteStartElement("bpmn", "definitions", Namespace);
                    xml.WriteAttributeString("id", "Definitions_" + process);
                    xml.WriteAttributeString("targetNamespace", "https://sourceflow.net/workflows/" + _workflow.Owner);
                    xml.WriteAttributeString("exporter", "SourceFlow Studio");
                    xml.WriteAttributeString("exporterVersion", "1");
                    foreach (KeyValuePair<string, string> message in _messages)
                    {
                        xml.WriteStartElement("bpmn", "message", Namespace);
                        xml.WriteAttributeString("id", message.Key);
                        xml.WriteAttributeString("name", message.Value);
                        xml.WriteEndElement();
                    }

                    xml.WriteStartElement("bpmn", "process", Namespace);
                    xml.WriteAttributeString("id", "Process_" + process);
                    xml.WriteAttributeString("name", _workflow.ProcessName);
                    xml.WriteAttributeString("isExecutable", "false");
                    if (_workflow.Description is { } description)
                    {
                        xml.WriteElementString("bpmn", "documentation", Namespace, description);
                    }

                    foreach (Element element in _elements)
                    {
                        WriteElement(xml, element);
                    }

                    foreach (var flow in _flows)
                    {
                        xml.WriteStartElement("bpmn", "sequenceFlow", Namespace);
                        xml.WriteAttributeString("id", flow.Id);
                        if (flow.Name is not null)
                        {
                            xml.WriteAttributeString("name", flow.Name);
                        }

                        xml.WriteAttributeString("sourceRef", flow.From);
                        xml.WriteAttributeString("targetRef", flow.To);
                        if (flow.Condition is not null)
                        {
                            xml.WriteStartElement("bpmn", "conditionExpression", Namespace);
                            xml.WriteAttributeString("xsi", "type", "http://www.w3.org/2001/XMLSchema-instance", "bpmn:tFormalExpression");
                            xml.WriteAttributeString("language", "https://github.com/cloudevents/spec/blob/main/cesql/spec.md");
                            xml.WriteString(flow.Condition);
                            xml.WriteEndElement();
                        }

                        xml.WriteEndElement();
                    }

                    foreach (var association in _associations)
                    {
                        xml.WriteStartElement("bpmn", "association", Namespace);
                        xml.WriteAttributeString("id", association.Id);
                        xml.WriteAttributeString("associationDirection", "One");
                        xml.WriteAttributeString("sourceRef", association.From);
                        xml.WriteAttributeString("targetRef", association.To);
                        xml.WriteEndElement();
                    }

                    xml.WriteEndElement();
                    xml.WriteEndElement();
                }

                return output.ToString().Replace("\r\n", "\n") + "\n";
            }

            private void WriteElement(XmlWriter xml, Element element)
            {
                xml.WriteStartElement("bpmn", element.Kind, Namespace);
                xml.WriteAttributeString("id", element.Id);
                xml.WriteAttributeString("name", element.Name);
                if (element.AttachedTo is not null)
                {
                    xml.WriteAttributeString("attachedToRef", element.AttachedTo);
                    xml.WriteAttributeString("cancelActivity", "true");
                }

                if (element.ForCompensation)
                {
                    xml.WriteAttributeString("isForCompensation", "true");
                }

                if (element.Default is not null)
                {
                    xml.WriteAttributeString("default", element.Default);
                }

                if (element.Kind is "sendTask" or "receiveTask" && element.MessageRef is not null)
                {
                    xml.WriteAttributeString("messageRef", element.MessageRef);
                }

                if (element.Documentation is not null)
                {
                    xml.WriteElementString("bpmn", "documentation", Namespace, element.Documentation);
                }

                foreach (var flow in _flows.Where(f => f.To == element.Id))
                {
                    xml.WriteElementString("bpmn", "incoming", Namespace, flow.Id);
                }

                foreach (var flow in _flows.Where(f => f.From == element.Id))
                {
                    xml.WriteElementString("bpmn", "outgoing", Namespace, flow.Id);
                }

                if (element.Timer is not null)
                {
                    xml.WriteStartElement("bpmn", "timerEventDefinition", Namespace);
                    xml.WriteStartElement("bpmn", "timeDuration", Namespace);
                    xml.WriteAttributeString("xsi", "type", "http://www.w3.org/2001/XMLSchema-instance", "bpmn:tFormalExpression");
                    xml.WriteString(element.Timer);
                    xml.WriteEndElement();
                    xml.WriteEndElement();
                }
                else if (element.Compensate)
                {
                    xml.WriteStartElement("bpmn", "compensateEventDefinition", Namespace);
                    xml.WriteEndElement();
                }
                else if (element.MessageRef is not null && element.Kind is "startEvent" or "boundaryEvent")
                {
                    xml.WriteStartElement("bpmn", "messageEventDefinition", Namespace);
                    xml.WriteAttributeString("messageRef", element.MessageRef);
                    xml.WriteEndElement();
                }

                xml.WriteEndElement();
            }
        }

        private sealed record Element(string Kind, string Id, string Name, string? MessageRef, string? AttachedTo, string? Timer, bool Compensate, bool ForCompensation, string? Default, string? Documentation);

        /// <summary>An XML-safe id from a name.</summary>
        internal static string Id(string name)
        {
            var id = new StringBuilder();
            foreach (char c in name)
            {
                id.Append(char.IsLetterOrDigit(c) ? c : '_');
            }

            return id.ToString();
        }

        private sealed class StringWriterUtf8 : StringWriter
        {
            public StringWriterUtf8()
                : base(CultureInfo.InvariantCulture)
            {
            }

            public override Encoding Encoding => new UTF8Encoding(false);
        }
    }
}

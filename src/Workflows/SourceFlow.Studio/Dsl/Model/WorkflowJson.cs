using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SourceFlow.Studio.Dsl.Json;

namespace SourceFlow.Studio.Dsl.Model
{
    /// <summary>
    /// The workflow model as compact JSON, without source positions: the generator embeds it in the generated process
    /// manager and the workflow engine reads it back (WP-S.5). <see cref="Read"/> of <see cref="Write"/> gives an
    /// equivalent model.
    /// </summary>
    public static class WorkflowJson
    {
        /// <summary>Writes <paramref name="workflow"/>.</summary>
        public static string Write(WorkflowDefinition workflow)
        {
            if (workflow is null)
            {
                throw new ArgumentNullException(nameof(workflow));
            }

            var json = new Writer();
            json.Begin();
            json.Prop("name", workflow.Name).Prop("version", workflow.Version).Prop("owner", workflow.Owner);
            json.Key("start").Begin().Prop("on", workflow.Start.On).Prop("correlate", workflow.Start.Correlate).Prop("when", workflow.Start.When).End();
            json.Key("data").Array(workflow.Data, (w, d) =>
            {
                w.Begin().Prop("name", d.Name).Prop("from", d.From).Prop("type", d.Type).Prop("personal", d.Personal);
                if (d.Default is { } value)
                {
                    w.Key("default").Scalar(value);
                }

                w.End();
            });
            json.Key("steps").Array(workflow.Steps, (w, s) =>
            {
                w.Begin().Prop("id", s.Id).Prop("pivot", s.Pivot).Prop("when", s.When).Prop("next", s.Next);
                Send(w, "send", s.Send);
                if (s.Await is { } wait)
                {
                    Await(w.Key("await"), wait);
                }

                Timeout(w, "timeout", s.Timeout);
                if (s.Compensate is { } c)
                {
                    w.Key("compensate").Begin().Prop("none", c.None).Prop("reason", c.Reason);
                    Send(w, "send", c.Send);
                    if (c.Await is { } cw)
                    {
                        Await(w.Key("await"), cw);
                    }

                    Retry(w, "retry", c.Retry);
                    Timeout(w, "timeout", c.Timeout);
                    w.End();
                }

                w.End();
            });
            json.Key("on").Begin();
            Send(json, "completed", workflow.On.Completed);
            Send(json, "compensated", workflow.On.Compensated);
            json.Prop("manualAlert", workflow.On.ManualAlert).End();
            json.End();
            return json.ToString();
        }

        /// <summary>Reads a model written by <see cref="Write"/>.</summary>
        /// <exception cref="FormatException">The JSON is not a written workflow.</exception>
        public static WorkflowDefinition Read(string json)
        {
            JsonValue root = JsonLite.Parse(json);
            var workflow = new WorkflowDefinition
            {
                SchemaVersion = WorkflowDefinition.DslVersion,
                Name = Str(root, "name") ?? throw new FormatException("A workflow has a name."),
                Version = Int(root, "version"),
                Owner = Str(root, "owner") ?? string.Empty,
            };
            JsonValue start = root["start"] ?? throw new FormatException("A workflow has a start.");
            workflow.Start = new StartSpec { On = Str(start, "on") ?? string.Empty, Correlate = Str(start, "correlate") ?? string.Empty, When = Str(start, "when") };
            workflow.Data = (root["data"]?.Items ?? Array.Empty<JsonValue>()).Select(d => new DataField
            {
                Name = Str(d, "name") ?? string.Empty,
                From = Str(d, "from"),
                Type = Str(d, "type"),
                Personal = d["personal"]?.AsBoolean ?? false,
                Default = d["default"] is { } value ? Scalar(value) : null,
            }).ToArray();
            workflow.Steps = (root["steps"]?.Items ?? Array.Empty<JsonValue>()).Select(s => new WorkflowStep
            {
                Id = Str(s, "id") ?? string.Empty,
                Pivot = s["pivot"]?.AsBoolean ?? false,
                When = Str(s, "when"),
                Next = Str(s, "next"),
                Send = Send(s["send"]),
                Await = Await(s["await"]),
                Timeout = Timeout(s["timeout"]),
                Compensate = s["compensate"] is { } c
                    ? new CompensateSpec
                    {
                        None = c["none"]?.AsBoolean ?? false,
                        Reason = Str(c, "reason"),
                        Send = Send(c["send"]),
                        Await = Await(c["await"]),
                        Retry = Retry(c["retry"]),
                        Timeout = Timeout(c["timeout"]),
                    }
                    : null,
            }).ToArray();
            JsonValue? on = root["on"];
            workflow.On = new TerminalActions
            {
                Completed = Send(on?["completed"]),
                Compensated = Send(on?["compensated"]),
                ManualAlert = on?["manualAlert"]?.AsBoolean ?? true,
            };
            return workflow;
        }

        private static void Send(Writer w, string key, SendSpec? send)
        {
            if (send is null)
            {
                return;
            }

            w.Key(key).Begin().Prop("command", send.Command).Key("map").Array(send.Map, (m, e) =>
            {
                m.Begin().Prop("field", e.Field).Prop("kind", e.Value.Kind.ToString()).Prop("text", e.Value.Text);
                if (e.Value.Literal is { } literal)
                {
                    m.Key("literal").Scalar(literal);
                }

                m.End();
            }).End();
        }

        private static void Await(Writer w, AwaitSpec wait)
        {
            w.Begin().Key("success").Begin().Prop("on", wait.Success.On).Prop("when", wait.Success.When)
                .Key("capture").Array(wait.Success.Capture, (c, e) => c.Begin().Prop("field", e.Field).Prop("path", e.Path).End()).End();
            w.Key("failures").Array(wait.Failures, (f, e) =>
            {
                f.Begin().Prop("rejected", e.Rejected).Prop("on", e.On);
                Retry(f, "retry", e.Retry);
                Then(f, e.Then);
                f.End();
            });
            w.End();
        }

        private static void Timeout(Writer w, string key, TimeoutSpec? timeout)
        {
            if (timeout is null)
            {
                return;
            }

            w.Key(key).Begin().Prop("none", timeout.None).Prop("reason", timeout.Reason);
            if (timeout.After is { } after)
            {
                w.Prop("after", IsoDuration.Format(after));
            }

            Retry(w, "retry", timeout.Retry);
            Then(w, timeout.Then);
            w.End();
        }

        private static void Retry(Writer w, string key, RetrySpec? retry)
        {
            if (retry is null)
            {
                return;
            }

            w.Key(key).Begin().Prop("max", retry.Max).Prop("backoff", retry.Backoff == Backoff.Exponential ? "exponential" : "fixed")
                .Prop("initial", IsoDuration.Format(retry.Initial)).Prop("jitter", retry.Jitter);
            if (retry.Cap is { } cap)
            {
                w.Prop("cap", IsoDuration.Format(cap));
            }

            w.End();
        }

        private static void Then(Writer w, ThenSpec then)
        {
            w.Key("then").Begin().Prop("kind", then.Kind.ToString()).Prop("target", then.Target).End();
        }

        private static SendSpec? Send(JsonValue? node)
        {
            if (node is null)
            {
                return null;
            }

            return new SendSpec
            {
                Command = Str(node, "command") ?? string.Empty,
                Map = (node["map"]?.Items ?? Array.Empty<JsonValue>()).Select(e => new MapEntry
                {
                    Field = Str(e, "field") ?? string.Empty,
                    Value = new MapValue(
                        (MapValueKind)Enum.Parse(typeof(MapValueKind), Str(e, "kind") ?? "Literal"),
                        Str(e, "text"),
                        e["literal"] is { } literal ? Scalar(literal) : null),
                }).ToArray(),
            };
        }

        private static AwaitSpec? Await(JsonValue? node)
        {
            if (node is null)
            {
                return null;
            }

            JsonValue success = node["success"] ?? throw new FormatException("An await has a success.");
            return new AwaitSpec
            {
                Success = new SuccessSpec
                {
                    On = Str(success, "on") ?? string.Empty,
                    When = Str(success, "when"),
                    Capture = (success["capture"]?.Items ?? Array.Empty<JsonValue>()).Select(c => new CaptureSpec { Field = Str(c, "field") ?? string.Empty, Path = Str(c, "path") ?? string.Empty }).ToArray(),
                },
                Failures = (node["failures"]?.Items ?? Array.Empty<JsonValue>()).Select(f => new FailureSpec
                {
                    Rejected = Str(f, "rejected"),
                    On = Str(f, "on"),
                    Retry = Retry(f["retry"]),
                    Then = Then(f["then"]),
                }).ToArray(),
            };
        }

        private static TimeoutSpec? Timeout(JsonValue? node)
        {
            if (node is null)
            {
                return null;
            }

            return new TimeoutSpec
            {
                None = node["none"]?.AsBoolean ?? false,
                Reason = Str(node, "reason"),
                After = Str(node, "after") is { } after && IsoDuration.TryParse(after, out TimeSpan d) ? d : (TimeSpan?)null,
                Retry = Retry(node["retry"]),
                Then = Then(node["then"]),
            };
        }

        private static RetrySpec? Retry(JsonValue? node)
        {
            if (node is null)
            {
                return null;
            }

            return new RetrySpec
            {
                Max = Int(node, "max"),
                Backoff = Str(node, "backoff") == "exponential" ? Backoff.Exponential : Backoff.Fixed,
                Initial = Str(node, "initial") is { } initial && IsoDuration.TryParse(initial, out TimeSpan i) ? i : TimeSpan.Zero,
                Cap = Str(node, "cap") is { } cap && IsoDuration.TryParse(cap, out TimeSpan c) ? c : (TimeSpan?)null,
                Jitter = node["jitter"]?.AsBoolean ?? true,
            };
        }

        private static ThenSpec Then(JsonValue? node)
        {
            if (node is null)
            {
                return ThenSpec.Fail;
            }

            var kind = (ThenKind)Enum.Parse(typeof(ThenKind), Str(node, "kind") ?? "Fail");
            return new ThenSpec(kind, Str(node, "target"));
        }

        private static ScalarValue Scalar(JsonValue node)
        {
            var kind = (ScalarKind)Enum.Parse(typeof(ScalarKind), Str(node, "kind") ?? "Null");
            return kind == ScalarKind.Null ? ScalarValue.NullValue : new ScalarValue(kind, Str(node, "text"));
        }

        private static string? Str(JsonValue node, string name)
        {
            return node[name]?.AsString;
        }

        private static int Int(JsonValue node, string name)
        {
            return (int)(node[name]?.AsInteger ?? 0);
        }

        /// <summary>A minimal JSON writer: members with null values are left out.</summary>
        internal sealed class Writer
        {
            private readonly StringBuilder _text = new StringBuilder();
            private readonly Stack<bool> _first = new Stack<bool>();
            private bool _afterKey;

            public Writer Begin()
            {
                Comma();
                _text.Append('{');
                _first.Push(true);
                return this;
            }

            public Writer End()
            {
                _text.Append('}');
                _first.Pop();
                return this;
            }

            public Writer Key(string name)
            {
                Comma();
                String(name);
                _text.Append(':');
                _afterKey = true;
                return this;
            }

            public Writer Prop(string name, string? value)
            {
                if (value is not null)
                {
                    Key(name).Value(value);
                }

                return this;
            }

            public Writer Prop(string name, int value)
            {
                Key(name);
                _afterKey = false;
                _text.Append(value.ToString(CultureInfo.InvariantCulture));
                return this;
            }

            public Writer Prop(string name, bool value)
            {
                if (value)
                {
                    Key(name);
                    _afterKey = false;
                    _text.Append("true");
                }

                return this;
            }

            public Writer Value(string value)
            {
                Comma();
                String(value);
                return this;
            }

            public Writer Scalar(ScalarValue value)
            {
                return Begin().Prop("kind", value.Kind.ToString()).Prop("text", value.Text).End();
            }

            public Writer Array<T>(IEnumerable<T> items, Action<Writer, T> write)
            {
                Comma();
                _text.Append('[');
                _first.Push(true);
                foreach (T item in items)
                {
                    write(this, item);
                }

                _first.Pop();
                _text.Append(']');
                return this;
            }

            public override string ToString()
            {
                return _text.ToString();
            }

            private void Comma()
            {
                if (_afterKey)
                {
                    _afterKey = false;
                    return;
                }

                if (_first.Count > 0)
                {
                    if (!_first.Peek())
                    {
                        _text.Append(',');
                    }

                    _first.Pop();
                    _first.Push(false);
                }
            }

            internal void String(string value)
            {
                _text.Append('"');
                foreach (char c in value)
                {
                    switch (c)
                    {
                        case '"': _text.Append("\\\""); break;
                        case '\\': _text.Append("\\\\"); break;
                        case '\n': _text.Append("\\n"); break;
                        case '\r': _text.Append("\\r"); break;
                        case '\t': _text.Append("\\t"); break;
                        default:
                            if (c < ' ')
                            {
                                _text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                _text.Append(c);
                            }

                            break;
                    }
                }

                _text.Append('"');
            }

            internal void Raw(string text)
            {
                Comma();
                _text.Append(text);
            }
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SourceFlow.Studio.Dsl.Json;
using SourceFlow.Studio.Dsl.Model;

namespace SourceFlow.Studio.Dsl.Simulation
{
    /// <summary>
    /// An instance's state as JSON: what the generated process manager stores between messages (WP-S.5). Values are
    /// JSON-native (null, true/false, decimal, string, lists and maps), so <see cref="Read"/> of <see cref="Write"/> is the
    /// same state.
    /// </summary>
    public static class SimulationStateJson
    {
        /// <summary>Writes <paramref name="state"/>.</summary>
        public static string Write(SimulationState state)
        {
            if (state is null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var w = new WorkflowJson.Writer();
            w.Begin()
                .Prop("instance", state.Instance)
                .Prop("status", state.Status.ToString())
                .Prop("step", state.Step)
                .Prop("phase", state.Phase.ToString())
                .Prop("attempt", state.Attempt)
                .Prop("aborting", state.Aborting)
                .Prop("compensationPending", state.CompensationPending)
                .Prop("now", state.Now.ToString("O", CultureInfo.InvariantCulture))
                .Prop("startedAt", state.StartedAt.ToString("O", CultureInfo.InvariantCulture));
            if (state.PivotPassed)
            {
                w.Prop("pivotPassed", true);   // written only once set, so instances before the pivot keep their exact form
            }

            w.Key("retries").Begin();
            foreach (KeyValuePair<string, int> retry in state.Retries.OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                w.Prop(retry.Key, retry.Value);
            }

            w.End();
            w.Key("attempts").Begin();
            foreach (KeyValuePair<string, int> attempt in state.Attempts.OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                w.Prop(attempt.Key, attempt.Value);
            }

            w.End();
            w.Key("stack").Array(state.CompensationStack, (a, step) => a.Value(step));
            w.Key("data");
            Value(w, state.Data);
            w.Key("startData");
            Value(w, state.StartData);
            if (state.Failure is { } failure)
            {
                w.Key("failure").Begin().Prop("code", failure.Code).Prop("message", failure.Message).Prop("step", failure.Step).End();
            }

            if (state.Timer is { } timer)
            {
                w.Key("timer").Begin().Prop("kind", timer.Kind).Prop("step", timer.Step).Prop("attempt", timer.Attempt)
                    .Prop("due", timer.Due.ToString("O", CultureInfo.InvariantCulture)).End();
            }

            return w.End().ToString();
        }

        /// <summary>Reads a state written by <see cref="Write"/> for <paramref name="workflow"/>.</summary>
        public static SimulationState Read(WorkflowDefinition workflow, string json, SimulationOptions? options = null)
        {
            if (workflow is null)
            {
                throw new ArgumentNullException(nameof(workflow));
            }

            JsonValue root = JsonLite.Parse(json);
            var state = new SimulationState(workflow, root["instance"]?.AsString ?? string.Empty)
            {
                Status = (WorkflowStatus)Enum.Parse(typeof(WorkflowStatus), root["status"]?.AsString ?? "Running"),
                Step = root["step"]?.AsString,
                Phase = (WorkflowPhase)Enum.Parse(typeof(WorkflowPhase), root["phase"]?.AsString ?? "Awaiting"),
                Attempt = (int)(root["attempt"]?.AsInteger ?? 1),
                Aborting = root["aborting"]?.AsBoolean ?? false,
                CompensationPending = root["compensationPending"]?.AsBoolean ?? false,
                PivotPassed = root["pivotPassed"]?.AsBoolean ?? false,
                Now = Time(root["now"]?.AsString),
                StartedAt = Time(root["startedAt"]?.AsString ?? root["now"]?.AsString),
                StartData = Map(root["startData"]),
                Options = options ?? SimulationOptions.Default,
            };
            foreach (KeyValuePair<string, JsonValue> retry in root["retries"]?.Members ?? Array.Empty<KeyValuePair<string, JsonValue>>())
            {
                state._retries[retry.Key] = (int)(retry.Value.AsInteger ?? 0);
            }

            foreach (KeyValuePair<string, JsonValue> attempt in root["attempts"]?.Members ?? Array.Empty<KeyValuePair<string, JsonValue>>())
            {
                state._attempts[attempt.Key] = (int)(attempt.Value.AsInteger ?? 0);
            }

            state._stack.AddRange((root["stack"]?.Items ?? Array.Empty<JsonValue>()).Select(i => i.AsString ?? string.Empty));
            foreach (KeyValuePair<string, object?> pair in Map(root["data"]))
            {
                state._data[pair.Key] = pair.Value;
            }

            if (root["failure"] is { } failure)
            {
                state.Failure = new WorkflowFailure(failure["code"]?.AsString ?? string.Empty, failure["message"]?.AsString ?? string.Empty, failure["step"]?.AsString ?? string.Empty);
            }

            if (root["timer"] is { } timer)
            {
                state.Timer = new SimulationTimer(timer["kind"]?.AsString ?? SimulationTimer.Timeout, timer["step"]?.AsString ?? string.Empty, (int)(timer["attempt"]?.AsInteger ?? 1), Time(timer["due"]?.AsString));
            }

            return state;
        }

        /// <summary>Converts a parsed JSON value to an engine value.</summary>
        public static object? FromJson(JsonValue? value)
        {
            if (value is null)
            {
                return null;
            }

            if (value.AsString is { } text)
            {
                return text;
            }

            if (value.AsBoolean is { } flag)
            {
                return flag;
            }

            if (value.AsDecimal is { } number)
            {
                return number;
            }

            if (value.AsNumber is { } approximate)
            {
                return (decimal)approximate;
            }

            if (value.IsObject)
            {
                return Map(value);
            }

            if (value.IsArray)
            {
                return value.Items.Select(FromJson).ToList();
            }

            return null;
        }

        /// <summary>Writes an engine value (or a CLR scalar) as JSON.</summary>
        public static string ToJson(object? value)
        {
            var w = new WorkflowJson.Writer();
            Value(w, value);
            return w.ToString();
        }

        private static Dictionary<string, object?> Map(JsonValue? value)
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, JsonValue> member in value?.Members ?? Array.Empty<KeyValuePair<string, JsonValue>>())
            {
                map[member.Key] = FromJson(member.Value);
            }

            return map;
        }

        private static DateTimeOffset Time(string? text)
        {
            return text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset time) ? time : default;
        }

        private static void Value(WorkflowJson.Writer w, object? value)
        {
            switch (value)
            {
                case null:
                    w.Raw("null");
                    break;
                case string s:
                    w.Value(s);
                    break;
                case bool b:
                    w.Raw(b ? "true" : "false");
                    break;
                case decimal d:
                    w.Raw(d.ToString(CultureInfo.InvariantCulture));
                    break;
                case int i:
                    w.Raw(i.ToString(CultureInfo.InvariantCulture));
                    break;
                case long l:
                    w.Raw(l.ToString(CultureInfo.InvariantCulture));
                    break;
                case double f:
                    w.Raw(((decimal)f).ToString(CultureInfo.InvariantCulture));
                    break;
                case IReadOnlyDictionary<string, object?> map:
                    w.Begin();
                    foreach (KeyValuePair<string, object?> pair in map)
                    {
                        w.Key(pair.Key);
                        Value(w, pair.Value);
                    }

                    w.End();
                    break;
                case IDictionary<string, object?> map:
                    w.Begin();
                    foreach (KeyValuePair<string, object?> pair in map)
                    {
                        w.Key(pair.Key);
                        Value(w, pair.Value);
                    }

                    w.End();
                    break;
                case IEnumerable list:
                    w.Array(list.Cast<object?>(), (a, item) => Value(a, item));
                    break;
                case DateTimeOffset t:
                    w.Value(t.ToString("O", CultureInfo.InvariantCulture));
                    break;
                case Guid g:
                    w.Value(g.ToString("D"));
                    break;
                case IFormattable formattable:
                    w.Value(formattable.ToString(null, CultureInfo.InvariantCulture));
                    break;
                default:
                    w.Value(value.ToString() ?? string.Empty);
                    break;
            }
        }
    }
}

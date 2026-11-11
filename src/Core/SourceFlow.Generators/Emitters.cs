using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;

namespace SourceFlow.Generators
{
    internal static class Emitters
    {
        private const string Sf = "global::SourceFlow.";

        public static string Id(IdModel model)
        {
            var w = new SourceWriter();
            w.OpenNamespace(model.Namespace);
            string self = model.Name;
            string backing = model.Backing switch
            {
                IdBackingKind.String => "string",
                IdBackingKind.Int64 => "long",
                _ => "global::System.Guid",
            };

            w.Open($"partial {model.Keyword} {self} : {Sf}IStronglyTypedId<{self}>, global::System.IComparable<{self}>, global::System.ISpanFormattable");
            switch (model.Backing)
            {
                case IdBackingKind.Guid:
                    w.Line($"/// <summary>Creates a new time-ordered (UUIDv7) id.</summary>");
                    w.Line($"public static {self} New() => new(global::System.Guid.CreateVersion7());");
                    w.Line($"/// <summary>Creates a new time-ordered (UUIDv7) id using <paramref name=\"time\"/>.</summary>");
                    w.Line($"public static {self} New(global::System.TimeProvider time) => new(global::System.Guid.CreateVersion7((time ?? throw new global::System.ArgumentNullException(nameof(time))).GetUtcNow()));");
                    w.Line("public bool IsDefault => Value == global::System.Guid.Empty;");
                    w.Line("public string ToStreamSegment() => Value.ToString(\"N\", global::System.Globalization.CultureInfo.InvariantCulture);");
                    w.Line("public override string ToString() => Value.ToString(\"D\", global::System.Globalization.CultureInfo.InvariantCulture);");
                    w.Open($"public static bool TryParse(string? value, out {self} id)");
                    w.Open("if (global::System.Guid.TryParse(value, out global::System.Guid guid) && guid != global::System.Guid.Empty)");
                    w.Line("id = new(guid);").Line("return true;").Close();
                    w.Line("id = default;").Line("return false;").Close();
                    w.Line("public string ToString(string? format, global::System.IFormatProvider? formatProvider) => Value.ToString(string.IsNullOrEmpty(format) ? \"D\" : format, formatProvider);");
                    w.Line("public bool TryFormat(global::System.Span<char> destination, out int charsWritten, global::System.ReadOnlySpan<char> format, global::System.IFormatProvider? provider) => Value.TryFormat(destination, out charsWritten, format.IsEmpty ? global::System.MemoryExtensions.AsSpan(\"D\") : format);");
                    break;
                case IdBackingKind.Int64:
                    w.Line("public bool IsDefault => Value == 0;");
                    w.Line("public string ToStreamSegment() => Value.ToString(global::System.Globalization.CultureInfo.InvariantCulture);");
                    w.Line("public override string ToString() => Value.ToString(global::System.Globalization.CultureInfo.InvariantCulture);");
                    w.Open($"public static bool TryParse(string? value, out {self} id)");
                    w.Open("if (long.TryParse(value, global::System.Globalization.NumberStyles.AllowLeadingSign, global::System.Globalization.CultureInfo.InvariantCulture, out long number) && number != 0)");
                    w.Line("id = new(number);").Line("return true;").Close();
                    w.Line("id = default;").Line("return false;").Close();
                    w.Line("public string ToString(string? format, global::System.IFormatProvider? formatProvider) => Value.ToString(format, formatProvider);");
                    w.Line("public bool TryFormat(global::System.Span<char> destination, out int charsWritten, global::System.ReadOnlySpan<char> format, global::System.IFormatProvider? provider) => Value.TryFormat(destination, out charsWritten, format, provider);");
                    break;
                default:
                    w.Line($"/// <summary>The maximum length of the id value.</summary>");
                    w.Line($"public const int MaxLength = {model.MaxLength.ToString(CultureInfo.InvariantCulture)};");
                    w.Line("public bool IsDefault => string.IsNullOrEmpty(Value);");
                    w.Open("public string ToStreamSegment()");
                    w.Line("if (IsDefault) throw new global::System.InvalidOperationException(\"The default id has no stream segment.\");");
                    w.Line("return Value;").Close();
                    w.Line("public override string ToString() => Value ?? string.Empty;");
                    w.Open($"public static bool TryParse(string? value, out {self} id)");
                    w.Open("if (IsValid(value))");
                    w.Line("id = new(value!);").Line("return true;").Close();
                    w.Line("id = default;").Line("return false;").Close();
                    w.Line("/// <summary>True when <paramref name=\"value\"/> is non-empty, at most <see cref=\"MaxLength\"/> long, and has no dash, whitespace or control character.</summary>");
                    w.Open("public static bool IsValid(string? value)");
                    w.Line("if (string.IsNullOrEmpty(value) || value!.Length > MaxLength) return false;");
                    w.Line("foreach (char c in value) { if (c == '-' || char.IsWhiteSpace(c) || char.IsControl(c)) return false; }");
                    w.Line("return true;").Close();
                    w.Line("public string ToString(string? format, global::System.IFormatProvider? formatProvider) => ToString();");
                    w.Open("public bool TryFormat(global::System.Span<char> destination, out int charsWritten, global::System.ReadOnlySpan<char> format, global::System.IFormatProvider? provider)");
                    w.Line("string text = ToString();");
                    w.Line("if (global::System.MemoryExtensions.AsSpan(text).TryCopyTo(destination)) { charsWritten = text.Length; return true; }");
                    w.Line("charsWritten = 0;").Line("return false;").Close();
                    break;
            }

            w.Line($"public static {self} Parse(string value) => TryParse(value, out {self} id) ? id : throw new global::System.FormatException($\"'{{value}}' is not a valid {self}.\");");
            string compare = model.Backing == IdBackingKind.String ? "string.CompareOrdinal(Value, other.Value)" : "Value.CompareTo(other.Value)";
            w.Line($"public int CompareTo({self} other) => {compare};");
            w.Line();
            w.Line($"/// <summary>Writes the id as a JSON scalar (GEN-19). Registered by the generated module.</summary>");
            w.Open($"public sealed class SourceFlowJsonConverter : global::System.Text.Json.Serialization.JsonConverter<{self}>");
            string read = model.Backing switch
            {
                IdBackingKind.Int64 => "new(reader.GetInt64())",
                IdBackingKind.String => $"Parse(reader.GetString() ?? throw new global::System.Text.Json.JsonException(\"{self} cannot be null.\"))",
                _ => "new(reader.GetGuid())",
            };
            string write = model.Backing == IdBackingKind.Int64 ? "writer.WriteNumberValue(value.Value);" : "writer.WriteStringValue(value.ToString());";
            w.Line($"public override {self} Read(ref global::System.Text.Json.Utf8JsonReader reader, global::System.Type typeToConvert, global::System.Text.Json.JsonSerializerOptions options) => {read};");
            w.Line($"public override void Write(global::System.Text.Json.Utf8JsonWriter writer, {self} value, global::System.Text.Json.JsonSerializerOptions options) => {write}");
            w.Line($"public override {self} ReadAsPropertyName(ref global::System.Text.Json.Utf8JsonReader reader, global::System.Type typeToConvert, global::System.Text.Json.JsonSerializerOptions options) => Parse(reader.GetString()!);");
            w.Line($"public override void WriteAsPropertyName(global::System.Text.Json.Utf8JsonWriter writer, {self} value, global::System.Text.Json.JsonSerializerOptions options) => writer.WritePropertyName(value.ToString());");
            w.Close();
            w.Close();
            w.CloseNamespace(model.Namespace);
            return w.ToString();
        }

        public static string Aggregate(AggregateModel model, IReadOnlyList<(string Command, string? Member)> targeting)
        {
            var w = new SourceWriter();
            w.OpenNamespace(model.Namespace);
            string self = model.Name;
            string contract = $"{Sf}IAggregateRuntime<{self}, {model.IdType}>";
            w.Open($"partial {model.Keyword} {self} : {contract}");
            w.Line($"/// <summary>The aggregate (stream type) name.</summary>");
            w.Line($"public const string AggregateName = {Literal.Of(model.AggregateName)};");
            w.Line($"private static readonly global::System.Type[] __sourceFlowDecided = [{string.Join(", ", model.DecidedCommands.Select(c => $"typeof({c})"))}];");
            w.Line($"private static readonly global::System.Type[] __sourceFlowTargeting = [{string.Join(", ", targeting.Select(c => $"typeof({c.Command})"))}];");
            w.Line($"static string {contract}.AggregateName => AggregateName;");
            w.Line($"static int {contract}.SnapshotEvery => {model.SnapshotEvery.ToString(CultureInfo.InvariantCulture)};");
            w.Line($"static global::System.Collections.Generic.IReadOnlyList<global::System.Type> {contract}.DecidedCommands => __sourceFlowDecided;");
            w.Line($"static global::System.Collections.Generic.IReadOnlyList<global::System.Type> {contract}.TargetingCommands => __sourceFlowTargeting;");
            w.Line();
            w.Open($"static {Sf}Decision {contract}.Decide({self} state, {Sf}ICommand command, in {Sf}DecisionContext context)");
            w.Open("return command switch");
            foreach (string command in model.DecidedCommands)
            {
                w.Line($"{command} c => state.Decide(c, context),");
            }

            w.Line($"_ => throw new {Sf}UnhandledMessageException(command.GetType()),");
            w.Close(";");
            w.Close();
            w.Line();
            w.Open($"static {model.IdType} {contract}.TargetOf({Sf}ICommand command)");
            w.Open("return command switch");
            foreach ((string command, string? member) in targeting)
            {
                if (member is not null)
                {
                    w.Line($"{command} c => c.{member},");
                }
            }

            w.Line($"_ => throw new {Sf}UnhandledMessageException(command.GetType()),");
            w.Close(";");
            w.Close();
            w.Line();
            w.Line("/// <summary>Applies an event. Unknown events leave the state unchanged (tolerant evolve).</summary>");
            w.Open($"public {self} Evolve({Sf}IEvent @event)");
            w.Open("return @event switch");
            foreach (string e in model.EvolvedEvents)
            {
                w.Line($"{e} e => When(e),");
            }

            w.Line("_ => this,");
            w.Close(";");
            w.Close();
            w.Close();
            w.CloseNamespace(model.Namespace);
            return w.ToString();
        }

        public static string ProcessManager(ProcessManagerModel model)
        {
            var w = new SourceWriter();
            w.OpenNamespace(model.Namespace);
            string self = model.Name;
            string contract = $"{Sf}IProcessManagerRuntime<{self}, {model.IdType}>";
            w.Open($"partial {model.Keyword} {self} : {contract}");
            w.Line($"/// <summary>The process type name.</summary>");
            w.Line($"public const string ProcessName = {Literal.Of(model.ProcessName)};");
            w.Line($"private static readonly global::System.Type[] __sourceFlowHandled = [{string.Join(", ", model.ReactedMessages.Select(c => $"typeof({c})"))}];");
            w.Line($"static string {contract}.ProcessName => ProcessName;");
            w.Line($"static global::System.Collections.Generic.IReadOnlyList<global::System.Type> {contract}.HandledMessages => __sourceFlowHandled;");
            w.Line();
            w.Open($"static bool {contract}.TryCorrelate({Sf}IMessage message, out {model.IdType} id)");
            w.Open("switch (message)");
            foreach (string m in model.CorrelatedMessages)
            {
                w.Line($"case {m} m: id = CorrelateBy(m); return true;");
            }

            w.Line("default: id = default; return false;");
            w.Close();
            w.Close();
            w.Line();
            w.Open($"public {Sf}Reaction React({Sf}IMessage message, {Sf}ReactionContext<{model.IdType}> context)");
            w.Open("return message switch");
            foreach (string m in model.ReactedMessages)
            {
                w.Line($"{m} m => React(m, context),");
            }

            w.Line($"_ => {Sf}Reaction.Ignore,");
            w.Close(";");
            w.Close();
            w.Close();
            w.CloseNamespace(model.Namespace);
            return w.ToString();
        }

        public static string Projection(ProjectionModel model)
        {
            var w = new SourceWriter();
            w.OpenNamespace(model.Namespace);
            string self = model.Name;
            string contract = $"{Sf}IDocumentProjectionRuntime<{self}, {model.DocumentType}, {model.KeyType}>";
            w.Open($"partial {model.Keyword} {self} : {contract}");
            w.Line($"/// <summary>The projection (collection) name.</summary>");
            w.Line($"public const string ProjectionName = {Literal.Of(model.ProjectionName)};");
            w.Line($"private static readonly global::System.Type[] __sourceFlowHandled = [{string.Join(", ", model.Events.Select(e => $"typeof({e.EventType})"))}];");
            w.Line($"static string {contract}.ProjectionName => ProjectionName;");
            w.Line($"static int {contract}.ProjectionVersion => {model.Version.ToString(CultureInfo.InvariantCulture)};");
            w.Line($"static global::System.Collections.Generic.IReadOnlyList<global::System.Type> {contract}.HandledEvents => __sourceFlowHandled;");
            w.Line();
            string fallback = model.KeyKind switch
            {
                KeyKind.StronglyTypedId => $"return {model.KeyType}.TryParse(context.AggregateKey, out key);",
                KeyKind.String => "key = context.AggregateKey; return key.Length > 0;",
                _ => "key = default!; return false;",
            };
            w.Open($"static bool {contract}.TryGetKey({Sf}IEvent @event, in {Sf}EventContext context, out {model.KeyType} key)");
            w.Open("switch (@event)");
            foreach (ProjectedEvent e in model.Events)
            {
                switch (e.KeySource)
                {
                    case KeySource.KeyOf:
                        w.Line($"case {e.EventType} e: key = KeyOf(e); return true;");
                        break;
                    case KeySource.Member:
                        w.Line($"case {e.EventType} e: key = e.{e.Member}; return true;");
                        break;
                }
            }

            w.Line($"default: {fallback}");
            w.Close();
            w.Close();
            string format = model.KeyKind switch
            {
                KeyKind.StronglyTypedId => "key.ToStreamSegment()",
                KeyKind.String => "key",
                _ => "key.ToString() ?? string.Empty",
            };
            w.Line($"static string {contract}.FormatKey({model.KeyType} key) => {format};");
            w.Line();
            w.Open($"public {model.DocumentType}? Apply({Sf}IEvent @event, {model.DocumentType}? current, in {Sf}EventContext context)");
            w.Open("return @event switch");
            foreach (ProjectedEvent e in model.Events)
            {
                w.Line($"{e.EventType} e => When(e, current, context),");
            }

            w.Line("_ => current,");
            w.Close(";");
            w.Close();
            w.Close();
            w.CloseNamespace(model.Namespace);
            return w.ToString();
        }

        public static string Module(
            ModuleModel module,
            ImmutableArray<IdModel> ids,
            ImmutableArray<MessageModel> messages,
            ImmutableArray<AggregateModel> aggregates,
            ImmutableArray<ProcessManagerModel> processes,
            ImmutableArray<ProjectionModel> projections,
            ImmutableArray<HandlerModel> handlers)
        {
            var w = new SourceWriter();
            w.OpenNamespace(module.Namespace);
            w.Open($"partial class {module.Name} : {Sf}ISourceFlowModule");
            w.Line($"/// <summary>The module name.</summary>");
            w.Line($"public static string ModuleName => {Literal.Of(module.Name)};");
            w.Line();
            w.Line("/// <summary>Registers everything found in this assembly at compile time (no scanning, GAP-16).</summary>");
            w.Open($"public static void Register({Sf}ISourceFlowModuleBuilder builder)");
            w.Line("global::System.ArgumentNullException.ThrowIfNull(builder);");
            if (module.JsonContext is not null)
            {
                w.Line($"builder.AddJsonTypeInfoResolver({module.JsonContext}.Default);");
            }

            foreach (IdModel id in ids.OrderBy(i => i.FullName, System.StringComparer.Ordinal))
            {
                w.Line($"builder.AddJsonConverter(new {id.FullName}.SourceFlowJsonConverter());");
            }

            foreach (MessageModel m in messages.OrderBy(m => m.LogicalName, System.StringComparer.Ordinal).ThenBy(m => m.Version))
            {
                w.Line($"builder.AddMessage<{m.FullName}>({Literal.Of(m.LogicalName)}, {m.Version.ToString(CultureInfo.InvariantCulture)});");
            }

            foreach (HandlerModel h in handlers.Where(h => h.Kind == HandlerKind.Upcaster).OrderBy(h => h.FullName, System.StringComparer.Ordinal))
            {
                w.Line($"builder.AddUpcaster<{h.First}, {h.Second}, {h.FullName}>();");
            }

            foreach (AggregateModel a in aggregates.OrderBy(a => a.FullName, System.StringComparer.Ordinal))
            {
                w.Line($"builder.AddAggregate<{a.FullName}, {a.IdType}>();");
            }

            foreach (HandlerModel h in handlers.Where(h => h.Kind == HandlerKind.Command).OrderBy(h => h.FullName, System.StringComparer.Ordinal))
            {
                w.Line($"builder.AddCommandHandler<{h.FullName}, {h.First}, {h.Second}>();");
            }

            foreach (ProcessManagerModel p in processes.OrderBy(p => p.FullName, System.StringComparer.Ordinal))
            {
                w.Line($"builder.AddProcessManager<{p.FullName}, {p.IdType}>();");
            }

            foreach (ProjectionModel p in projections.OrderBy(p => p.FullName, System.StringComparer.Ordinal))
            {
                w.Line($"builder.AddProjection<{p.FullName}, {p.DocumentType}, {p.KeyType}>();");
            }

            foreach (HandlerModel h in handlers.Where(h => h.Kind == HandlerKind.Event).OrderBy(h => h.FullName, System.StringComparer.Ordinal).ThenBy(h => h.First, System.StringComparer.Ordinal))
            {
                w.Line($"builder.AddEventHandler<{h.FullName}, {h.First}>();");
            }

            w.Close();
            w.Close();
            w.CloseNamespace(module.Namespace);
            return w.ToString();
        }
    }
}

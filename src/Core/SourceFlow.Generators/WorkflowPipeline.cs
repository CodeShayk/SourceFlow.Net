using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using SourceFlow.Studio.Dsl;
using SourceFlow.Studio.Dsl.Diagnostics;
using SourceFlow.Studio.Dsl.Manifests;
using SourceFlow.Studio.Dsl.Model;
using SourceFlow.Studio.Dsl.Validation;

namespace SourceFlow.Generators
{
    // DSL workflows (doc 15 §5.5, WP-S.5): *.sfflow.yaml → validated with the shared DSL core (SF0020) → a generated
    // process manager that runs the shared engine, plus its module registration and manifest entry.

    internal sealed record WorkflowFile(string Path, string Text);

    internal sealed record MessageEntry(string Ref, string ClrType, string Kind, EquatableArray<MemberEntry> Members);

    internal sealed record MemberEntry(string JsonName, string Property);

    internal sealed record WorkflowContext(
        string AssemblyName,
        EquatableArray<MessageEntry> Messages,
        EquatableArray<string> ReferencedManifests,
        EquatableArray<string> UserMethods);

    internal sealed record WorkflowOptions(string? RootNamespace, string? ServiceName, string? ProjectDir);

    internal sealed record WorkflowDiagnostic(string Id, string Message, bool IsError, string Path, int Line, int Column, string Rule);

    internal sealed record WorkflowRegistration(string FullType, string Owner, EquatableArray<RejectionModel> Rejections);

    internal sealed record WorkflowManifestEntry(string Name, int Version, string File, string Fingerprint);

    internal sealed record WorkflowOutput(
        string Path,
        EquatableArray<WorkflowDiagnostic> Diagnostics,
        string? HintName,
        string? Source,
        WorkflowRegistration? Registration,
        WorkflowManifestEntry? Manifest);

    /// <summary>Names shared with <c>SourceFlow.Workflows.WorkflowRuntime</c> (the generator does not reference the core).</summary>
    internal static class WorkflowRuntimeNames
    {
        public const string InstancesQuerySuffix = ".workflow-instances";
    }

    internal static class WorkflowPipeline
    {
        public const string Suffix = ".sfflow.yaml";
        private const string Sf = "global::SourceFlow.";
        private const string Wf = "global::SourceFlow.Workflows.";

        /// <summary>The message types visible to the compilation: its own and those of assemblies built on SourceFlow.</summary>
        public static WorkflowContext Context(Compilation compilation, bool hasWorkflows, CancellationToken cancellationToken)
        {
            if (!hasWorkflows)
            {
                return new WorkflowContext(compilation.AssemblyName ?? string.Empty, EquatableArray<MessageEntry>.Empty, EquatableArray<string>.Empty, EquatableArray<string>.Empty);
            }

            var messages = new List<MessageEntry>();
            var manifests = new List<string>();
            var methods = new List<string>();
            foreach (IAssemblySymbol assembly in new[] { compilation.Assembly }.Concat(compilation.SourceModule.ReferencedAssemblySymbols))
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool own = SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly);
                if (!own && assembly.Name != "SourceFlow.Net" && !assembly.Modules.Any(m => m.ReferencedAssemblies.Any(r => r.Name == "SourceFlow.Net")))
                {
                    continue;
                }

                if (!own)
                {
                    foreach (AttributeData attribute in assembly.GetAttributes())
                    {
                        if (attribute.AttributeClass.Is(ManifestBuilder.ManifestAttribute) && attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is string json)
                        {
                            manifests.Add(json);
                        }
                    }
                }

                foreach (INamedTypeSymbol type in Types(assembly.GlobalNamespace))
                {
                    if (own && type.IsPartial())
                    {
                        foreach (IMethodSymbol method in type.GetMembers().OfType<IMethodSymbol>().Where(m => m.IsStatic && m.Parameters.Length == 1))
                        {
                            methods.Add(type.FullName() + "." + method.Name);
                        }
                    }

                    if (type.IsAbstract || (!own && type.DeclaredAccessibility != Accessibility.Public)
                        || ManifestAnalysis.Ref(type) is not { } reference || ModelBuilder.KindOf(type) is not { } kind)
                    {
                        continue;
                    }

                    var members = new List<MemberEntry>();
                    foreach (IPropertySymbol property in type.GetMembers().OfType<IPropertySymbol>().Where(p => !p.IsStatic && p.DeclaredAccessibility == Accessibility.Public && p.Name != "EqualityContract"))
                    {
                        string json = property.FindAttribute("System.Text.Json.Serialization.JsonPropertyNameAttribute") is { ConstructorArguments.Length: 1 } renamed && renamed.ConstructorArguments[0].Value is string explicitName
                            ? explicitName
                            : Camel(property.Name);
                        members.Add(new MemberEntry(json, property.Name));
                    }

                    messages.Add(new MessageEntry(reference, type.FullName(), kind, members.ToEquatableArray()));
                }
            }

            return new WorkflowContext(
                compilation.AssemblyName ?? string.Empty,
                messages.GroupBy(m => m.Ref).Select(g => g.First()).OrderBy(m => m.Ref, StringComparer.Ordinal).ToEquatableArray(),
                manifests.OrderBy(m => m, StringComparer.Ordinal).ToEquatableArray(),
                methods.Distinct().OrderBy(m => m, StringComparer.Ordinal).ToEquatableArray());
        }

        public static WorkflowOptions Options(AnalyzerConfigOptionsProvider provider, CancellationToken cancellationToken)
        {
            provider.GlobalOptions.TryGetValue("build_property.RootNamespace", out string? root);
            provider.GlobalOptions.TryGetValue("build_property.SourceFlowServiceName", out string? service);
            provider.GlobalOptions.TryGetValue("build_property.ProjectDir", out string? dir);
            return new WorkflowOptions(Blank(root), Blank(service), Blank(dir));
        }

        public static WorkflowOutput Build(
            WorkflowFile file,
            ImmutableArray<WorkflowFile> all,
            WorkflowContext context,
            string? ownManifest,
            ImmutableArray<ModuleModel> modules,
            WorkflowOptions options,
            CancellationToken cancellationToken)
        {
            var diagnostics = new List<WorkflowDiagnostic>();
            var manifests = new List<ServiceManifest>();
            foreach (string json in (ownManifest is null ? Enumerable.Empty<string>() : new[] { ownManifest }).Concat(context.ReferencedManifests))
            {
                try
                {
                    manifests.Add(ServiceManifest.Parse(json));
                }
                catch (FormatException)
                {
                    // a manifest of another SourceFlow version: ignored
                }
            }

            var siblings = all.Where(f => f.Path != file.Path)
                .Select(f => SourceFlow.Studio.Dsl.Reading.WorkflowReader.Read(f.Text).Workflow)
                .Where(w => w is not null)
                .Select(w => w!)
                .ToList();
            cancellationToken.ThrowIfCancellationRequested();
            WorkflowAnalysisResult analysis = WorkflowAnalysis.Analyze(file.Text, new WorkflowEnvironment(manifests, siblings, options.ServiceName, partial: true));
            foreach (SourceFlow.Studio.Dsl.Diagnostics.WorkflowDiagnostic d in analysis.Diagnostics)
            {
                diagnostics.Add(new WorkflowDiagnostic("SF0020", d.Id + ": " + d.Message, d.IsError, file.Path, d.Location.Line, d.Location.Column, d.Id));
            }

            WorkflowDefinition? workflow = analysis.Workflow;
            if (workflow is null || analysis.HasErrors)
            {
                return new WorkflowOutput(file.Path, diagnostics.ToEquatableArray(), null, null, null, null);
            }

            ModuleModel? module = modules.OrderBy(m => m.FullName, StringComparer.Ordinal).FirstOrDefault();
            if (module is null)
            {
                diagnostics.Add(new WorkflowDiagnostic("SF0020", "The workflow needs a [SourceFlowModule] in this project to be registered.", true, file.Path, 1, 1, "WF-V02"));
                return new WorkflowOutput(file.Path, diagnostics.ToEquatableArray(), null, null, null, null);
            }

            var types = context.Messages.ToDictionary(m => m.Ref, StringComparer.Ordinal);
            MessageEntry? Resolve(string reference, SourceLocation at)
            {
                if (types.TryGetValue(reference, out MessageEntry? entry))
                {
                    return entry;
                }

                diagnostics.Add(new WorkflowDiagnostic("SF0020", "WF-V01: " + reference + " has no CLR type in this compilation; reference the contract package that declares it.", true, file.Path, at.Line, at.Column, "WF-V01"));
                return null;
            }

            string className = Pascal(workflow.Name) + "WorkflowV" + workflow.Version.ToString(CultureInfo.InvariantCulture);
            string ns = module.Namespace.Length > 0 ? module.Namespace : options.RootNamespace ?? string.Empty;
            string fullType = "global::" + (ns.Length > 0 ? ns + "." : string.Empty) + className;
            string correlation = workflow.Start.Correlate.Substring(workflow.Start.Correlate.LastIndexOf('.') + 1);

            MessageEntry? start = Resolve(workflow.Start.On, workflow.Start.At("on"));
            var events = new SortedDictionary<string, MessageEntry>(StringComparer.Ordinal);
            var commands = new SortedDictionary<string, MessageEntry>(StringComparer.Ordinal);
            var rejections = new SortedDictionary<string, MessageEntry>(StringComparer.Ordinal);
            var hooks = new SortedSet<string>(StringComparer.Ordinal);
            void Send(SendSpec? send)
            {
                if (send is null)
                {
                    return;
                }

                if (Resolve(send.Command, send.At("command")) is { } command)
                {
                    commands[send.Command] = command;
                }

                foreach (MapEntry entry in send.Map.Where(e => e.Value.Kind == MapValueKind.Hook))
                {
                    hooks.Add(entry.Value.Text!);
                }
            }

            // CR-32 (doc 15 §5.3): every awaited command's rejection reaches the workflow, declared or not (an undeclared one
            // goes to ManualIntervention), and so does a compensation's (a rejected compensation is a failed attempt).
            void Rejectable(SendSpec? send)
            {
                if (send is not null && types.TryGetValue(send.Command, out MessageEntry? rejected))
                {
                    rejections[send.Command] = rejected;
                }
            }

            foreach (WorkflowStep step in workflow.Steps)
            {
                Send(step.Send);
                if (step.Await is not null)
                {
                    Rejectable(step.Send);
                }

                if (step.Await is { } wait)
                {
                    if (Resolve(wait.Success.On, wait.Success.At("on")) is { } success)
                    {
                        events[wait.Success.On] = success;
                    }

                    foreach (FailureSpec failure in wait.Failures)
                    {
                        if (failure.On is { } on && Resolve(on, failure.At("on")) is { } failed)
                        {
                            events[on] = failed;
                        }
                    }
                }

                if (step.Compensate is { None: false } compensate)
                {
                    Rejectable(compensate.Send);
                    Send(compensate.Send);
                    if (compensate.Await?.Success is { } undone && Resolve(undone.On, undone.At("on")) is { } compensated)
                    {
                        events[undone.On] = compensated;
                    }
                }
            }

            Send(workflow.On.Completed);
            Send(workflow.On.Compensated);
            foreach (string hook in hooks)
            {
                if (!context.UserMethods.Contains(fullType + "." + hook))
                {
                    SourceLocation at = HookLocation(workflow, hook);
                    diagnostics.Add(new WorkflowDiagnostic("SF0021", "The workflow hook '" + hook + "' has no implementation: add 'private static partial object? " + hook + "(IReadOnlyDictionary<string, object?> data)' to partial record " + className + ".", true, file.Path, at.Line, at.Column, "SF0021"));
                }
            }

            if (start is null || diagnostics.Any(d => d.IsError))
            {
                return new WorkflowOutput(file.Path, diagnostics.ToEquatableArray(), null, null, null, null);
            }

            events.Remove(workflow.Start.On);
            string source = Emit(workflow, ns, className, start, correlation, events, commands, rejections, hooks.Where(h => context.UserMethods.Contains(fullType + "." + h)));
            string relative = Relative(file.Path, options.ProjectDir);
            var registration = new WorkflowRegistration(
                fullType,
                workflow.Owner,
                rejections.Values.Select(r => new RejectionModel(Sf + "CommandRejected<" + r.ClrType + ">", Name(r.Ref) + ".rejected", Version(r.Ref))).ToEquatableArray());
            return new WorkflowOutput(
                file.Path,
                diagnostics.ToEquatableArray(),
                className + ".Workflow.g.cs",
                source,
                registration,
                new WorkflowManifestEntry(workflow.Name, workflow.Version, relative, WorkflowFingerprint.Compute(workflow)));
        }

        private static string Emit(
            WorkflowDefinition workflow,
            string ns,
            string className,
            MessageEntry start,
            string correlation,
            SortedDictionary<string, MessageEntry> events,
            SortedDictionary<string, MessageEntry> commands,
            SortedDictionary<string, MessageEntry> rejections,
            IEnumerable<string> hooks)
        {
            string id = Wf + "WorkflowInstanceId";
            string contract = $"{Sf}IProcessManagerRuntime<{className}, {id}>";
            var w = new SourceWriter();
            w.OpenNamespace(ns);
            w.Line($"/// <summary>The DSL workflow {Xml(workflow.ProcessName)} (generated from its .sfflow.yaml).</summary>");
            w.Line($"[{Sf}ProcessManager({Literal.Of(workflow.ProcessName)})]");
            w.Line("[global::System.CodeDom.Compiler.GeneratedCode(\"SourceFlow.Generators\", \"3\")]");
            w.Open($"public sealed partial record {className} : {contract}, {Wf}IWorkflowState<{className}>");
            w.Line("/// <summary>The process type name.</summary>");
            w.Line($"public const string ProcessName = {Literal.Of(workflow.ProcessName)};");
            w.Line();
            w.Line($"internal const string Definition = {Literal.Of(WorkflowJson.Write(workflow))};");
            w.Line();
            w.Line($"internal static readonly global::SourceFlow.Workflows.Engine.WorkflowProcess<{className}> Process = new(Definition, ProcessName, CreateCommand, Hook);");
            w.Line();
            var handled = new List<string> { start.ClrType };
            handled.AddRange(events.Values.Select(e => e.ClrType));
            handled.AddRange(rejections.Values.Select(r => Sf + "CommandRejected<" + r.ClrType + ">"));
            handled.Add(Wf + "WorkflowTimer");
            handled.Add(Wf + "WorkflowControl");
            w.Line($"private static readonly global::System.Type[] __handled = [{string.Join(", ", handled.Distinct().Select(h => "typeof(" + h + ")"))}];");
            w.Line();
            w.Line("/// <summary>The initial (not started) state.</summary>");
            w.Line($"public static {className} Initial {{ get; }} = new();");
            w.Line();
            w.Line("/// <summary>The engine state as JSON.</summary>");
            w.Line("public string? State { get; init; }");
            w.Line();
            w.Line("/// <summary>The status, or null before the start message.</summary>");
            w.Line($"public {Wf}WorkflowStatus? Status => Process.Status(this);");
            w.Line();
            w.Line($"static {className} {Wf}IWorkflowState<{className}>.FromState(string? state) => new() {{ State = state }};");
            w.Line($"{Wf}WorkflowInstanceInfo? {Wf}IWorkflowInstance.Describe() => Process.Describe(this);");
            w.Line($"static string {contract}.ProcessName => ProcessName;");
            w.Line($"static global::System.Collections.Generic.IReadOnlyList<global::System.Type> {contract}.HandledMessages => __handled;");
            w.Line();
            w.Open($"static bool {contract}.TryCorrelate({Sf}IMessage message, out {id} id)");
            w.Open("switch (message)");
            Correlate(w, start, correlation, id);
            foreach (MessageEntry e in events.Values)
            {
                Correlate(w, e, correlation, id);
            }

            w.Line($"case {Wf}WorkflowTimer m when m.Workflow == ProcessName: id = {id}.From(m.Instance); return true;");
            w.Line($"case {Wf}WorkflowControl m when m.Workflow == ProcessName: id = {id}.From(m.Instance); return true;");
            w.Line("default: id = default; return false;");
            w.Close();
            w.Close();
            w.Line();
            w.Open($"public {Sf}Reaction React({Sf}IMessage message, {Sf}ReactionContext<{id}> context)");
            w.Open("return message switch");
            w.Line($"{start.ClrType} m => Process.Start(this, m, context),");
            foreach (MessageEntry e in events.Values)
            {
                w.Line($"{e.ClrType} m => Process.Receive(this, {Literal.Of(e.Ref)}, m, context),");
            }

            foreach (MessageEntry r in rejections.Values)
            {
                w.Line($"{Sf}CommandRejected<{r.ClrType}> m => Process.Rejected(this, {Literal.Of(r.Ref)}, m.Error.Code, m.Error.Message, context),");
            }

            w.Line($"{Wf}WorkflowTimer m when m.Workflow == ProcessName => Process.Timer(this, m, context),");
            w.Line($"{Wf}WorkflowControl m when m.Workflow == ProcessName => Process.Control(this, m, context),");
            w.Line($"_ => {Sf}Reaction.Ignore,");
            w.Close(";");
            w.Close();
            w.Line();
            w.Open($"private static {Sf}ICommand CreateCommand(string type, string json, global::System.Text.Json.JsonSerializerOptions options)");
            w.Open("return type switch");
            foreach (MessageEntry c in commands.Values)
            {
                w.Line($"{Literal.Of(c.Ref)} => ({Sf}ICommand)global::System.Text.Json.JsonSerializer.Deserialize(json, options.GetTypeInfo(typeof({c.ClrType})))!,");
            }

            w.Line("_ => throw new global::System.InvalidOperationException(\"Unknown workflow command \" + type + \".\"),");
            w.Close(";");
            w.Close();
            w.Line();
            List<string> implemented = hooks.ToList();
            w.Open("private static object? Hook(string name, global::System.Collections.Generic.IReadOnlyDictionary<string, object?> data)");
            w.Open("return name switch");
            foreach (string hook in implemented)
            {
                w.Line($"{Literal.Of(hook)} => {hook}(data),");
            }

            w.Line("_ => null,");
            w.Close(";");
            w.Close();
            foreach (string hook in implemented)
            {
                w.Line();
                w.Line($"/// <summary>The <c>$hook: {hook}</c> of the workflow: pure and deterministic.</summary>");
                w.Line($"private static partial object? {hook}(global::System.Collections.Generic.IReadOnlyDictionary<string, object?> data);");
            }

            w.Close();
            w.CloseNamespace(ns);
            return w.ToString();
        }

        private static void Correlate(SourceWriter w, MessageEntry message, string correlation, string id)
        {
            if (message.Members.FirstOrDefault(m => m.JsonName == correlation) is { } member)
            {
                w.Line($"case {message.ClrType} m: id = {id}.From({Wf}WorkflowRuntime.CorrelationValue(m.{member.Property})); return !id.IsDefault;");
            }
        }

        /// <summary>The generated domain query's class name.</summary>
        public const string InstancesQueryClass = "WorkflowInstancesQuery";

        /// <summary>
        /// The module part that registers the workflows, the shared messages and the replies, and the domain query
        /// <c>&lt;service&gt;.workflow-instances</c> v1 over the instances' read model (doc 15 §6.1, §7).
        /// </summary>
        public static string Registration(ModuleModel module, IEnumerable<WorkflowRegistration> workflows, string? serviceName)
        {
            WorkflowRegistration[] all = workflows.OrderBy(x => x.FullType, StringComparer.Ordinal).ToArray();
            string service = serviceName ?? all.Select(x => x.Owner).Where(o => o.Length > 0).OrderBy(o => o, StringComparer.Ordinal).FirstOrDefault() ?? "service";
            string query = InstancesQueryClass;
            string result = Wf + "WorkflowInstanceSummary";
            string contract = $"{Sf}IDomainQueryRuntime<{query}, {result}>";
            var w = new SourceWriter();
            w.OpenNamespace(module.Namespace);
            w.Open($"partial class {module.Name}");
            w.Open($"static partial void RegisterWorkflows({Sf}ISourceFlowModuleBuilder builder)");
            w.Line($"{Wf}WorkflowRuntime.RegisterMessages(builder);");
            foreach (WorkflowRegistration workflow in all)
            {
                w.Line($"builder.AddJsonTypeInfoResolver({Wf}WorkflowRuntime.StateResolver<{workflow.FullType}>());");
                foreach (RejectionModel r in workflow.Rejections)
                {
                    w.Line($"builder.AddMessage<{r.FullName}>({Literal.Of(r.LogicalName)}, {r.Version.ToString(CultureInfo.InvariantCulture)});");
                }

                w.Line($"builder.AddProcessManager<{workflow.FullType}, {Wf}WorkflowInstanceId>();");
            }

            w.Line($"builder.AddDomainQuery<{query}, {result}>();");
            w.Close();
            w.Close();
            w.Line();
            w.Line($"/// <summary>The domain query {Xml(service + WorkflowRuntimeNames.InstancesQuerySuffix)} v1: one workflow's instances, newest first (generated).</summary>");
            w.Line("/// <param name=\"Workflow\">The workflow name, without the version.</param>");
            w.Line("[global::System.CodeDom.Compiler.GeneratedCode(\"SourceFlow.Generators\", \"3\")]");
            w.Open($"public sealed record {query}(string Workflow) : {contract}");
            w.Line($"/// <summary>The query name.</summary>");
            w.Line($"public const string QueryName = {Literal.Of(service + WorkflowRuntimeNames.InstancesQuerySuffix)};");
            w.Line();
            w.Line($"static string {contract}.QueryName => QueryName;");
            w.Line($"static int {contract}.QueryVersion => 1;");
            w.Line($"static string {contract}.QueryText => {Wf}WorkflowRuntime.InstancesQueryText;");
            w.Line($"static bool {contract}.IsSql => false;");
            w.Line($"static global::System.Collections.Generic.IReadOnlyList<{Sf}DomainQueryParameter> {contract}.QueryParameters => {Wf}WorkflowRuntime.InstancesQueryParameters;");
            w.Line($"static global::System.Collections.Generic.IReadOnlyList<string> {contract}.ResultColumns => {Wf}WorkflowRuntime.InstancesQueryColumns;");
            w.Line($"global::System.Collections.Generic.IReadOnlyDictionary<string, object?> {contract}.ToParameters() => new global::System.Collections.Generic.Dictionary<string, object?>(global::System.StringComparer.Ordinal) {{ [\"workflow\"] = Workflow }};");
            w.Line($"static {result} {contract}.Materialize({Sf}IQueryRow row) => {Wf}WorkflowRuntime.MaterializeInstance(row);");
            w.Close();
            w.CloseNamespace(module.Namespace);
            return w.ToString();
        }

        /// <summary>The engine sources, injected once as internal types in <c>SourceFlow.Workflows.Engine</c>.</summary>
        public static IEnumerable<(string HintName, string Source)> Engine()
        {
            Assembly assembly = typeof(WorkflowPipeline).Assembly;
            foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Engine/", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
            {
                using Stream stream = assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                string text = reader.ReadToEnd();
                string name = resource.Substring("Engine/".Length);
                if (!name.EndsWith("WorkflowBridge.cs", StringComparison.Ordinal))
                {
                    text = Internalise(text);
                }

                yield return ("SourceFlow.Workflows.Engine." + Path.GetFileNameWithoutExtension(name) + ".g.cs", text);
            }
        }

        private static readonly Regex PublicType = new Regex(@"(?m)^(\s*)public (?=(?:(?:static|sealed|abstract|readonly|partial)\s+)*(?:class|record|struct|enum|interface)\b)", RegexOptions.CultureInvariant);

        // The DSL sources as generated engine code: own namespace, internal types, marked generated.
        internal static string Internalise(string source)
        {
            string text = source.Replace("SourceFlow.Studio.Dsl", "SourceFlow.Workflows.Engine");
            text = PublicType.Replace(text, "$1internal ");
            return "// <auto-generated/>\n// The SourceFlow workflow engine (SourceFlow.Studio/Dsl), injected by SourceFlow.Generators. Do not edit.\n#nullable enable\n#pragma warning disable\n" + text;
        }

        private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol root)
        {
            foreach (INamespaceOrTypeSymbol member in root.GetMembers())
            {
                if (member is INamespaceSymbol ns)
                {
                    foreach (INamedTypeSymbol nested in Types(ns))
                    {
                        yield return nested;
                    }
                }
                else if (member is INamedTypeSymbol type)
                {
                    yield return type;
                }
            }
        }

        public static Location Location(WorkflowDiagnostic diagnostic, SourceText? text)
        {
            int line = Math.Max(diagnostic.Line, 1) - 1;
            int column = Math.Max(diagnostic.Column, 1) - 1;
            var position = new LinePosition(line, column);
            TextSpan span = text is not null && line < text.Lines.Count
                ? new TextSpan(Math.Min(text.Lines[line].Start + column, text.Lines[line].End), 0)
                : default;
            return Microsoft.CodeAnalysis.Location.Create(diagnostic.Path, span, new LinePositionSpan(position, position));
        }

        private static SourceLocation HookLocation(WorkflowDefinition workflow, string hook)
        {
            foreach (SendSpec send in workflow.Steps.SelectMany(s => new[] { s.Send, s.Compensate?.Send }).Concat(new[] { workflow.On.Completed, workflow.On.Compensated }).Where(s => s is not null)!)
            {
                foreach (MapEntry entry in send.Map)
                {
                    if (entry.Value.Kind == MapValueKind.Hook && entry.Value.Text == hook)
                    {
                        return entry.At("value");
                    }
                }
            }

            return workflow.Location;
        }

        private static string Relative(string path, string? projectDir)
        {
            string normal = path.Replace('\\', '/');
            if (projectDir is not null)
            {
                string root = projectDir.Replace('\\', '/').TrimEnd('/') + "/";
                if (normal.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    return normal.Substring(root.Length);
                }
            }

            return normal.Substring(normal.LastIndexOf('/') + 1);
        }

        private static string Name(string reference)
        {
            return reference.Substring(0, reference.LastIndexOf(".v", StringComparison.Ordinal));
        }

        private static int Version(string reference)
        {
            return int.Parse(reference.Substring(reference.LastIndexOf(".v", StringComparison.Ordinal) + 2), CultureInfo.InvariantCulture);
        }

        internal static string Pascal(string name)
        {
            var text = new StringBuilder();
            bool upper = true;
            foreach (char c in name)
            {
                if (!char.IsLetterOrDigit(c))
                {
                    upper = true;
                    continue;
                }

                text.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }

            return text.ToString();
        }

        private static string Camel(string name)
        {
            return ManifestBuilder.CamelCase(name);
        }

        private static string? Blank(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static string Xml(string text)
        {
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}

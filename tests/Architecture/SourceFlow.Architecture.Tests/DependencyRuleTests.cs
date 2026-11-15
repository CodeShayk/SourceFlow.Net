using System.Reflection;
using NetArchTest.Rules;

namespace SourceFlow.Architecture.Tests;

/// <summary>Dependency rules from ADR-027 §4 and plan 06 §9.</summary>
[Trait("Category", "Architecture")]
public sealed class DependencyRuleTests
{
    private static readonly string[] AllowedCorePrefixes =
    [
        "System.",
        "System",
        "Microsoft.Extensions.",
        "Polly",
        "netstandard",
        "mscorlib",
        Packages.Core,
    ];

    [Fact]
    [Trait("Id", "ARCH-01")]
    [Trait("Covers", "ARCH-01;ADR-027")]
    public void Core_references_only_the_BCL_Microsoft_Extensions_and_Polly()
    {
        Assembly core = Packages.Load(Packages.Core);

        string[] offending = core.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => !AllowedCorePrefixes.Any(prefix => name == prefix.TrimEnd('.') || name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        offending.ShouldBeEmpty($"{Packages.Core} may reference only the BCL, Microsoft.Extensions.* and Polly (ARCH-01).");
    }

    [Fact]
    [Trait("Id", "ARCH-01")]
    [Trait("Covers", "ARCH-01;ADR-027")]
    public void Core_package_graph_is_exactly_the_allowed_packages()
    {
        // CR-58: the whole package graph (project.assets.json), not just the direct assembly references, so a transitive
        // dependency can't slip in. A new package is a deliberate change to this list.
        string[] allowed =
        [
            "Microsoft.Extensions.Caching.Abstractions", "Microsoft.Extensions.Caching.Memory",
            "Microsoft.Extensions.Compliance.Abstractions", "Microsoft.Extensions.Compliance.Redaction",
            "Microsoft.Extensions.Configuration", "Microsoft.Extensions.Configuration.Abstractions", "Microsoft.Extensions.Configuration.Binder",
            "Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.Diagnostics.Abstractions",
            "Microsoft.Extensions.Diagnostics.HealthChecks", "Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions",
            "Microsoft.Extensions.FileProviders.Abstractions", "Microsoft.Extensions.Hosting.Abstractions",
            "Microsoft.Extensions.Logging.Abstractions", "Microsoft.Extensions.ObjectPool", "Microsoft.Extensions.Options",
            "Microsoft.Extensions.Options.ConfigurationExtensions", "Microsoft.Extensions.Primitives",
            "Polly.Core", "System.IO.Hashing",
            "Microsoft.CodeAnalysis.BannedApiAnalyzers", "Microsoft.NET.ILLink.Tasks",   // build only (PrivateAssets)
        ];

        string[] extra = [.. Packages.Graph("src/Core/SourceFlow.Net").Where(p => !allowed.Contains(p, StringComparer.Ordinal))];

        extra.ShouldBeEmpty($"{Packages.Core} may depend only on the listed packages (ARCH-01); add a new one here deliberately.");
    }

    [Theory]
    [Trait("Id", "ARCH-03")]
    [Trait("Covers", "ARCH-03;ADR-027")]
    [InlineData("src/Storage/SourceFlow.Storage.PostgreSQL", "Npgsql")]
    [InlineData("src/Storage/SourceFlow.Storage.SqlServer", "Microsoft.Data.SqlClient", "Microsoft.SqlServer.Server", "Microsoft.Identity", "Microsoft.Bcl.Cryptography", "System.")]
    [InlineData("src/Storage/SourceFlow.Storage.EntityFrameworkCore", "Microsoft.EntityFrameworkCore", "Microsoft.Extensions.")]
    [InlineData("src/Cloud/SourceFlow.Cloud.Aws", "AWSSDK.")]
    [InlineData("src/Cloud/SourceFlow.Cloud.Azure", "Azure.", "Microsoft.Azure.Amqp", "Microsoft.Identity", "Microsoft.Bcl.AsyncInterfaces", "System.")]
    [InlineData("src/Cloud/SourceFlow.Cloud.Gcp", "Google.", "Grpc.", "Newtonsoft.Json", "Microsoft.Bcl.AsyncInterfaces", "System.")]
    [InlineData("src/Hosting/SourceFlow.AspNetCore")]
    public void Adapters_depend_on_the_core_and_their_own_SDK_only(string project, params string[] sdk)
    {
        // CR-58: what an adapter adds to the core's package graph comes from its own SDK.
        HashSet<string> core = [.. Packages.Graph("src/Core/SourceFlow.Net")];
        string[] foreign = [.. Packages.Graph(project).Where(p => !core.Contains(p) && !sdk.Any(prefix => p == prefix || p.StartsWith(prefix, StringComparison.Ordinal)))];

        foreign.ShouldBeEmpty($"{project} must depend on {Packages.Core} and its own SDK only (ARCH-03).");
    }

    [Fact]
    [Trait("Id", "ARCH-02")]
    [Trait("Covers", "ARCH-02;ADR-027")]
    public void Core_never_references_an_adapter_or_other_SourceFlow_package()
    {
        Assembly core = Packages.Load(Packages.Core);

        Packages.ReferencedSourceFlowAssemblies(core).ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Packages.AdapterData), MemberType = typeof(Packages))]
    [Trait("Id", "ARCH-03")]
    [Trait("Covers", "ARCH-03;ADR-027")]
    public void Adapters_reference_the_core_only_never_each_other(string adapter)
    {
        Assembly assembly = Packages.Load(adapter);

        string[] others = Packages.ReferencedSourceFlowAssemblies(assembly)
            .Where(n => n != Packages.Core)
            .ToArray();

        others.ShouldBeEmpty($"{adapter} must depend on {Packages.Core} and its own SDK only (ARCH-03).");
    }

    public static TheoryData<string> SampleDomains => new("Bank.Contracts", "Bank.Accounts", "Bank.Transfers", "Bank.Notifications");

    [Theory]
    [MemberData(nameof(SampleDomains))]
    [Trait("Id", "ARCH-04")]
    [Trait("Covers", "ARCH-04;ADR-027")]
    public void Domain_code_uses_only_the_contract_namespaces(string domain)
    {
        Assembly assembly = Packages.Load(domain);

        Packages.ReferencedSourceFlowAssemblies(assembly).ShouldAllBe(name => name == Packages.Core);
        NetArchTest.Rules.TestResult result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "SourceFlow.Runtime", "SourceFlow.Storage", "SourceFlow.Transport", "SourceFlow.Security", "SourceFlow.Hosting",
                "SourceFlow.Diagnostics", "SourceFlow.Messaging", "SourceFlow.Query", "SourceFlow.Pipeline")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Theory]
    [MemberData(nameof(Packages.RuntimeData), MemberType = typeof(Packages))]
    [Trait("Id", "ARCH-05")]
    [Trait("Covers", "ARCH-05;ADR-004")]
    public void Runtime_packages_do_not_use_reflection_activation(string package)
    {
        // IL scan: the generic Activator.CreateInstance<T>() that C# emits for 'new T()' is AOT-safe and allowed;
        // activation and generic instantiation from a runtime Type, and CLR-name type lookup, are not (HP-1, GAP-04).
        string[] offending = ReflectionScan.FindBannedCalls(Packages.Load(package).Location).ToArray();

        offending.ShouldBeEmpty($"{package} must not use reflection activation (ARCH-05).");

        NetArchTest.Rules.TestResult emit = Types.InAssembly(Packages.Load(package))
            .ShouldNot()
            .HaveDependencyOn("System.Reflection.Emit")
            .GetResult();
        emit.IsSuccessful.ShouldBeTrue(Describe(emit));
    }

    [Theory]
    [MemberData(nameof(Packages.AdapterData), MemberType = typeof(Packages))]
    [Trait("Id", "ARCH-07")]
    [Trait("Covers", "ARCH-07")]
    public void Public_adapter_classes_are_sealed_unless_abstract_or_static(string adapter)
    {
        Type[] unsealed = Packages.Load(adapter).GetExportedTypes()
            .Where(t => t.IsClass && !t.IsSealed && !t.IsAbstract)
            .ToArray();

        unsealed.ShouldBeEmpty($"Public classes in {adapter} must be sealed unless designed for extension (ARCH-07).");
    }

    [Fact]
    [Trait("Id", "ARCH-09")]
    [Trait("Covers", "ADR-006")]
    public void Only_the_outbox_relay_sends_to_the_transport()
    {
        // ADR-006: every outbound message goes through the outbox. A call to ITransport.SendAsync or PublishAsync anywhere
        // else in the core would skip the outbox's transaction, payload protection, size check and retries (CR-07).
        using Mono.Cecil.AssemblyDefinition core = Mono.Cecil.AssemblyDefinition.ReadAssembly(Packages.Load(Packages.Core).Location);
        string[] callers =
        [
            .. core.MainModule.GetTypes()
                .SelectMany(t => t.Methods.Where(m => m.HasBody).Select(m => (Type: t, Method: m)))
                .Where(c => c.Method.Body.Instructions.Any(i => i.Operand is Mono.Cecil.MethodReference called
                    && called.DeclaringType.FullName == "SourceFlow.Transport.ITransport" && called.Name is "SendAsync" or "PublishAsync"))
                .Select(c => Outermost(c.Type).FullName)
                .Distinct(),
        ];

        callers.ShouldBe(["SourceFlow.Runtime.OutboxRelay"], "only the outbox relay may send to the transport (ARCH-09)");
    }

    [Fact]
    [Trait("Id", "ARCH-10")]
    [Trait("Covers", "ARCH-10;ADR-013")]
    public void No_hand_written_backoff_outside_the_resilience_engine()
    {
        // ADR-013 (CR-39): retries go through the Polly pipelines; a stored retry's delay comes from Backoff. Exponents and
        // jitter anywhere else are a hand-written policy, and a Task.Delay outside the polling loops listed here is a retry
        // loop that bypasses the engine.
        using Mono.Cecil.AssemblyDefinition core = Mono.Cecil.AssemblyDefinition.ReadAssembly(Packages.Load(Packages.Core).Location);
        (string Type, string Called)[] calls =
        [
            .. core.MainModule.GetTypes()
                .SelectMany(t => t.Methods.Where(m => m.HasBody).Select(m => (Type: t, Method: m)))
                .SelectMany(c => c.Method.Body.Instructions
                    .Select(i => i.Operand as Mono.Cecil.MethodReference)
                    .Where(called => called is not null)
                    .Select(called => (Type: Outermost(c.Type).FullName, Called: called!.DeclaringType.FullName + "::" + called.Name)))
                .Distinct(),
        ];

        calls.Where(c => c.Called is "System.Math::Pow" || c.Called.StartsWith("System.Random::", StringComparison.Ordinal))
            .Select(c => c.Type).Distinct().ShouldBe(["SourceFlow.Runtime.Backoff"], "exponents and jitter live in Backoff only (ARCH-10)");

        string[] polling =
        [
            "SourceFlow.Hosting.SourceFlowHost",          // the host's polling, heartbeat and retention loops
            "SourceFlow.Runtime.DeadLetterIngestor",      // the idle wait between polls (failures retry through the ingest pipeline)
            "SourceFlow.Runtime.ReceiveEndpoint",         // the idle wait of an empty receive
            "SourceFlow.Runtime.SubscriptionEngine",      // the wait for new events
            "SourceFlow.Runtime.SourceFlowMonitor",       // the sampling interval
            "SourceFlow.Query.QueryService",              // the consistency wait for a read model to reach a token
            "SourceFlow.Storage.InMemory.InMemoryFaults", // the in-memory store's injected latency (tests)
        ];
        calls.Where(c => c.Called == "System.Threading.Tasks.Task::Delay").Select(c => c.Type).Distinct()
            .Where(t => !polling.Contains(t)).ShouldBeEmpty("Task.Delay outside the polling loops is a hand-written retry (ARCH-10)");
    }

    private static Mono.Cecil.TypeDefinition Outermost(Mono.Cecil.TypeDefinition type)
    {
        while (type.DeclaringType is { } outer)
        {
            type = outer;
        }

        return type;
    }

    private static string Describe(NetArchTest.Rules.TestResult result)
    {
        return result.IsSuccessful
            ? string.Empty
            : "Offending types: " + string.Join(", ", result.FailingTypeNames ?? []);
    }
}

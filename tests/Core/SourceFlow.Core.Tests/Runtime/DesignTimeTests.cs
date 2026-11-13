using System.Text.Json.Nodes;
using Bank.Accounts;
using Bank.Contracts;
using Bank.Transfers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SourceFlow.Hosting;
using SourceFlow.Runtime;
using SourceFlow.Transport;

namespace SourceFlow.Core.Tests.Runtime;

/// <summary>Design-time mode and the service manifest export (doc 15 §3.2, WP-S.2).</summary>
[Trait("Category", "Unit")]
public sealed class DesignTimeTests
{
    [Fact]
    [Trait("Id", "UT-MAN-09")]
    [Trait("Covers", "WFD-11")]
    public void Export_MergesTheStaticManifestsWithRoutesPublicationsAndSubscriptions()
    {
        using ServiceProvider services = Transfers(reverse: false).BuildServiceProvider();

        JsonObject manifest = JsonNode.Parse(SourceFlowManifestExporter.Export(services))!.AsObject();

        manifest["manifestVersion"]!.GetValue<int>().ShouldBe(1);
        manifest["service"]!.GetValue<string>().ShouldBe("transfers");
        manifest["endpoint"]!.GetValue<string>().ShouldBe("transfers");
        manifest["schemas"]!.GetValue<string>().ShouldBe("catalog://transfers");
        Names(manifest["aggregates"]!).ShouldBe(["bank-transfer"], "only what this service registers; bank-account belongs to the Accounts service");
        Names(manifest["processManagers"]!).ShouldBe(["money-transfer"]);
        JsonObject request = manifest["aggregates"]![0]!["commands"]!.AsArray().Single(c => c!["type"]!.GetValue<string>() == "bank.transfer.request")!.AsObject();
        request["rejects"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe(["transfer.amount_must_be_positive", "transfer.same_account"]);

        manifest["commandRoutes"]!.ToJsonString().ShouldBe(
            """[{"command":"bank.account.deposit.v1","to":"accounts","replyOnReject":true},{"command":"bank.account.withdraw.v1","to":"accounts","replyOnReject":false}]""");
        manifest["publishes"]!.ToJsonString().ShouldBe(
            """[{"domainEvent":"bank.transfer.completed.v1","as":"bank.transfer.completed.v1","topic":"transfers.events"}]""");
        manifest["subscribes"]!.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).ShouldBe(
            """[{"topic":"accounts.events","only":["accounts.account-opened.v1"],"where":"source = 'accounts'"},{"topic":"audit.events"}]""");

        string[] messages = [.. manifest["messages"]!.AsArray().Select(m => m!["type"]!.GetValue<string>() + ".v" + m["version"]!.GetValue<int>())];
        messages.ShouldContain("bank.account.deposit.v1", "remote commands the service registers are described with their fields");
        messages.ShouldContain("accounts.account-opened.v1");
        messages.ShouldBe([.. messages.Order(StringComparer.Ordinal)]);
        manifest["messages"]!.AsArray().Single(m => m!["type"]!.GetValue<string>() == "bank.account.deposit")!["fields"]!.ToJsonString()
            .ShouldBe("""[{"name":"accountId","type":"uuid"},{"name":"amount","type":"number"}]""");
    }

    [Fact]
    [Trait("Id", "UT-MAN-10")]
    [Trait("Covers", "WFD-11;WFD-NFR-1")]
    public void DesignTimeMode_ExportsWhenTheHostIsBuilt_WithoutTouchingStorageOrTransport()
    {
        string path = Path.Combine(Path.GetTempPath(), "sourceflow-design-" + Path.GetRandomFileName(), "manifest.json");
        int? exit = null;
        var faults = new List<string>();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSourceFlow(
            sf =>
            {
                Configure(sf, reverse: false);
                // Fault-injecting adapters: any use is a design-time I/O leak.
                sf.UseStorage(_ =>
                {
                    faults.Add("storage");
                    throw new InvalidOperationException("storage used at design time");
                });
                sf.UseTransport(_ =>
                {
                    faults.Add("transport");
                    throw new InvalidOperationException("transport used at design time");
                });
            },
            new DesignTimeExport(path, code => exit = code, TextWriter.Null, TextWriter.Null));
        builder.Services.AddHostedService<Tripwire>();

        try
        {
            using IHost host = builder.Build();

            exit.ShouldBe(0);
            faults.ShouldBeEmpty();
            Tripwire.Started.ShouldBeFalse();
            builder.Services.Count(d => d.ServiceType == typeof(IHostedService)).ShouldBe(1, "only the application's own; SourceFlow's host is not registered at design time");
            JsonNode.Parse(File.ReadAllText(path))!["service"]!.GetValue<string>().ShouldBe("transfers");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    [Trait("Id", "UT-MAN-10")]
    public void DesignTimeHook_LeavesOrdinaryHostsAlone_AndReportsFailures()
    {
        HostApplicationBuilder ordinary = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        ordinary.Services.AddSourceFlow(sf => Configure(sf, reverse: false), designTime: null);
        using (IHost host = ordinary.Build())
        {
            host.Services.GetService<DesignTimeExport>().ShouldBeNull();
        }

        // An unwritable target: the export fails with exit code 1 and a message, instead of throwing out of Build().
        string blocked = Path.Combine(Path.GetTempPath(), "sourceflow-design-" + Path.GetRandomFileName());
        File.WriteAllText(blocked, "a file, not a folder");
        int? exit = null;
        var error = new StringWriter();
        HostApplicationBuilder failing = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        failing.Services.AddSourceFlow(sf => Configure(sf, reverse: false), new DesignTimeExport(Path.Combine(blocked, "manifest.json"), code => exit = code, TextWriter.Null, error));
        try
        {
            using IHost host = failing.Build();
            exit.ShouldBe(1);
            error.ToString().ShouldContain("SourceFlow design-time export failed");
        }
        finally
        {
            File.Delete(blocked);
        }

        var output = new StringWriter();
        HostApplicationBuilder toStdout = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        toStdout.Services.AddSourceFlow(sf => Configure(sf, reverse: false), new DesignTimeExport(null, _ => { }, output, TextWriter.Null));
        using (IHost host = toStdout.Build())
        {
            output.ToString().ShouldStartWith("{\n  \"manifestVersion\": 1,");
        }
    }

    [Fact]
    [Trait("Id", "UT-MAN-11")]
    [Trait("Covers", "WFD-NFR-3")]
    public void Export_IsByteIdentical_RegardlessOfDeclarationOrder()
    {
        using ServiceProvider forward = Transfers(reverse: false).BuildServiceProvider();
        using ServiceProvider reversed = Transfers(reverse: true).BuildServiceProvider();

        string first = SourceFlowManifestExporter.Export(forward);
        string second = SourceFlowManifestExporter.Export(reversed);

        second.ShouldBe(first);
        SourceFlowManifestExporter.Export(forward).ShouldBe(first);
        first.ShouldNotContain("\r");
        first.ShouldEndWith("}\n");
    }

    private static ServiceCollection Transfers(bool reverse)
    {
        var services = new ServiceCollection();
        services.AddSourceFlow(sf => Configure(sf, reverse), designTime: null);
        return services;
    }

    private static void Configure(SourceFlowBuilder sf, bool reverse)
    {
        sf.ServiceName = "transfers";
        if (reverse)
        {
            sf.AddModule<RemoteAccountCommands>();
            sf.AddModule<ContractsModule>();
            sf.AddModule<TransfersModule>();
        }
        else
        {
            sf.AddModule<TransfersModule>();
            sf.AddModule<ContractsModule>();
            sf.AddModule<RemoteAccountCommands>();
        }

        sf.UseInMemoryStorage();
        sf.Routing(r =>
        {
#pragma warning disable SF0014 // the Accounts service owns these commands
            if (reverse)
            {
                r.Subscribe("audit.events");
                r.Subscribe("accounts.events", s => s.Only<AccountOpenedV1>().Where("source = 'accounts'"));
                r.Command<Withdraw>().To("accounts");
                r.Command<Deposit>().To("accounts").ReplyOnReject();
            }
            else
            {
                r.Command<Deposit>().To("accounts").ReplyOnReject();
                r.Command<Withdraw>().To("accounts");
                r.Subscribe("accounts.events", s => s.Only<AccountOpenedV1>().Where("source = 'accounts'"));
                r.Subscribe("audit.events");
            }
#pragma warning restore SF0014
            r.Publish<TransferCompleted>().AsIs().To("transfers.events");
        });
    }

    private static string[] Names(JsonNode array)
    {
        return [.. array.AsArray().Select(n => n!["name"]!.GetValue<string>())];
    }

    private sealed class RemoteAccountCommands : ISourceFlowModule
    {
        public static string ModuleName => nameof(RemoteAccountCommands);

        public static void Register(ISourceFlowModuleBuilder builder)
        {
            builder.AddMessage<Withdraw>("bank.account.withdraw", 1);
            builder.AddMessage<Deposit>("bank.account.deposit", 1);
            builder.AddJsonTypeInfoResolver(new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver());
        }
    }

    private sealed class Tripwire : IHostedService
    {
        public static bool Started { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}

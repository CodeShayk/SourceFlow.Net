using System.Globalization;
using System.Text;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Google.Cloud.PubSub.V1;
using Grpc.Core;
using Grpc.Net.Client;
using Shouldly;
using SourceFlow.Cloud.Aws;
using SourceFlow.Cloud.Gcp;
using SourceFlow.Cloud.Tests.Aws;
using SourceFlow.Cloud.Tests.Gcp;
using SourceFlow.Testing.Conformance;
using SourceFlow.Transport;

namespace SourceFlow.Cloud.Tests.Topology;

/// <summary>
/// The reference infrastructure-as-code modules (<c>deploy/topology</c>, WP-3.5) against the TCK topology fixtures. The
/// Terraform modules are applied to LocalStack and the Pub/Sub emulator, and what they create must pass the transports'
/// <see cref="ProvisioningMode.Validate"/>: the same check a production service runs at startup with describe-only
/// permissions. Each module's own <c>terraform test</c> suite (mock providers, every fixture) runs too; the Azure modules
/// have no emulator for ARM, so that suite is their check.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Requires", "Terraform")]
public sealed class TopologyIacTests(LocalStackServer localStack, PubSubEmulatorServer pubSub)
{
    // Publisher before subscriber: the subscriber only subscribes to the publisher's topic.
    private static readonly string[] Order = ["minimal", "full", "ordered", "filtered", "publisher", "subscriber"];

    [Fact]
    [Trait("Id", "IAC-01")]
    [Trait("Cloud", "Aws")]
    [Trait("Requires", "LocalStack")]
    [Trait("Covers", "ADR-017;doc 07 5")]
    public async Task TerraformAwsModule_OnLocalStack_PassesValidate()
    {
        TerraformCli terraform = Terraform();
        CancellationToken ct = TestContext.Current.CancellationToken;
        await localStack.EnsureStartedAsync();
        string prefix = RunPrefix();
        var options = new AwsTransportOptions();
        using var workspace = new Workspace("aws");
        foreach (string fixture in Order)
        {
            workspace.WriteExport(fixture, AwsTopology.Describe(options, TopologyFixtures.Get(fixture, prefix)), TopologyFixtures.Get(fixture, prefix));
        }

        string endpoint = terraform.HostUrl(localStack.Endpoint.ToString().TrimEnd('/'));
        workspace.WriteRoot($$"""
            provider "aws" {
              region                      = "{{LocalStackServer.Region}}"
              access_key                  = "test"
              secret_key                  = "test"
              skip_credentials_validation = true
              skip_metadata_api_check     = true
              skip_requesting_account_id  = true
              endpoints {
                sqs = "{{endpoint}}"
                sns = "{{endpoint}}"
              }
            }
            """, fixture => fixture == "subscriber" ? "create_subscribed_topics = false" : "create_subscribed_topics = true");

        await terraform.RunOrFailAsync(workspace.Directory, ct, "init", "-input=false", "-no-color");
        await terraform.RunOrFailAsync(workspace.Directory, ct, "apply", "-auto-approve", "-input=false", "-no-color", "-parallelism=20");

        var credentials = new BasicAWSCredentials("test", "test");
        using var sqs = new AmazonSQSClient(credentials, new AmazonSQSConfig { ServiceURL = localStack.Endpoint.ToString(), AuthenticationRegion = LocalStackServer.Region });
        using var sns = new AmazonSimpleNotificationServiceClient(credentials, new AmazonSimpleNotificationServiceConfig { ServiceURL = localStack.Endpoint.ToString(), AuthenticationRegion = LocalStackServer.Region });
        using var transport = new AwsTransport(sqs, sns, options);
        await ValidateAllAsync(transport, prefix, ct);
    }

    [Fact]
    [Trait("Id", "IAC-02")]
    [Trait("Cloud", "Gcp")]
    [Trait("Requires", "PubSubEmulator")]
    [Trait("Covers", "ADR-017;doc 07 5")]
    public async Task TerraformGcpModule_OnTheEmulator_PassesValidate()
    {
        TerraformCli terraform = Terraform();
        CancellationToken ct = TestContext.Current.CancellationToken;
        await pubSub.EnsureStartedAsync();
        string prefix = RunPrefix();
        var options = new GcpTransportOptions { ProjectId = PubSubEmulatorServer.ProjectId };
        using var workspace = new Workspace("gcp");
        foreach (string fixture in Order)
        {
            workspace.WriteExport(fixture, GcpTopology.Describe(options, TopologyFixtures.Get(fixture, prefix)), TopologyFixtures.Get(fixture, prefix));
        }

        string endpoint = terraform.HostUrl("http://" + pubSub.Host);
        workspace.WriteRoot($$"""
            provider "google" {
              project                = "{{PubSubEmulatorServer.ProjectId}}"
              access_token           = "emulator"
              pubsub_custom_endpoint = "{{endpoint}}/v1/"
            }
            """, fixture => $"""
            project                  = "{PubSubEmulatorServer.ProjectId}"
              grant_service_agent      = false
              create_subscribed_topics = {(fixture == "subscriber" ? "false" : "true")}
            """);

        await terraform.RunOrFailAsync(workspace.Directory, ct, "init", "-input=false", "-no-color");
        await terraform.RunOrFailAsync(workspace.Directory, ct, "apply", "-auto-approve", "-input=false", "-no-color", "-parallelism=20");

        using GrpcChannel channel = GrpcChannel.ForAddress("http://" + pubSub.Host, new GrpcChannelOptions { Credentials = ChannelCredentials.Insecure });
        CallInvoker invoker = channel.CreateCallInvoker();
        var transport = new GcpTransport(new PublisherServiceApiClientBuilder { CallInvoker = invoker }.Build(), new SubscriberServiceApiClientBuilder { CallInvoker = invoker }.Build(), options);
        await ValidateAllAsync(transport, prefix, ct);
    }

    [Theory]
    [InlineData("aws")]
    [InlineData("gcp")]
    [InlineData("azure")]
    [Trait("Id", "IAC-03")]
    [Trait("Covers", "ADR-017;doc 07 5")]
    public async Task TerraformModuleTests_PassForEveryFixture(string module)
    {
        TerraformCli terraform = Terraform();
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var workspace = new Workspace(module);

        // The module's tests read the committed fixtures (../../fixtures), as in the repository.
        string directory = Path.Combine(workspace.Directory, "modules", module);
        await terraform.RunOrFailAsync(terraform.InDocker ? workspace.Directory : directory, ct, Relative(terraform, module, "init", "-backend=false", "-input=false", "-no-color"));
        string output = await terraform.RunOrFailAsync(terraform.InDocker ? workspace.Directory : directory, ct, Relative(terraform, module, "test", "-no-color"));

        output.ShouldContain("0 failed");
        output.ShouldNotContain("skip");
    }

    [Fact]
    [Trait("Id", "IAC-04")]
    [Trait("Cloud", "Azure")]
    [Trait("Covers", "ADR-017;doc 07 5")]
    public async Task BicepModule_Builds_ForEveryAzureFixture()
    {
        (string? file, string[] prefix) = Bicep();
        Assert.SkipWhen(file is null, "Neither bicep nor az is on the path (CI runners have both).");
        CancellationToken ct = TestContext.Current.CancellationToken;
        string directory = Path.Combine(Path.GetTempPath(), "sourceflow-bicep-" + Guid.CreateVersion7().ToString("N")[^12..]);
        Directory.CreateDirectory(directory);
        try
        {
            File.Copy(Path.Combine(TopologyExports.ModulesDirectory, "bicep", "servicebus.bicep"), Path.Combine(directory, "servicebus.bicep"));
            var modules = new StringBuilder();
            foreach (string fixture in Order)
            {
                File.Copy(TopologyExports.FixturePath("azure", fixture), Path.Combine(directory, fixture + ".json"));
                modules.AppendLine(CultureInfo.InvariantCulture, $$"""
                    module {{fixture}} 'servicebus.bicep' = {
                      name: '{{fixture}}-topology'
                      params: {
                        namespaceName: 'sourceflow'
                        topology: loadJsonContent('{{fixture}}.json')
                        createSubscribedTopics: {{(fixture == "subscriber" ? "false" : "true")}}
                      }
                    }
                    """);
            }

            File.WriteAllText(Path.Combine(directory, "main.bicep"), modules.ToString(), new UTF8Encoding(false));
            string main = Path.Combine(directory, "main.bicep");
            (int code, string output) = await RunAsync(file!, prefix.Length == 0 ? ["build", main, "--stdout"] : [.. prefix, "build", "--file", main, "--stdout"], ct);

            code.ShouldBe(0, output);
            output.ShouldContain("Microsoft.ServiceBus/namespaces/queues");
            output.ShouldContain("Microsoft.ServiceBus/namespaces/topics/subscriptions/rules");
            output.ShouldNotContain("Error BCP", Case.Insensitive);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static (string? File, string[] Prefix) Bicep()
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach ((string name, string[] prefix) in ((string, string[])[])[("bicep", []), ("bicep.exe", []), ("az", ["bicep"]), ("az.cmd", ["bicep"])])
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return (candidate, prefix);
                }
            }
        }

        return (null, []);
    }

    private static async Task<(int Code, string Output)> RunAsync(string file, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var start = new System.Diagnostics.ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await stdout + await stderr);
    }

    private static async Task ValidateAllAsync(ITransport transport, string prefix, CancellationToken ct)
    {
        var failures = new StringBuilder();
        foreach (string fixture in Order)
        {
            try
            {
                await transport.ProvisionAsync(TopologyFixtures.Get(fixture, prefix), ProvisioningMode.Validate, ct);
            }
            catch (TopologyValidationException e)
            {
                failures.AppendLine(CultureInfo.InvariantCulture, $"{fixture}: {e.Message}");
            }
        }

        failures.ToString().ShouldBeEmpty("what the Terraform module created must pass Validate");
    }

    // In Docker the working directory is mounted at /work; -chdir selects the module inside it.
    private static string[] Relative(TerraformCli terraform, string module, params string[] arguments)
    {
        return terraform.InDocker ? [$"-chdir=modules/{module}", .. arguments] : arguments;
    }

    private static TerraformCli Terraform()
    {
        (TerraformCli? cli, string reason) = TerraformCli.Find();
        Assert.SkipWhen(cli is null, reason);
        return cli!;
    }

    private static string RunPrefix()
    {
        return "iac" + Guid.CreateVersion7().ToString("N")[^8..] + ".";
    }

    /// <summary>
    /// A Terraform root in a temporary directory: <c>modules/</c> (a copy of <c>deploy/topology/terraform</c>),
    /// <c>fixtures/</c> (the committed fixtures, for the modules' own tests), <c>exports/</c> (this run's exports) and
    /// <c>main.tf</c> with one module instance per fixture.
    /// </summary>
    private sealed class Workspace : IDisposable
    {
        private readonly string _module;

        public Workspace(string module)
        {
            _module = module;
            Directory = Path.Combine(Path.GetTempPath(), "sourceflow-iac-" + Guid.CreateVersion7().ToString("N")[^12..]);
            Copy(Path.Combine(TopologyExports.ModulesDirectory, "terraform"), Path.Combine(Directory, "modules"));
            Copy(TopologyExports.FixturesDirectory, Path.Combine(Directory, "fixtures"));
            System.IO.Directory.CreateDirectory(Path.Combine(Directory, "exports"));
        }

        public string Directory { get; }

        public void WriteExport(string fixture, PhysicalTopology physical, TransportTopology topology)
        {
            File.WriteAllText(Path.Combine(Directory, "exports", fixture + ".json"),
                TopologyExport.ToJson(new TopologyDocument(fixture, topology) { Physical = physical }), new UTF8Encoding(false));
        }

        public void WriteRoot(string provider, Func<string, string> arguments)
        {
            var root = new StringBuilder(provider).AppendLine().AppendLine();
            foreach (string fixture in Order)
            {
                root.AppendLine(CultureInfo.InvariantCulture, $$"""
                    module "{{fixture}}" {
                      source   = "./modules/{{_module}}"
                      topology = file("exports/{{fixture}}.json")
                      {{arguments(fixture)}}
                    {{(fixture == "subscriber" ? "  depends_on = [module.publisher]" : string.Empty)}}
                    }
                    """);
            }

            File.WriteAllText(Path.Combine(Directory, "main.tf"), root.ToString(), new UTF8Encoding(false));
        }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
                // Files written by a container may be locked or owned by root; the temp directory is cleaned up later.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void Copy(string source, string target)
        {
            foreach (string file in System.IO.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, file);
                if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p.StartsWith(".terraform", StringComparison.Ordinal)))
                {
                    continue;
                }

                string destination = Path.Combine(target, relative);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
        }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using SourceFlow.Tool;
using SourceFlow.Tool.Studio;

namespace SourceFlow.Tooling.Tests;

/// <summary>
/// <c>dotnet sourceflow studio</c>, the Studio local host (WP-S.8, doc 15 §6.3, §6.4): loopback only, a Host-header
/// guard, writes limited to the workspace's workflow and layout files, a read-only mode, and the manifest export trigger.
/// </summary>
[Trait("Category", "Unit")]
public sealed class StudioHostTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sourceflow-studio-" + Guid.CreateVersion7().ToString("N")[^12..]);
    private readonly List<string> _exported = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        string golden = Path.Combine(Repository(), "tests", "workflow-golden");
        Directory.CreateDirectory(Path.Combine(_root, "manifests"));
        foreach (string manifest in Directory.GetFiles(Path.Combine(golden, "domain"), "*.json"))
        {
            File.Copy(manifest, Path.Combine(_root, "manifests", Path.GetFileName(manifest)));
        }

        Directory.CreateDirectory(Path.Combine(_root, "orders", "workflows"));
        Directory.CreateDirectory(Path.Combine(_root, "orders", "src"));
        Directory.CreateDirectory(Path.Combine(_root, "orders", "tests"));
        File.WriteAllText(Path.Combine(_root, "orders", "tests", "Orders.Tests.csproj"), "<Project />");
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.Copy(Path.Combine(golden, "cases", "01-order-fulfilment", "workflow.sfflow.yaml"), Path.Combine(_root, "orders", "workflows", "order-fulfilment.sfflow.yaml"));
        File.WriteAllText(Path.Combine(_root, "orders", "src", "Orders.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "docs", "example.sfflow.yaml"), "name: example\n");
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "secret");
        File.WriteAllText(Path.Combine(_root, "sourceflow.domain.yaml"), """
            domain: commerce
            description: Order to delivery
            services:
              - name: orders
                manifest: manifests/orders.manifest.json
                workflows: orders/workflows
                project: orders/src
                tests: orders/tests
                module: Orders.OrdersModule
              - name: inventory
                manifest: manifests/inventory.manifest.json
              - name: payments
                manifest: manifests/payments.manifest.json
              - name: shipping
                manifest: manifests/shipping.manifest.json
            """);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    [Trait("Id", "UT-STU-HOST-01")]
    [Trait("Covers", "WFD-NFR-5")]
    public async Task BindsToLoopbackOnly()
    {
        await using StudioHost host = await Start();

        host.Url.Host.ShouldBe("127.0.0.1");
        host.Url.Port.ShouldBeGreaterThan(0);
        using HttpClient client = Client(host);
        (await client.GetAsync("api/workspace", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("api/nothing", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [Trait("Id", "UT-STU-HOST-02")]
    [Trait("Covers", "WFD-NFR-5")]
    [InlineData("evil.example", HttpStatusCode.Forbidden)]
    [InlineData("192.168.1.10", HttpStatusCode.Forbidden)]
    [InlineData("localhost.evil.example", HttpStatusCode.Forbidden)]
    [InlineData("localhost", HttpStatusCode.OK)]
    [InlineData("127.0.0.1", HttpStatusCode.OK)]
    public async Task NonLoopbackHostHeaders_AreRefused(string name, HttpStatusCode expected)
    {
        await using StudioHost host = await Start();
        using HttpClient client = Client(host);
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/workspace");
        request.Headers.Host = name + ":" + host.Url.Port;

        HttpResponseMessage response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(expected, "DNS rebinding: a page on another name must not reach the workspace");
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("default-src 'self'");
        response.Headers.GetValues("X-Frame-Options").Single().ShouldBe("DENY");
    }

    [Fact]
    [Trait("Id", "UT-STU-HOST-03")]
    [Trait("Covers", "WFD-NFR-5")]
    public async Task Writes_ComeFromTheStudioPageOnly_AsJson()
    {
        await using StudioHost host = await Start();
        using HttpClient client = Client(host);

        using var foreign = Put("orders/workflows/x.sfflow.yaml", "a: 1");
        foreign.Headers.Add("Origin", "https://evil.example");
        (await client.SendAsync(foreign, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var plain = new HttpRequestMessage(HttpMethod.Put, "api/files?path=orders/workflows/x.sfflow.yaml") { Content = new StringContent("{\"content\":\"a\"}", Encoding.UTF8, "text/plain") };
        (await client.SendAsync(plain, Ct)).StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType, "a simple cross-site form post can't send JSON");

        using var own = Put("orders/workflows/x.sfflow.yaml", "a: 1");
        own.Headers.Add("Origin", host.Url.GetLeftPart(UriPartial.Authority));
        (await client.SendAsync(own, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Id", "UT-STU-HOST-04")]
    [Trait("Covers", "WFD-10")]
    public async Task Workspace_ListsServicesManifestsAndWorkflows()
    {
        await using StudioHost host = await Start();
        using HttpClient client = Client(host);

        JsonNode workspace = (await client.GetFromJsonAsync<JsonNode>("api/workspace", Ct))!;

        workspace["domain"]!.GetValue<string>().ShouldBe("commerce");
        workspace["readOnly"]!.GetValue<bool>().ShouldBeFalse();
        JsonArray services = workspace["services"]!.AsArray();
        services.Select(s => s!["name"]!.GetValue<string>()).ShouldBe(["orders", "inventory", "payments", "shipping"]);
        JsonNode orders = services[0]!;
        orders["workflowFiles"]!.AsArray().Select(f => f!.GetValue<string>()).ShouldBe(["orders/workflows/order-fulfilment.sfflow.yaml"]);
        orders["project"]!.GetValue<string>().ShouldBe("orders/src");
        orders["tests"]!.GetValue<string>().ShouldBe("orders/tests");
        orders["module"]!.GetValue<string>().ShouldBe("Orders.OrdersModule");
        services[1]!["tests"].ShouldBeNull();
        JsonNode.Parse(orders["manifestJson"]!.GetValue<string>())!["service"]!.GetValue<string>().ShouldBe("orders");
        services[1]!["workflows"].ShouldBeNull();
    }

    [Theory]
    [Trait("Id", "UT-STU-HOST-05")]
    [Trait("Covers", "WFD-NFR-5")]
    [InlineData("orders/workflows/order-fulfilment.sfflow.yaml", HttpStatusCode.OK)]
    [InlineData("docs/example.sfflow.yaml", HttpStatusCode.OK)]
    [InlineData("orders/workflows/missing.sfflow.yaml", HttpStatusCode.NotFound)]
    [InlineData("secret.txt", HttpStatusCode.Forbidden)]
    [InlineData("sourceflow.domain.yaml", HttpStatusCode.Forbidden)]
    [InlineData("../outside.sfflow.yaml", HttpStatusCode.Forbidden)]
    [InlineData("orders/../../outside.sfflow.yaml", HttpStatusCode.Forbidden)]
    [InlineData("C:/Windows/win.sfflow.yaml", HttpStatusCode.Forbidden)]
    [InlineData("/etc/passwd.sfflow.yaml", HttpStatusCode.Forbidden)]
    public async Task Reads_AreLimitedToWorkflowAndLayoutFilesInTheWorkspace(string path, HttpStatusCode expected)
    {
        await using StudioHost host = await Start();
        using HttpClient client = Client(host);

        HttpResponseMessage response = await client.GetAsync("api/files?path=" + Uri.EscapeDataString(path), Ct);

        response.StatusCode.ShouldBe(expected);
        if (expected == HttpStatusCode.OK)
        {
            (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("name:");
        }
    }

    [Fact]
    [Trait("Id", "UT-STU-HOST-06")]
    [Trait("Covers", "WFD-NFR-5")]
    public async Task Writes_AreLimitedToTheServicesWorkflowFolders()
    {
        await using StudioHost host = await Start();
        using HttpClient client = Client(host);
        const string Text = "# edited in Studio\nname: order-fulfilment\n";

        HttpResponseMessage written = await client.SendAsync(Put("orders/workflows/order-fulfilment.sfflow.yaml", Text), Ct);
        written.StatusCode.ShouldBe(HttpStatusCode.OK);
        File.ReadAllText(Path.Combine(_root, "orders", "workflows", "order-fulfilment.sfflow.yaml")).ShouldBe(Text, "byte for byte");
        Directory.GetFiles(Path.Combine(_root, "orders", "workflows"), "*.tmp-*").ShouldBeEmpty("written through a temporary file and a move");

        (await client.SendAsync(Put("orders/workflows/nested/order-fulfilment.sfflow.layout.json", "{}"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.SendAsync(Put("docs/example.sfflow.yaml", Text), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "not a service's workflows folder");
        (await client.SendAsync(Put("orders/workflows/notes.txt", Text), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.SendAsync(Put("orders/workflows/../../x.sfflow.yaml", Text), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var empty = new HttpRequestMessage(HttpMethod.Put, "api/files?path=orders/workflows/y.sfflow.yaml") { Content = JsonContent.Create(new JsonObject()) };
        (await client.SendAsync(empty, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        File.Exists(Path.Combine(_root, "x.sfflow.yaml")).ShouldBeFalse();
    }

    [Fact]
    [Trait("Id", "UT-STU-HOST-07")]
    [Trait("Covers", "WFD-NFR-5")]
    public async Task ReadOnlyMode_RefusesEveryWrite()
    {
        await using StudioHost host = await Start(readOnly: true);
        using HttpClient client = Client(host);

        (await client.GetFromJsonAsync<JsonNode>("api/workspace", Ct))!["readOnly"]!.GetValue<bool>().ShouldBeTrue();
        (await client.GetAsync("api/files?path=orders/workflows/order-fulfilment.sfflow.yaml", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        HttpResponseMessage write = await client.SendAsync(Put("orders/workflows/order-fulfilment.sfflow.yaml", "x"), Ct);
        write.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await write.Content.ReadAsStringAsync(Ct)).ShouldContain("--read-only");
        (await client.PostAsync("api/services/orders/manifest/export", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _exported.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Id", "UT-STU-HOST-08")]
    [Trait("Covers", "WFD-12")]
    public async Task ManifestExport_RunsForTheServicesProject_AndRewritesItsManifest()
    {
        await using StudioHost host = await Start();
        using HttpClient client = Client(host);

        HttpResponseMessage exported = await client.PostAsync("api/services/orders/manifest/export", null, Ct);

        exported.StatusCode.ShouldBe(HttpStatusCode.OK);
        _exported.ShouldBe([Path.Combine(_root, "orders", "src")]);
        File.ReadAllText(Path.Combine(_root, "manifests", "orders.manifest.json")).ShouldContain("\"exported\": true");
        (await client.PostAsync("api/services/inventory/manifest/export", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "no project in the workspace");
        (await client.PostAsync("api/services/nobody/manifest/export", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Id", "UT-STU-HOST-09")]
    [Trait("Covers", "WFD-NFR-5")]
    public async Task ServesTheApp_WithClientRoutes_AndTheCompressedCore()
    {
        string app = Path.Combine(_root, "app");
        string framework = Path.Combine(_root, "framework");
        Directory.CreateDirectory(Path.Combine(app, "assets"));
        Directory.CreateDirectory(framework);
        File.WriteAllText(Path.Combine(app, "index.html"), "<!doctype html><title>Studio</title>");
        File.WriteAllText(Path.Combine(app, "assets", "app.js"), "console.log(1)");
        File.WriteAllText(Path.Combine(framework, "dotnet.js"), "plain");
        File.WriteAllBytes(Path.Combine(framework, "dotnet.js.br"), [1, 2, 3]);
        await using StudioHost host = await Start(assets: app, framework: framework);
        using HttpClient client = Client(host);

        (await client.GetStringAsync("", Ct)).ShouldContain("<title>Studio</title>");
        (await client.GetStringAsync("workflows/orders/order-fulfilment", Ct)).ShouldContain("<title>Studio</title>", Case.Sensitive, "client-side routes get the SPA");
        using var navigation = new HttpRequestMessage(HttpMethod.Get, "workflows/orders/workflows/order-fulfilment.sfflow.yaml");
        navigation.Headers.Accept.ParseAdd("text/html");
        (await (await client.SendAsync(navigation, Ct)).Content.ReadAsStringAsync(Ct)).ShouldContain("<title>Studio</title>", Case.Sensitive, "a workflow route ends in .sfflow.yaml");
        (await client.GetAsync("workflows/orders/workflows/order-fulfilment.sfflow.yaml", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound, "not a page navigation");
        HttpResponseMessage script = await client.GetAsync("assets/app.js", Ct);
        script.Content.Headers.ContentType!.MediaType.ShouldBe("text/javascript");
        (await client.GetAsync("assets/missing.js", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var compressed = new HttpRequestMessage(HttpMethod.Get, "_framework/dotnet.js");
        compressed.Headers.AcceptEncoding.ParseAdd("br");
        HttpResponseMessage brotli = await client.SendAsync(compressed, Ct);
        brotli.Content.Headers.ContentEncoding.ShouldBe(["br"]);
        (await brotli.Content.ReadAsByteArrayAsync(Ct)).ShouldBe([1, 2, 3]);
        (await client.GetStringAsync("_framework/dotnet.js", Ct)).ShouldBe("plain");
        (await client.PostAsync("index.html", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    [Trait("Id", "UT-STU-HOST-10")]
    [Trait("Covers", "WFD-10")]
    public async Task TheCli_StartsAndStopsTheHost_AndReportsABadWorkspace()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        (await SourceFlowCli.RunAsync(["studio", "--workspace", Path.Combine(_root, "missing")], null, output, error, Ct)).ShouldBe(1);
        error.ToString().ShouldContain("not found");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var started = new StringWriter();
        Task<int> run = SourceFlowCli.RunAsync(["studio", "--workspace", _root, "--port", "0", "--read-only"], null, started, error, stop.Token);
        for (int i = 0; i < 200 && !started.ToString().Contains("SourceFlow Studio:", StringComparison.Ordinal); i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, Ct);
        }

        started.ToString().ShouldMatch(@"SourceFlow Studio: http://127\.0\.0\.1:\d+/ \(read-only\)");
        await stop.CancelAsync();
        (await run).ShouldBe(0);
    }

    [Fact]
    [Trait("Id", "UT-STU-HOST-11")]
    [Trait("Covers", "WFD-34")]
    public async Task Specs_AreGeneratedIntoTheOwnersTestProject()
    {
        await using StudioHost host = await Start();
        using HttpClient client = Client(host);
        string file = Path.Combine(_root, "orders", "tests", "Workflows", "OrderFulfilmentV1PathTests.cs");

        // A dry run is the preview: the same text, nothing written.
        JsonNode preview = (await (await client.PostAsJsonAsync("api/services/orders/specs", new { workflow = "orders/workflows/order-fulfilment.sfflow.yaml", dryRun = true }, Ct)).Content.ReadFromJsonAsync<JsonNode>(Ct))!;
        preview["written"]!.GetValue<bool>().ShouldBeFalse();
        preview["path"]!.GetValue<string>().ShouldBe("orders/tests/Workflows/OrderFulfilmentV1PathTests.cs");
        File.Exists(file).ShouldBeFalse();

        HttpResponseMessage cover = await client.PostAsJsonAsync("api/services/orders/specs", new { workflow = "orders/workflows/order-fulfilment.sfflow.yaml" }, Ct);

        cover.StatusCode.ShouldBe(HttpStatusCode.OK, await cover.Content.ReadAsStringAsync(Ct));
        JsonNode written = (await cover.Content.ReadFromJsonAsync<JsonNode>(Ct))!;
        written["path"]!.GetValue<string>().ShouldBe("orders/tests/Workflows/OrderFulfilmentV1PathTests.cs");
        written["tests"]!.GetValue<int>().ShouldBe(6, "the minimal cover by default (doc 15 §5.3)");
        string text = File.ReadAllText(file);
        written["written"]!.GetValue<bool>().ShouldBeTrue();
        text.ShouldBe(preview["text"]!.GetValue<string>());
        text.ShouldStartWith("// <auto-generated>");
        text.ShouldContain("namespace Orders.Tests.Workflows;");
        text.ShouldContain("WorkflowSpec.For<Orders.OrderFulfilmentWorkflowV1, Orders.OrdersModule>()");
        Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp-*").ShouldBeEmpty();

        // Selected paths regenerate the generated file.
        HttpResponseMessage two = await client.PostAsJsonAsync("api/services/orders/specs", new { workflow = "orders/workflows/order-fulfilment.sfflow.yaml", paths = new[] { "P01", "P02" } }, Ct);
        two.StatusCode.ShouldBe(HttpStatusCode.OK, await two.Content.ReadAsStringAsync(Ct));
        (await two.Content.ReadFromJsonAsync<JsonNode>(Ct))!["tests"]!.GetValue<int>().ShouldBe(2);
        File.ReadAllText(file).Length.ShouldBeLessThan(text.Length);
    }

    [Fact]
    [Trait("Id", "UT-STU-HOST-12")]
    [Trait("Covers", "WFD-NFR-5")]
    public async Task Specs_AreRefused_OutsideATestProject_OrOverAHandWrittenFile()
    {
        await using StudioHost host = await Start();
        using HttpClient client = Client(host);
        Task<HttpResponseMessage> Specs(string service, string workflow) => client.PostAsJsonAsync("api/services/" + service + "/specs", new { workflow }, Ct);
        const string Workflow = "orders/workflows/order-fulfilment.sfflow.yaml";

        (await Specs("nobody", Workflow)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Specs("inventory", Workflow)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "not inventory's workflow");
        (await Specs("orders", "docs/example.sfflow.yaml")).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "not a workflow file of the service");

        string hand = Path.Combine(_root, "orders", "tests", "Workflows", "OrderFulfilmentV1PathTests.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(hand)!);
        File.WriteAllText(hand, "// written by a person\n");
        HttpResponseMessage conflict = await Specs("orders", Workflow);
        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Error(conflict)).ShouldContain("isn't a file Studio generated");
        File.ReadAllText(hand).ShouldBe("// written by a person\n");
        File.Delete(hand);

        string domain = Path.Combine(_root, "sourceflow.domain.yaml");
        string original = File.ReadAllText(domain);
        File.WriteAllText(domain, original.Replace("    tests: orders/tests\n", "", StringComparison.Ordinal).Replace("    tests: orders/tests\r\n", "", StringComparison.Ordinal));
        (await Error(await Specs("orders", Workflow))).ShouldContain("has no 'tests'");
        File.WriteAllText(domain, original.Replace("orders/tests", "../elsewhere", StringComparison.Ordinal));
        (await Specs("orders", Workflow)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "only inside the workspace");
        File.WriteAllText(domain, original.Replace("orders/tests", "docs", StringComparison.Ordinal));
        (await Error(await Specs("orders", Workflow))).ShouldContain("not a project folder");
        File.WriteAllText(domain, original.Replace("    module: Orders.OrdersModule\n", "", StringComparison.Ordinal).Replace("    module: Orders.OrdersModule\r\n", "", StringComparison.Ordinal));
        (await Error(await Specs("orders", Workflow))).ShouldContain("has no 'module'");
        File.WriteAllText(domain, original);

        File.WriteAllText(Path.Combine(_root, "orders", "workflows", "order-fulfilment.sfflow.yaml"), "sourceflow.workflow: 1\nname: broken\n");
        (await Error(await Specs("orders", Workflow))).ShouldContain("has errors");
        File.Exists(hand).ShouldBeFalse();

        await using StudioHost readOnly = await Start(readOnly: true);
        using HttpClient reader = Client(readOnly);
        (await reader.PostAsJsonAsync("api/services/orders/specs", new { workflow = Workflow }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        File.WriteAllText(Path.Combine(_root, "orders", "workflows", "order-fulfilment.sfflow.yaml"), File.ReadAllText(Path.Combine(Repository(), "tests", "workflow-golden", "cases", "01-order-fulfilment", "workflow.sfflow.yaml")));
        (await reader.PostAsJsonAsync("api/services/orders/specs", new { workflow = Workflow, dryRun = true }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "a preview writes nothing");
        File.Exists(hand).ShouldBeFalse();
    }

    // ================================================================ helpers

    private Task<StudioHost> Start(bool readOnly = false, string? assets = null, string? framework = null)
    {
        return StudioHost.StartAsync(new StudioHostOptions(_root, Port: 0, readOnly, assets, framework, (project, _) =>
        {
            _exported.Add(project);
            return Task.FromResult("{\n  \"manifestVersion\": 1,\n  \"service\": \"orders\",\n  \"exported\": true\n}\n");
        }), Ct);
    }

    private static async Task<string> Error(HttpResponseMessage response)
    {
        return (await response.Content.ReadFromJsonAsync<JsonNode>(Ct))!["error"]!.GetValue<string>();
    }

    private static HttpClient Client(StudioHost host)
    {
        return new HttpClient { BaseAddress = host.Url };
    }

    private static HttpRequestMessage Put(string path, string content)
    {
        return new HttpRequestMessage(HttpMethod.Put, "api/files?path=" + Uri.EscapeDataString(path)) { Content = JsonContent.Create(new JsonObject { ["content"] = content }) };
    }

    private static string Repository()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "SourceFlow.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new InvalidOperationException("Repository root not found.");
    }
}

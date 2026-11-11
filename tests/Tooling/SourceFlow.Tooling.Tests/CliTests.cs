using System.Text.Json;
using System.Text.Json.Nodes;
using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Runtime;
using SourceFlow.Storage;
using SourceFlow.Testing;
using SourceFlow.Tool;

namespace SourceFlow.Tooling.Tests;

/// <summary>UT-CLI-01..30 (plan 05, WP-4.1): <c>dotnet sourceflow</c> parsing and handlers, run inside an in-memory service.</summary>
[Trait("Category", "Unit")]
public sealed class CliTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ================================================================ parsing and modes

    [Fact]
    [Trait("Id", "UT-CLI-01")]
    public async Task Help_ListsTheCommandGroups()
    {
        (int code, string output, _) = await RunAsync(null, "--help");

        code.ShouldBe(0);
        foreach (string group in new[] { "db", "dlq", "projection", "topology", "query", "schema" })
        {
            output.ShouldContain(group);
        }
    }

    [Fact]
    [Trait("Id", "UT-CLI-02")]
    public async Task UnknownCommands_AndMissingRequiredOptions_AreUsageErrors()
    {
        (int unknown, _, _) = await RunAsync(null, "frobnicate");
        (int missing, _, string error) = await RunAsync(null, "dlq", "discard", Guid.Empty.ToString());

        unknown.ShouldNotBe(0);
        missing.ShouldNotBe(0);
        error.ShouldContain("--reason");
    }

    [Fact]
    [Trait("Id", "UT-CLI-03")]
    public async Task DbScript_PrintsTheMigrationsForTheSchema()
    {
        (int code, string output, _) = await RunAsync(null, "db", "script", "--schema", "billing");

        code.ShouldBe(0);
        output.ShouldContain("CREATE TABLE \"billing\".events");
        output.ShouldContain("CREATE TABLE \"billing\".schemas");
    }

    [Fact]
    [Trait("Id", "UT-CLI-27")]
    public async Task ModelCommands_OutsideAService_ExplainHowToRunThem()
    {
        (int code, _, string error) = await RunAsync(null, "schema", "export");

        code.ShouldBe(1);
        error.ShouldContain("dotnet run --project <service> -- sourceflow schema export");
    }

    [Fact]
    [Trait("Id", "UT-CLI-28")]
    public async Task StoreCommands_WithoutAConnection_Fail()
    {
        string? previous = Environment.GetEnvironmentVariable("SOURCEFLOW_CONNECTION");
        Environment.SetEnvironmentVariable("SOURCEFLOW_CONNECTION", null);
        try
        {
            (int code, _, string error) = await RunAsync(null, "dlq", "list");

            code.ShouldBe(1);
            error.ShouldContain("--connection");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SOURCEFLOW_CONNECTION", previous);
        }
    }

    [Fact]
    [Trait("Id", "UT-CLI-29")]
    public async Task IndexSync_NeedsPostgreSql()
    {
        await using SourceFlowTestHost host = await BankAsync();

        (int code, _, string error) = await RunAsync(host, "db", "index", "sync");

        code.ShouldBe(1);
        error.ShouldContain("PostgreSQL");
    }

    [Fact]
    [Trait("Id", "UT-CLI-30")]
    public async Task TryRun_OnlyHandlesSourceFlowArguments()
    {
        await using SourceFlowTestHost host = await BankAsync();

        (await SourceFlowCli.TryRunAsync(["--urls", "http://*:80"], host.Services, Ct)).ShouldBeNull();
        (await SourceFlowCli.TryRunAsync(["sourceflow", "topology", "export"], host.Services, Ct)).ShouldBe(0);
    }

    // ================================================================ dlq

    [Fact]
    [Trait("Id", "UT-CLI-04")]
    [Trait("Id", "UT-CLI-05")]
    public async Task DlqListAndShow()
    {
        await using SourceFlowTestHost host = await BankAsync();
        Guid id = await ParkAsync(host);

        (int listed, string list, _) = await RunAsync(host, "dlq", "list", "--consumer", "accounts");
        (int shown, string show, _) = await RunAsync(host, "dlq", "show", id.ToString(), "--payload");

        listed.ShouldBe(0);
        list.ShouldContain(id.ToString());
        list.ShouldContain("1 dead letter(s)");
        shown.ShouldBe(0);
        show.ShouldContain("reason:       PermanentFailure");
        show.ShouldContain("bank.account.open v1");
        show.ShouldContain("payload:      {");
    }

    [Fact]
    [Trait("Id", "UT-CLI-06")]
    public async Task DlqReplay_ProcessesTheMessage()
    {
        await using SourceFlowTestHost host = await BankAsync();
        var account = AccountId.New();
        Guid id = await ParkAsync(host, new OpenAccount(account, "Ada", 5m));

        (int code, _, _) = await RunAsync(host, "dlq", "replay", id.ToString(), "--note", "fixed");
        await host.WaitForIdleAsync();

        code.ShouldBe(0);
        (await host.EventsAsync(StreamId.For(BankAccount.AggregateName, account))).ShouldHaveSingleItem();
        (await host.Store.GetAsync(id, Ct))!.Status.ShouldBe(DeadLetterStatus.Replayed);
    }

    [Fact]
    [Trait("Id", "UT-CLI-07")]
    [Trait("Id", "UT-CLI-08")]
    public async Task DlqDiscard_ThenPurge()
    {
        await using SourceFlowTestHost host = await BankAsync();
        Guid id = await ParkAsync(host);

        (int discarded, _, _) = await RunAsync(host, "dlq", "discard", id.ToString(), "--reason", "obsolete");
        (int purged, string output, _) = await RunAsync(host, "dlq", "purge", "--before", host.Time.GetUtcNow().AddMinutes(1).ToString("O"));

        discarded.ShouldBe(0);
        purged.ShouldBe(0);
        output.ShouldContain("Purged 1 dead letter(s)");
        (await host.Store.GetAsync(id, Ct)).ShouldBeNull();
    }

    // ================================================================ projection

    [Fact]
    [Trait("Id", "UT-CLI-09")]
    public async Task ProjectionSkip_MovesPastTheNextEvent_AndAudits()
    {
        await using SourceFlowTestHost host = await BankAsync(loops: false);
        await host.SendAsync(new OpenAccount(AccountId.New(), "Ada", 1m), cancellationToken: Ct);

        (int code, string output, _) = await RunAsync(host, "projection", "skip", Summary, "--reason", "bad data");

        code.ShouldBe(0);
        output.ShouldContain("Skipped the event at");
        (await host.Store.GetAsync(Summary, 0, Ct)).ShouldNotBe(GlobalPosition.Start);
        (await host.Store.QueryAsync(new DeadLetterQuery(Status: DeadLetterStatus.Discarded), Ct)).ShouldHaveSingleItem().StatusNote.ShouldBe("skipped: bad data");
    }

    [Fact]
    [Trait("Id", "UT-CLI-10")]
    [Trait("Id", "UT-CLI-11")]
    [Trait("Covers", "doc 06 3")]
    public async Task ProjectionRepartition_StartsNewPartitionsAtTheMinimum_AndRefusesBadCounts()
    {
        await using SourceFlowTestHost host = await BankAsync(loops: false, partitions: 2);
        LeaseToken lease = (await host.Store.TryAcquireAsync($"sub:{Summary}:1", "seed", TimeSpan.FromSeconds(1), Ct))!;
        await using (IStorageSession session = await host.Store.BeginAsync(MessageEnvelope.DefaultTenant, Ct))
        {
            session.SaveCheckpoint(Summary, 1, new GlobalPosition(9, 1), lease);
            await session.CommitAsync(Ct);
        }

        await host.Store.ReleaseAsync(lease, Ct);

        (int code, string output, _) = await RunAsync(host, "projection", "repartition", Summary, "--from", "2", "--to", "4");
        (int bad, _, string error) = await RunAsync(host, "projection", "repartition", Summary, "--from", "2", "--to", "3");

        code.ShouldBe(0);
        output.ShouldContain("4 partitions from 0:0");   // partition 0 had no checkpoint: the minimum is the start
        for (int p = 0; p < 4; p++)
        {
            (await host.Store.GetAsync(Summary, p, Ct)).ShouldBe(GlobalPosition.Start);
        }

        bad.ShouldBe(1);
        error.ShouldContain("powers of two");
    }

    [Fact]
    [Trait("Id", "UT-CLI-12")]
    [Trait("Id", "UT-CLI-13")]
    [Trait("Covers", "doc 06 6")]
    public async Task ProjectionRebuild_RefusesWhileRunning_ThenDropsTheDocumentsAndReplays_DropDeletesAGeneration()
    {
        var store = new SourceFlow.Storage.InMemory.InMemoryStore();
        var id = AccountId.New();
        await using (SourceFlowTestHost running = await BankAsync(store: store))
        {
            await running.SendAsync(new OpenAccount(id, "Ada", 1m), cancellationToken: Ct);
            await running.WaitForIdleAsync();
            (int refused, _, string error) = await RunAsync(running, "projection", "rebuild", "account-summary@1");
            refused.ShouldBe(1);
            error.ShouldContain("stop the subscription first");
        }

        await using (SourceFlowTestHost stopped = await BankAsync(loops: false, store: store))
        {
            (int rebuilt, string output, _) = await RunAsync(stopped, "projection", "rebuild", "account-summary@1");
            rebuilt.ShouldBe(0);
            output.ShouldContain("dropped 1 document(s)");
            (await stopped.DocumentAsync<AccountSummaryProjection, AccountSummary>(id.ToStreamSegment())).ShouldBeNull();
        }

        await using SourceFlowTestHost restarted = await BankAsync(store: store);
        await restarted.WaitForIdleAsync();
        (await restarted.DocumentAsync<AccountSummaryProjection, AccountSummary>(id.ToStreamSegment())).ShouldNotBeNull();   // replayed

        (int dropped, string drop, _) = await RunAsync(restarted, "projection", "drop", "account-summary@1");
        dropped.ShouldBe(0);
        drop.ShouldContain("Dropped 1 document(s)");
    }

    // ================================================================ topology

    [Fact]
    [Trait("Id", "UT-CLI-14")]
    [Trait("Id", "UT-CLI-15")]
    [Trait("Id", "UT-CLI-16")]
    public async Task Topology_ExportsJsonAndText_AndValidates()
    {
        await using SourceFlowTestHost host = await BankAsync(routing: r => r.Publish<AccountOpened>().As(AccountContracts.ToV1).To("accounts.events"));

        (int json, string exported, _) = await RunAsync(host, "topology", "export");
        (int text, string lines, _) = await RunAsync(host, "topology", "export", "--format", "text");
        (int valid, string validated, _) = await RunAsync(host, "topology", "validate");

        json.ShouldBe(0);
        JsonNode topology = JsonNode.Parse(exported)!;
        topology["endpoint"]!.GetValue<string>().ShouldBe("accounts");
        topology["publishes"]![0]!["topic"]!.GetValue<string>().ShouldBe("accounts.events");
        topology["publishes"]![0]!["messages"]![0]!.GetValue<string>().ShouldBe("accounts.account-opened");
        topology["commands"]!.AsArray().Select(c => c!.GetValue<string>()).ShouldContain("bank.account.open");
        text.ShouldBe(0);
        lines.ShouldContain("publish   accounts.events");
        valid.ShouldBe(0);
        validated.ShouldContain("in place");
    }

    // ================================================================ query

    [Fact]
    [Trait("Id", "UT-CLI-17")]
    [Trait("Id", "UT-CLI-22")]
    public async Task Query_Table_WithParameters()
    {
        await using SourceFlowTestHost host = await SeededAsync();

        (int code, string output, _) = await RunAsync(host, "query", "from readmodel \"account-summary\" | where balance > @min | project holder, balance | order by holder", "--param", "min=15");

        code.ShouldBe(0);
        output.ShouldContain("holder");
        output.ShouldContain("=cmd");
        output.ShouldNotContain("Ada");
        output.ShouldContain("(2 rows)");
    }

    [Fact]
    [Trait("Id", "UT-CLI-18")]
    [Trait("Id", "UT-CLI-19")]
    public async Task Query_JsonAndCsv_WithFormulaNeutralisation()
    {
        await using SourceFlowTestHost host = await SeededAsync();
        const string Query = "from readmodel \"account-summary\" | project holder, balance | order by holder";

        (_, string json, _) = await RunAsync(host, "query", Query, "--format", "json");
        (_, string csv, _) = await RunAsync(host, "query", Query, "--format", "csv");

        string[] rows = json.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        rows.Length.ShouldBe(3);
        JsonDocument.Parse(rows[1]).RootElement.GetProperty("holder").GetString().ShouldBe("Ada");
        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines[0].ShouldBe("holder,balance");
        lines[1].ShouldBe("'=cmd,20");
    }

    [Fact]
    [Trait("Id", "UT-CLI-20")]
    [Trait("Id", "UT-CLI-21")]
    public async Task Query_ExplainAndValidate()
    {
        await using SourceFlowTestHost host = await SeededAsync();

        (int explained, string plan, _) = await RunAsync(host, "query", "from readmodel \"account-summary\" | where balance > @min", "--param", "min=1", "--explain");
        (int invalid, _, string diagnostics) = await RunAsync(host, "query", "DELETE FROM x", "--sql", "--validate");

        explained.ShouldBe(0);
        plan.ShouldContain("engine:     in-memory");
        plan.ShouldContain("parameters: 1");
        invalid.ShouldBe(1);
        diagnostics.ShouldContain("SFQ0102");
    }

    // ================================================================ schema

    [Fact]
    [Trait("Id", "UT-CLI-23")]
    [Trait("Id", "UT-CLI-24")]
    [Trait("Id", "UT-CLI-25")]
    [Trait("Covers", "doc 14 2.2")]
    public async Task SchemaExport_AndCheckAgainstItself()
    {
        await using SourceFlowTestHost host = await BankAsync();
        string directory = Directory.CreateTempSubdirectory("sourceflow-cli-").FullName;
        string lockFile = Path.Combine(directory, "schemas.lock.json");
        string asyncApi = Path.Combine(directory, "asyncapi.json");

        (int exported, _, _) = await RunAsync(host, "schema", "export", "--out", lockFile);
        (int api, _, _) = await RunAsync(host, "schema", "export", "--format", "asyncapi", "--out", asyncApi);
        (int checkedCode, string output, _) = await RunAsync(host, "schema", "check", "--baseline", lockFile);

        exported.ShouldBe(0);
        JsonNode.Parse(await File.ReadAllTextAsync(lockFile, Ct))!["schemas"]!.AsArray().Count.ShouldBeGreaterThan(5);
        api.ShouldBe(0);
        JsonNode.Parse(await File.ReadAllTextAsync(asyncApi, Ct))!["asyncapi"]!.GetValue<string>().ShouldBe("3.0.0");
        checkedCode.ShouldBe(0);
        output.ShouldContain("Compatible");
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    [Trait("Id", "UT-CLI-26")]
    [Trait("Covers", "doc 14 2.2")]
    public async Task SchemaCheck_ABreakingChange_ExitsOne()
    {
        await using SourceFlowTestHost host = await BankAsync();
        string directory = Directory.CreateTempSubdirectory("sourceflow-cli-").FullName;
        string lockFile = Path.Combine(directory, "schemas.lock.json");
        await RunAsync(host, "schema", "export", "--out", lockFile);

        // The published bank.account.opened v1 had a field this build no longer has.
        JsonNode baseline = JsonNode.Parse(await File.ReadAllTextAsync(lockFile, Ct))!;
        JsonNode opened = baseline["schemas"]!.AsArray().First(s => s!["type"]!.GetValue<string>() == "bank.account.opened")!;
        opened["schema"]!["properties"]!["branch"] = new JsonObject { ["type"] = "string" };
        await File.WriteAllTextAsync(lockFile, baseline.ToJsonString(), Ct);

        (int code, _, string error) = await RunAsync(host, "schema", "check", "--baseline", lockFile);

        code.ShouldBe(1);
        error.ShouldContain("SFC03");
        error.ShouldContain("bank.account.opened v1.branch");
        Directory.Delete(directory, recursive: true);
    }

    // ================================================================ helpers

    private const string Summary = "projection:account-summary@1";

    private static async Task<(int Code, string Output, string Error)> RunAsync(SourceFlowTestHost? host, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int code = await SourceFlowCli.RunAsync(args, host?.Services, output, error, Ct);
        return (code, output.ToString(), error.ToString());
    }

    private static Task<SourceFlowTestHost> BankAsync(bool loops = true, int partitions = 1, Action<RoutingBuilder>? routing = null, SourceFlow.Storage.InMemory.InMemoryStore? store = null)
    {
        return SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<Bank.Contracts.ContractsModule>();
            sf.AddModule<AccountsModule>();
            sf.Projections(p => p.Add<AccountSummaryProjection>().Async());
            sf.Configure(o => o.SubscriptionPartitions = partitions);
            if (!loops)
            {
                sf.Roles = SourceFlowRoles.Api;
            }

            if (routing is not null)
            {
                sf.Routing(routing);
            }
        }, store: store);
    }

    private static async Task<SourceFlowTestHost> SeededAsync()
    {
        SourceFlowTestHost host = await BankAsync();
        await host.SendAsync(new OpenAccount(AccountId.New(), "Ada", 10m), cancellationToken: Ct);
        await host.SendAsync(new OpenAccount(AccountId.New(), "=cmd", 20m), cancellationToken: Ct);
        await host.SendAsync(new OpenAccount(AccountId.New(), "Grace", 30m), cancellationToken: Ct);
        await host.WaitForIdleAsync();
        return host;
    }

    private static async Task<Guid> ParkAsync(SourceFlowTestHost host, OpenAccount? command = null)
    {
        DateTimeOffset now = host.Time.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        await host.Store.ParkAsync(new DeadLetter
        {
            Id = id,
            Consumer = "accounts",
            Source = "receive",
            Envelope = host.Runtime.CreateEnvelope(command ?? new OpenAccount(AccountId.New(), "Ada", 1m)),
            Destination = "accounts",
            Reason = "PermanentFailure",
            ErrorType = "System.InvalidOperationException",
            ErrorMessage = "boom",
            Attempts = 1,
            FirstFailedAt = now,
            ParkedAt = now,
        }, Ct);
        return id;
    }
}

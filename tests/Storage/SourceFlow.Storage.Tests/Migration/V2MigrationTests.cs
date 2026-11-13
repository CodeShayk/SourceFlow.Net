using System.Text.Json;
using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using SourceFlow.Hosting;
using SourceFlow.Migration;
using SourceFlow.Runtime;
using SourceFlow.Storage.PostgreSQL;
using SourceFlow.Storage.SqlServer;
using SourceFlow.Storage.Tests.PostgreSql;
using SourceFlow.Storage.Tests.SqlServer;
using SourceFlow.Tool;

namespace SourceFlow.Storage.Tests.Migration;

/// <summary>MIG-01..09 (plan 06 §6, WP-5.4) with the v2 database and the v3 store on PostgreSQL.</summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "EndToEnd")]
[Trait("Provider", "PostgreSQL")]
[Trait("Requires", "Docker")]
public sealed class PostgreSqlV2MigrationTests(PostgreSqlServer server) : V2MigrationScenarios
{
    public override Task EnsureStartedAsync()
    {
        return server.EnsureStartedAsync();
    }

    protected override Task<V2Database> CreateV2Async()
    {
        return V2Database.CreateAsync(server.DataSource, V2Dialect.PostgreSql, PostgreSqlServer.NewSchema("v2"), server.ConnectionString);
    }

    protected override void UseV3Storage(SourceFlowBuilder sf, string schema)
    {
        sf.UsePostgreSql(o =>
        {
            o.DataSource = server.DataSource;
            o.Schema = schema;
        });
    }

    protected override string NewV3Schema()
    {
        return PostgreSqlServer.NewSchema("v3");
    }
}

/// <summary>MIG-01..09 with the v2 database and the v3 store on SQL Server (v2's default EF provider).</summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "EndToEnd")]
[Trait("Provider", "SqlServer")]
[Trait("Requires", "Docker")]
public sealed class SqlServerV2MigrationTests(SqlServerServer server) : V2MigrationScenarios, IDisposable
{
    private Microsoft.Data.SqlClient.SqlClientFactory? _factory;
    private System.Data.Common.DbDataSource? _dataSource;

    public override Task EnsureStartedAsync()
    {
        return server.EnsureStartedAsync();
    }

    protected override Task<V2Database> CreateV2Async()
    {
        _factory = Microsoft.Data.SqlClient.SqlClientFactory.Instance;
        _dataSource = _factory.CreateDataSource(server.ConnectionString);
        return V2Database.CreateAsync(_dataSource, V2Dialect.SqlServer, SqlServerServer.NewSchema("v2"), server.ConnectionString);
    }

    protected override void UseV3Storage(SourceFlowBuilder sf, string schema)
    {
        sf.UseSqlServer(server.ConnectionString, o => o.Schema = schema);
    }

    protected override string NewV3Schema()
    {
        return SqlServerServer.NewSchema("v3");
    }

    public void Dispose()
    {
        _dataSource?.Dispose();
    }
}

/// <summary>
/// The v2 data migration (doc 12 §3) against a seeded v2 database: the Bank sample's account aggregate imported from the
/// v2 E2E <c>BankAccount</c> table, through <c>dotnet sourceflow migrate v2 …</c> inside the service and as the global
/// tool.
/// </summary>
public abstract class V2MigrationScenarios : IAsyncLifetime
{
    private readonly List<IHost> _hosts = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public abstract Task EnsureStartedAsync();

    protected abstract Task<V2Database> CreateV2Async();

    protected abstract void UseV3Storage(SourceFlowBuilder sf, string schema);

    protected abstract string NewV3Schema();

    public async ValueTask InitializeAsync()
    {
        await EnsureStartedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IHost host in _hosts)
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    // ================================================================ plan (MIG-01..02)

    [Fact]
    [Trait("Id", "MIG-01")]
    [Trait("Id", "MIG-02")]
    [Trait("Covers", "doc 12 3;GAP-04;GAP-20")]
    public async Task Plan_CountsEntitiesAndCommands_ReportsLostHistory_AndGatesOnTablesWithoutAnImport()
    {
        V2Database v2 = await CreateV2Async();
        (IHost host, _) = await StartAsync(v2);

        // MIG-01: entities and commands per type; the order command of account 999 isn't counted for accounts.
        V2Plan plan = await host.Services.GetRequiredService<V2Migration>().PlanAsync(Ct);
        plan.Commands.ShouldBe(V2Database.CommandCount);
        V2PlanEntity accounts = plan.Entities.ShouldHaveSingleItem();
        accounts.ShouldBe(new V2PlanEntity("BankAccount", BankAccount.AggregateName, nameof(BankAccountImport), V2Database.Accounts.Count, V2Database.CommandCount - 2 - 1));
        plan.CommandTypes.Single(t => t.CommandType.Contains(".DepositMoney,", StringComparison.Ordinal)).Commands.ShouldBe(4);
        plan.EstimatedDuration.ShouldBeGreaterThan(TimeSpan.Zero);
        plan.Ready.ShouldBeTrue();

        // MIG-02: GAP-20 (two creates logged under id 0) and GAP-04 (a type v2 couldn't rebuild, and an empty payload).
        plan.EntityIdZero.ShouldBe(2);
        plan.NotRebuildable.ShouldBe(2);
        plan.CommandTypes.Single(t => t.CommandType == V2Database.ApplyInterest).NotRebuildable.ShouldBe(1);
        (int code, string output, _) = await CliAsync(host, "migrate", "v2", "plan");
        code.ShouldBe(0);
        output.ShouldContain("GAP-20, logged under entity id 0: 2; GAP-04, not rebuildable: 2");
        output.ShouldContain($"BankAccount -> bank-account (BankAccountImport): {V2Database.Accounts.Count} stream(s)");

        // The global tool has no imports: every v2 table lacks one, so the plan fails the gate.
        (int gate, string tool, string error) = await CliAsync(null, ["migrate", "v2", "plan", .. v2.ToolOptions]);
        gate.ShouldBe(1);
        tool.ShouldContain($"v2 commands: {V2Database.CommandCount}");
        error.ShouldContain("AccountViewModel");
        error.ShouldContain("BankAccount");
    }

    // ================================================================ scaffold (MIG-03)

    [Fact]
    [Trait("Id", "MIG-03")]
    [Trait("Covers", "doc 12 3")]
    public async Task Scaffold_GeneratesTheRowType_AndAnImportStub_PerTable()
    {
        V2Database v2 = await CreateV2Async();
        (IHost host, _) = await StartAsync(v2);

        // In the service, the aggregate named like the table is filled in.
        (int code, string output, _) = await CliAsync(host, "migrate", "v2", "scaffold", "--table", "BankAccount", "--namespace", "Legacy.Import");
        code.ShouldBe(0);
        output.ShouldContain("namespace Legacy.Import;");
        output.ShouldContain("public sealed record V2BankAccount(int Id, DateTime CreatedOn, string? AccountName, decimal Balance, bool IsClosed, string? ClosureReason, DateTime ActiveOn) : IV2Entity<V2BankAccount>");
        output.ShouldContain("public static string TableName => \"BankAccount\";");
        output.ShouldContain("row.Decimal(\"Balance\")");
        output.ShouldContain("row.Boolean(\"IsClosed\")");
        output.ShouldContain("public sealed class BankAccountImport : IV2Import<V2BankAccount, global::Bank.Accounts.BankAccount, global::Bank.Accounts.AccountId>");
        output.ShouldContain("return V2Ids.FromLegacy<global::Bank.Accounts.AccountId>(entity.Id);");
        output.ShouldNotContain("#warning");

        // The global tool scaffolds every table into files; without a model the stub names placeholders.
        string directory = Path.Combine(Path.GetTempPath(), "sf-scaffold-" + Guid.CreateVersion7().ToString("N"));
        try
        {
            (int written, _, _) = await CliAsync(null, ["migrate", "v2", "scaffold", "--output", directory, .. v2.ToolOptions]);
            written.ShouldBe(0);
            Directory.GetFiles(directory).Select(Path.GetFileName).Order().ShouldBe(["AccountViewModelImport.cs", "BankAccountImport.cs"]);
            string stub = await File.ReadAllTextAsync(Path.Combine(directory, "AccountViewModelImport.cs"), Ct);
            stub.ShouldContain("#warning No v3 aggregate is named like the v2 table 'AccountViewModel'");
            stub.ShouldContain("IV2Import<V2AccountViewModel, TodoAggregate, TodoId>");
            stub.ShouldContain("int TransactionCount");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    // ================================================================ import (MIG-04..06)

    [Fact]
    [Trait("Id", "MIG-04")]
    [Trait("Id", "MIG-06")]
    [Trait("Covers", "doc 12 3;ES-R2")]
    public async Task Import_IsIdempotent_OneSessionPerStream_WithMigrationMetadata_AndProjectsInline()
    {
        V2Database v2 = await CreateV2Async();
        (IHost host, _) = await StartAsync(v2);
        V2Migration migration = host.Services.GetRequiredService<V2Migration>();

        V2ImportResult first = await migration.ImportAsync(new V2ImportOptions { BatchSize = 7 }, Ct);
        first.Complete.ShouldBeTrue();
        first.Imported.ShouldBe(V2Database.Accounts.Count);
        first.Skipped.ShouldBe(0);

        // MIG-04: a second run appends nothing (ExpectedVersion.NoStream).
        (int code, string output, _) = await CliAsync(host, "migrate", "v2", "import", "--batch", "5");
        code.ShouldBe(0);
        output.ShouldContain($"Imported 0 stream(s); {V2Database.Accounts.Count} already existed.");

        // MIG-06: each stream holds its genesis events with the migration headers and deterministic ids.
        SourceFlowRuntime runtime = host.Services.GetRequiredService<SourceFlowRuntime>();
        StreamId closed = StreamId.For(BankAccount.AggregateName, V2Ids.FromLegacy<AccountId>(999));
        StreamSlice events = await runtime.Storage.Events.ReadStreamAsync(runtime.Options.DefaultTenant, closed, new StreamVersion(1), 10, Ct);
        events.Events.Select(e => e.Type).ShouldBe(["bank.account.opened", "bank.account.closed"]);
        events.Events.Select(e => e.StreamVersion.Value).ShouldBe([1L, 2L]);
        foreach (StoredEvent stored in events.Events)
        {
            stored.Metadata.Actor.ShouldBe(V2Migration.Actor);
            stored.Metadata.Headers[V2Migration.MigratedFromHeader].ShouldBe("v2");
            stored.Metadata.Headers[V2Migration.CommandCountHeader].ShouldBe("6");      // the order command is another entity's
            stored.Metadata.Headers[V2Migration.LastSequenceNoHeader].ShouldBe("6");
            stored.Metadata.Headers[V2Migration.V2IdHeader].ShouldBe("999");
        }

        StoredEvent ada = (await runtime.Storage.Events.ReadStreamAsync(runtime.Options.DefaultTenant, StreamId.For(BankAccount.AggregateName, V2Ids.FromLegacy<AccountId>(1001)), new StreamVersion(1), 10, Ct)).Events.ShouldHaveSingleItem();
        ada.Metadata.Headers[V2Migration.CommandCountHeader].ShouldBe("3");
        ada.EventId.ShouldBe(Messaging.MessageIds.FromKey("sourceflow-v2-import", runtime.Options.DefaultTenant + "\n" + ada.StreamId.Value + "\n1"));

        // Step 5: the inline summary is built from the imported events.
        (await SummaryAsync(host, V2Ids.FromLegacy<AccountId>(999)))!.ShouldBe(new AccountSummary(V2Ids.FromLegacy<AccountId>(999), "John Doe", 1400m, AccountStatus.Closed, 2));
        (await SummaryAsync(host, V2Ids.FromLegacy<AccountId>(1001)))!.Balance.ShouldBe(520.5m);

        // An imported account takes v3 commands like any other.
        ICommandSender commands = host.Services.GetRequiredService<ICommandSender>();
        (await commands.SendAsync(new Deposit(V2Ids.FromLegacy<AccountId>(1000), 50m), null, Ct)).Version.ShouldBe(new StreamVersion(2));
    }

    [Fact]
    [Trait("Id", "MIG-05")]
    [Trait("Covers", "doc 12 3")]
    public async Task Import_ResumesAfterAnInterruption_AndAfterALimitedWave()
    {
        V2Database v2 = await CreateV2Async();
        var import = new BankAccountImport { FailOn = 2010 };
        (IHost host, _) = await StartAsync(v2, import);
        V2Migration migration = host.Services.GetRequiredService<V2Migration>();

        // Interrupted at v2 account 2010: the 13 streams before it (999, 1000, 1001, 2000..2009) are committed.
        await Should.ThrowAsync<InvalidOperationException>(async () => await migration.ImportAsync(new V2ImportOptions { BatchSize = 4 }, Ct));
        import.FailOn = null;

        // A limited wave continues from there...
        V2ImportResult wave = await migration.ImportAsync(new V2ImportOptions { BatchSize = 4, Limit = 10 }, Ct);
        wave.Complete.ShouldBeFalse();
        wave.Imported.ShouldBe(10);
        wave.Skipped.ShouldBe(13);

        // ...and the next run finishes, repeating nothing.
        (int code, string output, _) = await CliAsync(host, "migrate", "v2", "import");
        code.ShouldBe(0);
        output.ShouldContain($"Imported {V2Database.Accounts.Count - 23} stream(s); 23 already existed.");
        (await migration.VerifyAsync(cancellationToken: Ct)).Passed.ShouldBeTrue();
    }

    // ================================================================ archive (MIG-07)

    [Fact]
    [Trait("Id", "MIG-07")]
    [Trait("Covers", "doc 12 3")]
    public async Task Archive_CopiesTheCommandTable_ToAReadOnlyAuditCopy()
    {
        V2Database v2 = await CreateV2Async();

        (int code, string output, _) = await CliAsync(null, ["migrate", "v2", "archive", .. v2.ToolOptions]);
        code.ShouldBe(0);
        output.ShouldContain($"Archived {V2Database.CommandCount} command(s); {v2.ArchiveSchema}.commands holds {V2Database.CommandCount} of {V2Database.CommandCount}.");

        // Re-running copies only new rows.
        await v2.ExecuteAsync($"INSERT INTO {v2.T("CommandRecord")} ({v2.Q("EntityId")}, {v2.Q("SequenceNo")}, {v2.Q("CommandName")}, {v2.Q("CommandType")}, {v2.Q("PayloadType")}, {v2.Q("PayloadData")}, {v2.Q("Metadata")}, {v2.Q("Timestamp")}, {v2.Q("CreatedAt")}, {v2.Q("UpdatedAt")}) "
            + "VALUES (1000, 3, 'DepositMoney', 'x', 'y', '{}', '{}', '2025-02-01T00:00:00', '2025-02-01T00:00:00', '2025-02-01T00:00:00')");
        V2ArchiveResult again = await V2Migration.ForSource(v2.Source).ArchiveAsync(Ct);
        again.ShouldBe(new V2ArchiveResult(1, V2Database.CommandCount + 1, V2Database.CommandCount + 1));

        // Same columns and values as the source; updates and deletes are refused.
        string archive = v2.Q(v2.ArchiveSchema) + "." + v2.Q("commands");
        (await v2.ScalarAsync($"SELECT COUNT(*) FROM {archive} a JOIN {v2.T("CommandRecord")} c ON c.{v2.Q("Id")} = a.{v2.Q("Id")} AND c.{v2.Q("CommandType")} = a.{v2.Q("CommandType")} AND c.{v2.Q("PayloadData")} = a.{v2.Q("PayloadData")} AND c.{v2.Q("SequenceNo")} = a.{v2.Q("SequenceNo")}"))
            .ShouldBe(V2Database.CommandCount + 1);
        await Should.ThrowAsync<System.Data.Common.DbException>(async () => await v2.ExecuteAsync($"UPDATE {archive} SET {v2.Q("PayloadData")} = 'tampered'"));
        await Should.ThrowAsync<System.Data.Common.DbException>(async () => await v2.ExecuteAsync($"DELETE FROM {archive}"));
        (await v2.ScalarAsync($"SELECT COUNT(*) FROM {archive} WHERE {v2.Q("PayloadData")} = 'tampered'")).ShouldBe(0);
    }

    // ================================================================ verify (MIG-08..09)

    [Fact]
    [Trait("Id", "MIG-08")]
    [Trait("Id", "MIG-09")]
    [Trait("Covers", "doc 12 3")]
    public async Task Verify_PassesAfterTheImport_AndAMismatchReportFailsTheExitCode()
    {
        V2Database v2 = await CreateV2Async();
        (IHost host, _) = await StartAsync(v2);
        await host.Services.GetRequiredService<V2Migration>().ImportAsync(cancellationToken: Ct);

        // MIG-08: the rebuilt v3 state matches every v2 row.
        (int code, string output, _) = await CliAsync(host, "migrate", "v2", "verify");
        code.ShouldBe(0);
        output.ShouldContain($"Verified {V2Database.Accounts.Count} entities: no mismatches.");

        // MIG-09: v2 changed after the import (a write during the cut-over), and an account the import never saw.
        await v2.ExecuteAsync($"UPDATE {v2.T("BankAccount")} SET {v2.Q("Balance")} = 275 WHERE {v2.Q("Id")} = 1000");
        await v2.ExecuteAsync($"INSERT INTO {v2.T("BankAccount")} VALUES (3000, '2025-03-01T00:00:00', 'Late Comer', 5, {(v2.Dialect == V2Dialect.SqlServer ? "0" : "false")}, NULL, '2025-03-01T00:00:00')");
        (int failed, string summary, string report) = await CliAsync(host, "migrate", "v2", "verify");
        failed.ShouldBe(1);
        summary.ShouldContain($"Verified {V2Database.Accounts.Count + 1} entities: 2 mismatch(es).");
        report.ShouldContain("MISMATCH BankAccount 1000");
        report.ShouldContain("Balance: v2 275.00, v3 250.00");
        report.ShouldContain("MISMATCH BankAccount 3000");
        report.ShouldContain("not imported");
    }

    // ================================================================ plumbing

    private async Task<(IHost Host, string Schema)> StartAsync(V2Database v2, BankAccountImport? import = null)
    {
        string schema = NewV3Schema();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development, DisableDefaults = true });
        builder.Services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.Projections(p => p.Add<AccountSummaryProjection>().Inline());
            UseV3Storage(sf, schema);
            sf.MigrateFromV2(m => m
                .From(v2.Source)
                .Import(import ?? new BankAccountImport())
                .Ignore("AccountViewModel")
                .NotRebuildable("Legacy.Banking.ApplyInterest, Legacy.Banking"));
        });
        IHost host = builder.Build();
        await host.StartAsync(Ct);
        _hosts.Add(host);
        return (host, schema);
    }

    private static async Task<(int Code, string Output, string Error)> CliAsync(IHost? host, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int code = await SourceFlowCli.RunAsync(args, host?.Services, output, error, Ct);
        return (code, output.ToString(), error.ToString());
    }

    private static async Task<AccountSummary?> SummaryAsync(IHost host, AccountId id)
    {
        SourceFlowRuntime runtime = host.Services.GetRequiredService<SourceFlowRuntime>();
        ProjectionDescriptor projection = runtime.Model.Projections.Single(p => p.ProjectionType == typeof(AccountSummaryProjection));
        StoredDocument? stored = await runtime.Storage.Documents.GetAsync(runtime.Options.DefaultTenant, projection.Collection, id.ToStreamSegment(), Ct);
        return stored is null ? null : (AccountSummary?)JsonSerializer.Deserialize(stored.Data.Span, runtime.Model.JsonTypeInfoFor(typeof(AccountSummary)));
    }
}

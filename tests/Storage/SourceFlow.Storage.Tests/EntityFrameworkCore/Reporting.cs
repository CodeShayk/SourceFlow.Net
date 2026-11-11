using Bank.Accounts;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SourceFlow.Storage.EntityFrameworkCore;

namespace SourceFlow.Storage.Tests.EntityFrameworkCore;

/// <summary>A relational read model: one row per account.</summary>
public sealed class AccountActivity : IVersionedReadModel
{
    public Guid AccountId { get; set; }

    public string Holder { get; set; } = string.Empty;

    public decimal Balance { get; set; }

    public int Movements { get; set; }

    public long Version { get; set; }
}

/// <summary>
/// The reporting context; its tables live in the shared <c>reporting</c> schema, keyed by unique account ids, or in a
/// schema per read-model generation (UT-EF-11..13).
/// </summary>
public sealed class ReportingDbContext(DbContextOptions<ReportingDbContext> options, string schema) : DbContext(options), IGenerationSchemaContext
{
    public const string SharedSchema = "reporting";

    public ReportingDbContext(DbContextOptions<ReportingDbContext> options)
        : this(options, SharedSchema)
    {
    }

    public string Schema { get; } = schema;

    public DbSet<AccountActivity> Activity => Set<AccountActivity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<AccountActivity>(e =>
        {
            e.ToTable("account_activity");
            e.HasKey(a => a.AccountId);
        });
    }

    public static ReportingDbContext Create(DbContextOptions<ReportingDbContext> options)
    {
        return new ReportingDbContext(options);
    }

    public static void Configure(DbContextOptionsBuilder<ReportingDbContext> options, System.Data.Common.DbConnection connection)
    {
        options.UseNpgsql(connection);
    }

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HashSet<string> Created = [];

    /// <summary>Creates the reporting tables once per server.</summary>
    public static async Task EnsureCreatedAsync(NpgsqlDataSource dataSource)
    {
        await Gate.WaitAsync();
        try
        {
            if (!Created.Add(dataSource.ConnectionString))
            {
                return;
            }

            var options = new DbContextOptionsBuilder<ReportingDbContext>().UseNpgsql(dataSource).Options;
            await using var db = new ReportingDbContext(options);
            await using NpgsqlCommand exists = dataSource.CreateCommand("SELECT to_regclass('reporting.account_activity') IS NOT NULL");
            if (await exists.ExecuteScalarAsync() is not true)
            {
                // EF's own DDL for the model (no user input).
                await using NpgsqlCommand create = dataSource.CreateCommand("CREATE SCHEMA IF NOT EXISTS reporting;\n" + db.Database.GenerateCreateScript());
                await create.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}

/// <summary>An EF Core projection of the Bank accounts (doc 03 §6.3), version-guarded for replays.</summary>
[Projection("account-activity")]
public sealed partial class AccountActivityProjection : IEfProjection<ReportingDbContext>
{
    public async ValueTask When(AccountOpened e, ReportingDbContext db, EventContext ctx, CancellationToken ct)
    {
        await db.Activity.UpsertAsync(e.AccountId.Value, ctx.StreamVersion.Value,
            _ => new AccountActivity { AccountId = e.AccountId.Value, Holder = e.Holder, Balance = e.InitialDeposit }, ct);
    }

    public async ValueTask When(MoneyDeposited e, ReportingDbContext db, EventContext ctx, CancellationToken ct)
    {
        await db.Activity.UpsertAsync(e.AccountId.Value, ctx.StreamVersion.Value, row => Move(row, e.AccountId, e.BalanceAfter), ct);
    }

    public async Task When(MoneyWithdrawn e, ReportingDbContext db, EventContext ctx, CancellationToken ct)
    {
        await db.Activity.UpsertAsync(e.AccountId.Value, ctx.StreamVersion.Value, row => Move(row, e.AccountId, e.BalanceAfter), ct);
    }

    private static AccountActivity Move(AccountActivity? row, AccountId id, decimal balance)
    {
        row ??= new AccountActivity { AccountId = id.Value };
        row.Balance = balance;
        row.Movements++;
        return row;
    }
}

/// <summary>The reporting module: registers the EF projection (generated).</summary>
[SourceFlowModule]
public sealed partial class ReportingModule;

using System.CommandLine;
using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Migration;
using ParseResult = System.CommandLine.ParseResult;

namespace SourceFlow.Tool;

/// <summary>
/// <c>migrate v2 plan|scaffold|import|archive|verify</c> (doc 12 §3, WP-5.4). Inside a service the migration registered
/// with <c>sf.MigrateFromV2(…)</c> is used; the global tool runs <c>plan</c>, <c>scaffold</c> and <c>archive</c> against
/// <c>--v2-connection</c>. Exit codes: <c>plan</c> 1 when a v2 table has no import, <c>verify</c> 1 on any mismatch.
/// </summary>
internal sealed partial class Cli
{
    private readonly Option<string?> _v2Connection = new("--v2-connection") { Description = "The v2 database's connection string (or SOURCEFLOW_V2_CONNECTION). Not needed inside a service that calls MigrateFromV2." };
    private readonly Option<string> _v2Provider = new("--v2-provider") { Description = "postgresql (default) or sqlserver.", DefaultValueFactory = _ => "postgresql" };
    private readonly Option<string?> _v2Schema = new("--v2-schema") { Description = "The schema of the v2 tables (default: the connection's)." };
    private readonly Option<string> _v2ArchiveSchema = new("--v2-archive-schema") { Description = "The schema of the archive.", DefaultValueFactory = _ => V2Source.DefaultArchiveSchema };
    private readonly Option<string> _v2CommandTable = new("--v2-command-table") { Description = "The v2 command table after the naming convention.", DefaultValueFactory = _ => "CommandRecord" };

    private Command MigrateCommand()
    {
        var plan = V2(new Command("plan", "Count v2 entities and commands, report commands lost to GAP-04 and GAP-20, and check that every v2 table has an import (exit 1 if not)."));
        plan.SetAction((r, ct) => Guard(() => WithMigrationAsync(r, async migration =>
        {
            V2Plan result = await migration.PlanAsync(ct);
            await WritePlanAsync(result);
            return result.Ready ? 0 : 1;
        })));

        var tables = new Option<string[]>("--table") { Description = "A v2 table (repeatable; default: every table without an import)." };
        var ns = new Option<string>("--namespace") { Description = "The namespace of the generated code.", DefaultValueFactory = _ => "V2Migration" };
        var outputDirectory = new Option<DirectoryInfo?>("--output") { Description = "Write one file per table to this directory instead of standard output." };
        var scaffold = V2(new Command("scaffold", "Generate the v2 row type and an IV2Import stub per v2 table."), tables, ns, outputDirectory);
        scaffold.SetAction((r, ct) => Guard(() => WithMigrationAsync(r, async migration =>
        {
            string[]? selected = r.GetValue(tables) is { Length: > 0 } t ? t : null;
            IReadOnlyList<V2ScaffoldFile> files = await migration.ScaffoldAsync(selected, r.GetValue(ns)!, ct);
            DirectoryInfo? directory = r.GetValue(outputDirectory);
            foreach (V2ScaffoldFile file in files)
            {
                if (directory is null)
                {
                    await Out.WriteLineAsync("// " + file.FileName);
                    await Out.WriteLineAsync(file.Content);
                    continue;
                }

                directory.Create();
                string path = Path.Combine(directory.FullName, file.FileName);
                await File.WriteAllTextAsync(path, file.Content, ct);
                await Out.WriteLineAsync("Wrote " + path);
            }

            if (files.Count == 0)
            {
                await Out.WriteLineAsync("Every v2 table has an import or is ignored; pass --table to scaffold one.");
            }

            return 0;
        })));

        var batch = new Option<int>("--batch") { Description = "v2 rows read per query.", DefaultValueFactory = _ => 1000 };
        var limit = new Option<long?>("--limit") { Description = "Import at most this many new streams in this run (waves)." };
        var tenant = new Option<string?>("--tenant") { Description = "The v3 tenant (default: the service's default tenant)." };
        var import = V2(new Command("import", "Import every v2 entity as genesis events: one session per stream, ExpectedVersion.NoStream, so a re-run resumes and repeats nothing."), batch, limit, tenant);
        import.SetAction((r, ct) => Guard(() => WithMigrationAsync(r, async migration =>
        {
            V2ImportResult result = await migration.ImportAsync(new V2ImportOptions { BatchSize = r.GetValue(batch), Limit = r.GetValue(limit), TenantId = r.GetValue(tenant) }, ct);
            foreach (V2ImportTableResult table in result.Tables)
            {
                await Out.WriteLineAsync($"{table.Table} -> {table.Aggregate}: {table.Imported} imported, {table.Skipped} already imported");
            }

            await Out.WriteLineAsync(result.Complete
                ? $"Imported {result.Imported} stream(s); {result.Skipped} already existed."
                : $"Imported {result.Imported} stream(s) and stopped at --limit; run again to continue.");
            return 0;
        })));

        var archive = V2(new Command("archive", $"Copy the v2 command table to {V2Source.DefaultArchiveSchema}.{V2Source.ArchiveTable} (or --v2-archive-schema), a read-only audit copy."));
        archive.SetAction((r, ct) => Guard(() => WithMigrationAsync(r, async migration =>
        {
            V2ArchiveResult result = await migration.ArchiveAsync(ct);
            await Out.WriteLineAsync($"Archived {result.Copied} command(s); {migration.Source.ArchiveSchema}.{V2Source.ArchiveTable} holds {result.Archived} of {result.Source}.");
            return result.Archived >= result.Source ? 0 : 1;
        })));

        var max = new Option<int>("--max") { Description = "The most mismatches to print.", DefaultValueFactory = _ => 50 };
        var verify = V2(new Command("verify", "Compare the rebuilt v3 state of every v2 entity with its v2 row; exit 1 on any mismatch (the cut-over gate)."), tenant, batch, max);
        verify.SetAction((r, ct) => Guard(() => WithMigrationAsync(r, async migration =>
        {
            V2VerifyReport report = await migration.VerifyAsync(r.GetValue(tenant), r.GetValue(batch), ct);
            foreach (V2Mismatch mismatch in report.Mismatches.Take(r.GetValue(max)))
            {
                await Err.WriteLineAsync($"MISMATCH {mismatch.Table} {mismatch.V2Id} ({mismatch.Stream}): {string.Join("; ", mismatch.Differences)}");
            }

            await Out.WriteLineAsync(report.Passed
                ? $"Verified {report.Checked} entities: no mismatches."
                : $"Verified {report.Checked} entities: {report.Mismatches.Count} mismatch(es).");
            return report.Passed ? 0 : 1;
        })));

        var v2 = new Command("v2", "Migrate a SourceFlow v2 database (doc 12 §3).");
        foreach (Command command in new[] { plan, scaffold, import, archive, verify })
        {
            v2.Subcommands.Add(command);
        }

        var migrate = new Command("migrate", "Migrations from earlier SourceFlow versions.");
        migrate.Subcommands.Add(v2);
        return migrate;
    }

    private Command V2(Command command, params Option[] options)
    {
        command.Options.Add(_v2Connection);
        command.Options.Add(_v2Provider);
        command.Options.Add(_v2Schema);
        command.Options.Add(_v2CommandTable);
        command.Options.Add(_v2ArchiveSchema);
        foreach (Option option in options)
        {
            command.Options.Add(option);
        }

        return command;
    }

    private async Task<int> WithMigrationAsync(ParseResult r, Func<V2Migration, Task<int>> action)
    {
        string? connection = r.GetValue(_v2Connection) ?? Environment.GetEnvironmentVariable("SOURCEFLOW_V2_CONNECTION");
        if (connection is null && services?.GetService<V2Migration>() is { } registered)
        {
            return await action(registered);
        }

        if (connection is null)
        {
            throw new CliException("Pass --v2-connection or set SOURCEFLOW_V2_CONNECTION (or run inside a service that calls sf.MigrateFromV2).");
        }

        (V2Dialect dialect, DbDataSource dataSource) = r.GetValue(_v2Provider)?.ToLowerInvariant() switch
        {
            "postgresql" or "postgres" or "npgsql" => (V2Dialect.PostgreSql, (DbDataSource)Npgsql.NpgsqlDataSource.Create(connection)),
            "sqlserver" or "mssql" => (V2Dialect.SqlServer, Microsoft.Data.SqlClient.SqlClientFactory.Instance.CreateDataSource(connection)),
            var other => throw new CliException($"Unknown --v2-provider '{other}': use postgresql or sqlserver."),
        };

        await using (dataSource)
        {
            var source = new V2Source(dataSource, dialect) { Schema = r.GetValue(_v2Schema), CommandTable = r.GetValue(_v2CommandTable)!, ArchiveSchema = r.GetValue(_v2ArchiveSchema)! };
            return await action(V2Migration.ForSource(source));
        }
    }

    private async Task WritePlanAsync(V2Plan plan)
    {
        await Out.WriteLineAsync($"v2 commands: {plan.Commands} (GAP-20, logged under entity id 0: {plan.EntityIdZero}; GAP-04, not rebuildable: {plan.NotRebuildable})");
        foreach (V2PlanCommandType type in plan.CommandTypes.OrderByDescending(t => t.Commands))
        {
            await Out.WriteLineAsync($"  {type.Commands,8} {(type.CommandType.Length == 0 ? "(no type)" : V2TypeNames.Normalize(type.CommandType))}"
                + (type.EntityIdZero > 0 ? $"  id 0: {type.EntityIdZero}" : string.Empty)
                + (type.NotRebuildable > 0 ? $"  not rebuildable: {type.NotRebuildable}" : string.Empty));
        }

        await Out.WriteLineAsync("Entities:");
        foreach (V2PlanEntity entity in plan.Entities)
        {
            await Out.WriteLineAsync($"  {entity.Table} -> {entity.Aggregate} ({entity.Import}): {entity.Rows} stream(s), {entity.Commands} command(s)");
        }

        if (plan.Entities.Count == 0)
        {
            await Out.WriteLineAsync("  (no imports registered)");
        }

        await Out.WriteLineAsync("Estimated import: " + plan.EstimatedDuration.ToString("c", CultureInfo.InvariantCulture));
        if (plan.EntityIdZero + plan.NotRebuildable > 0)
        {
            await Out.WriteLineAsync("Lost history: the commands above can't be attributed or rebuilt; the current state is still migrated.");
        }

        if (!plan.Ready)
        {
            await Err.WriteLineAsync("No import for v2 table(s): " + string.Join(", ", plan.TablesWithoutImport) + " (add an IV2Import, or Ignore(…) tables that hold no entities).");
        }
    }
}

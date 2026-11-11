using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using SourceFlow.Benchmarks;

// dotnet run -c Release --project benchmarks/SourceFlow.Benchmarks -- bench [--filter *Command*] [--job short]
// dotnet run -c Release --project benchmarks/SourceFlow.Benchmarks -- measure
// dotnet run -c Release --project benchmarks/SourceFlow.Benchmarks -- baseline   (both, writes benchmarks/baselines/*.json)
string mode = args.Length > 0 ? args[0] : "bench";
string[] rest = args.Skip(1).ToArray();
string baselines = Path.Combine(RepositoryRoot(), "benchmarks", "baselines");
var options = new JsonSerializerOptions { WriteIndented = true };

if (mode is "bench" or "baseline")
{
    IConfig config = DefaultConfig.Instance
        .AddJob(Job.ShortRun.WithIterationCount(5).WithWarmupCount(3).WithId("baseline"))
        .WithOptions(ConfigOptions.DisableOptimizationsValidator);
    Summary[] summaries = mode == "baseline"
        ? BenchmarkRunner.Run([typeof(CommandBenchmarks), typeof(MiddlewareBenchmarks), typeof(PostgreSqlCommitBenchmarks), typeof(LoadBenchmarks), typeof(LogReadBenchmarks)], config)
        : BenchmarkSwitcher.FromAssembly(typeof(BenchModule).Assembly).Run(rest, config).ToArray();

    var rows = summaries.SelectMany(s => s.Reports).Where(r => r.ResultStatistics is not null).Select(r => new
    {
        benchmark = r.BenchmarkCase.Descriptor.WorkloadMethodDisplayInfo,
        parameters = r.BenchmarkCase.Parameters.DisplayInfo,
        meanMicroseconds = Math.Round(r.ResultStatistics!.Mean / 1000, 3),
        p50Microseconds = Math.Round(r.ResultStatistics.Percentiles.P50 / 1000, 3),
        p95Microseconds = Math.Round(r.ResultStatistics.Percentiles.P95 / 1000, 3),
        allocatedBytesPerOperation = r.GcStats.GetBytesAllocatedPerOperation(r.BenchmarkCase),
    }).ToArray();
    if (mode == "baseline")
    {
        Write("benchmarks.json", new { environment = RunEnvironment(), results = rows });
    }
}

if (mode is "measure" or "baseline")
{
    IReadOnlyList<PerfMeasurement> measured = await Measurements.RunAsync();
    foreach (PerfMeasurement m in measured)
    {
        Console.WriteLine($"{m.Id} {m.Name}: {m.Value.ToString("F1", CultureInfo.InvariantCulture)} {m.Unit} (target {m.Target}) {(m.Met ? "MET" : "NOT MET")} {m.Notes}");
    }

    Write("measurements.json", new { environment = RunEnvironment(), results = measured });
}

return;

void Write(string file, object content)
{
    Directory.CreateDirectory(baselines);
    File.WriteAllText(Path.Combine(baselines, file), JsonSerializer.Serialize(content, options) + "\n");
    Console.WriteLine("Wrote " + Path.Combine(baselines, file));
}

static object RunEnvironment()
{
    return new
    {
        os = RuntimeInformation.OSDescription,
        runtime = RuntimeInformation.FrameworkDescription,
        processors = Environment.ProcessorCount,
        database = "postgres:17-alpine (Testcontainers, local Docker)",
        recordedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };
}

static string RepositoryRoot()
{
    DirectoryInfo? dir = new(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SourceFlow.slnx")))
    {
        dir = dir.Parent;
    }

    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

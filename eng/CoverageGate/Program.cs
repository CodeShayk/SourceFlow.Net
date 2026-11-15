// Coverage gate (implementation plan 04 §5).
// Usage: CoverageGate <merged-cobertura.xml> <thresholds.json> [baseline-cobertura.xml]
//   - Fails when a package whose milestone has exited ("enforce": true) is below its line/branch target.
//   - Fails when any package drops more than "maxDropPoints" against the baseline report (if given).
//   - Packages that are not yet enforced are reported only.
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: CoverageGate <cobertura.xml> <thresholds.json> [baseline-cobertura.xml]");
    return 2;
}

Dictionary<string, (double Line, double Branch)> current = ReadCobertura(args[0]);
Dictionary<string, (double Line, double Branch)>? baseline = args.Length > 2 && File.Exists(args[2]) ? ReadCobertura(args[2]) : null;
using JsonDocument thresholds = JsonDocument.Parse(File.ReadAllText(args[1]));
double maxDrop = thresholds.RootElement.GetProperty("maxDropPoints").GetDouble();

int failures = 0;
Console.WriteLine($"{"Package",-45} {"Line",7} {"Target",7} {"Branch",7} {"Target",7}  Status");
foreach (JsonProperty package in thresholds.RootElement.GetProperty("packages").EnumerateObject())
{
    double lineTarget = package.Value.GetProperty("line").GetDouble();
    double branchTarget = package.Value.GetProperty("branch").GetDouble();
    bool enforce = package.Value.GetProperty("enforce").GetBoolean();

    if (!current.TryGetValue(package.Name, out var measured))
    {
        Console.WriteLine($"{package.Name,-45} {"-",7} {lineTarget,7:0.0} {"-",7} {branchTarget,7:0.0}  not measured");
        continue;
    }

    bool below = measured.Line < lineTarget || measured.Branch < branchTarget;
    string status = below ? (enforce ? "FAIL (below target)" : "report only") : "ok";
    if (below && enforce)
    {
        failures++;
    }

    if (baseline is not null && baseline.TryGetValue(package.Name, out var before))
    {
        if (before.Line - measured.Line > maxDrop || before.Branch - measured.Branch > maxDrop)
        {
            status = $"FAIL (dropped from {before.Line:0.0}/{before.Branch:0.0})";
            failures++;
        }
    }

    Console.WriteLine($"{package.Name,-45} {measured.Line,7:0.0} {lineTarget,7:0.0} {measured.Branch,7:0.0} {branchTarget,7:0.0}  {status}");
}

Console.WriteLine(failures == 0 ? "Coverage gate passed." : $"Coverage gate failed: {failures} package(s).");
return failures == 0 ? 0 : 1;

static Dictionary<string, (double Line, double Branch)> ReadCobertura(string path)
{
    XDocument document = XDocument.Load(path);
    var result = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
    foreach (XElement package in document.Descendants("package"))
    {
        string name = (string?)package.Attribute("name") ?? string.Empty;
        double line = Rate(package, "line-rate");
        double branch = Rate(package, "branch-rate");
        result[name] = (line * 100, branch * 100);
    }

    return result;
}

static double Rate(XElement element, string attribute)
{
    string? value = (string?)element.Attribute(attribute);
    return value is null ? 0 : double.Parse(value, CultureInfo.InvariantCulture);
}

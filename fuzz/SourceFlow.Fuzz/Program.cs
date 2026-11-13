using System.Globalization;
using SourceFlow.Fuzz;

// SourceFlow fuzzing (doc 10 §7, WP-5.5).
//   SourceFlow.Fuzz list                                      the targets
//   SourceFlow.Fuzz corpus <dir>                              write the seed corpus and dictionaries (<dir>/<target>/, <target>.dict)
//   SourceFlow.Fuzz mutate <target> [seconds] [seed]          in-process mutation fuzzing (no instrumentation needed)
//   SourceFlow.Fuzz <target>                                  libFuzzer mode: run under libfuzzer-dotnet after 'sharpfuzz' instrumented
//                                                             the SourceFlow assemblies (see fuzz/README.md)
// libfuzzer-dotnet passes only the assembly path (--target_arg), so libFuzzer mode also takes the target from
// SOURCEFLOW_FUZZ_TARGET when there are no arguments.
if (args.Length == 0 && Environment.GetEnvironmentVariable("SOURCEFLOW_FUZZ_TARGET") is { Length: > 0 } fromEnvironment)
{
    args = [fromEnvironment];
}

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("usage: SourceFlow.Fuzz list | corpus <dir> | mutate <target> [seconds] [seed] | <target>");
    return 2;
}

switch (args[0])
{
    case "list":
        foreach ((string name, FuzzTarget described) in FuzzTargets.All)
        {
            Console.WriteLine($"{name,-24} {described.Description}");
        }

        return 0;

    case "corpus":
        string root = args.Length > 1 ? args[1] : "corpus";
        foreach (string name in FuzzTargets.All.Keys)
        {
            string directory = Path.Combine(root, name);
            Directory.CreateDirectory(directory);
            IReadOnlyList<byte[]> seeds = FuzzTargets.Seeds(name);
            for (int i = 0; i < seeds.Count; i++)
            {
                File.WriteAllBytes(Path.Combine(directory, "seed-" + i.ToString("D2", CultureInfo.InvariantCulture)), seeds[i]);
            }

            File.WriteAllLines(Path.Combine(root, name + ".dict"), FuzzTargets.Dictionary(name).Select(t => "\"" + Escape(t) + "\""));
        }

        Console.WriteLine($"Wrote the corpus of {FuzzTargets.All.Count} targets to {Path.GetFullPath(root)}.");
        return 0;

    case "mutate":
        string mutated = args.Length > 1 ? args[1] : throw new ArgumentException("mutate needs a target.");
        double seconds = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 60;
        int seed = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : Environment.TickCount;
        var mutator = new Mutator(mutated, seed);
        FuzzFinding? finding = mutator.Run(long.MaxValue, TimeSpan.FromSeconds(seconds));
        if (finding is not null)
        {
            Directory.CreateDirectory("findings");
            string file = Path.Combine("findings", finding.Name);
            File.WriteAllBytes(file, finding.Input);
            Console.Error.WriteLine(finding);
            Console.Error.WriteLine("Saved " + Path.GetFullPath(file));
            return 1;
        }

        Console.WriteLine($"{mutated}: {mutator.Iterations:N0} inputs in {seconds:N0} s (seed {seed}), no findings.");
        return 0;

    default:
        string target = args[0];
        if (!FuzzTargets.All.ContainsKey(target))
        {
            Console.Error.WriteLine($"Unknown target '{target}'. Run 'list'.");
            return 2;
        }

        FuzzTargets.Seeds(target);   // build the shared state before libFuzzer starts timing
        SharpFuzz.Fuzzer.LibFuzzer.Run(span => FuzzTargets.Execute(target, span));
        return 0;
}

static string Escape(string token)
{
    var text = new System.Text.StringBuilder();
    foreach (byte b in System.Text.Encoding.UTF8.GetBytes(token))
    {
        text.Append(b is >= 0x20 and < 0x7F and not (byte)'"' and not (byte)'\\' ? ((char)b).ToString() : "\\x" + b.ToString("X2", CultureInfo.InvariantCulture));
    }

    return text.ToString();
}

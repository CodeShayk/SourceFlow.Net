using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace SourceFlow.Fuzz;

/// <summary>
/// A coverage-blind, in-process mutation fuzzer: seeds plus random byte, range, splice and dictionary-token mutations.
/// The unit tests and quick local runs use it. Coverage-guided fuzzing is the libFuzzer mode (SharpFuzz), which the
/// weekly workflow runs with the 2-hour budget per target.
/// </summary>
public sealed class Mutator
{
    private const int MaxLength = 64 * 1024;
    private const int PoolSize = 256;

    private readonly string _target;
    private readonly Random _random;
    private readonly List<byte[]> _pool;
    private readonly byte[][] _tokens;

    /// <summary>Creates a mutator for a target.</summary>
    /// <param name="target">The target name.</param>
    /// <param name="seed">The random seed, so a run can be repeated.</param>
    public Mutator(string target, int seed)
    {
        _target = target;
        _random = new Random(seed);
        _pool = [.. FuzzTargets.Seeds(target)];
        _tokens = [.. FuzzTargets.Dictionary(target).Select(Encoding.UTF8.GetBytes)];
    }

    /// <summary>Runs inputs until <paramref name="iterations"/> or <paramref name="duration"/> is reached.</summary>
    /// <returns>The first finding, or null.</returns>
    public FuzzFinding? Run(long iterations, TimeSpan duration)
    {
        foreach (byte[] seed in _pool.ToArray())
        {
            if (Try(seed) is { } finding)
            {
                return finding;
            }
        }

        long started = Stopwatch.GetTimestamp();
        for (long i = 0; i < iterations && Stopwatch.GetElapsedTime(started) < duration; i++)
        {
            byte[] input = Mutate(_pool[_random.Next(_pool.Count)]);
            if (Try(input) is { } finding)
            {
                return finding;
            }

            // Keep some mutants, so mutations accumulate.
            if (_random.Next(8) == 0)
            {
                _pool[_pool.Count < PoolSize ? _pool.Count - 1 : _random.Next(_pool.Count)] = input;
                if (_pool.Count < PoolSize)
                {
                    _pool.Add(input);
                }
            }

            Iterations = i + 1;
        }

        return null;
    }

    /// <summary>Inputs run after the seeds.</summary>
    public long Iterations { get; private set; }

    private FuzzFinding? Try(byte[] input)
    {
        try
        {
            FuzzTargets.Execute(_target, input);
            return null;
        }
        catch (Exception e)
        {
            return new FuzzFinding(_target, input, e);
        }
    }

    private byte[] Mutate(byte[] source)
    {
        var data = new List<byte>(source);
        int count = 1 + _random.Next(4);
        for (int m = 0; m < count; m++)
        {
            switch (_random.Next(9))
            {
                case 0 when data.Count > 0:
                    int bit = _random.Next(data.Count);
                    data[bit] ^= (byte)(1 << _random.Next(8));
                    break;
                case 1 when data.Count > 0:
                    data[_random.Next(data.Count)] = Interesting();
                    break;
                case 2:
                    data.Insert(_random.Next(data.Count + 1), Interesting());
                    break;
                case 3 when data.Count > 1:
                    int from = _random.Next(data.Count);
                    data.RemoveRange(from, Math.Min(data.Count - from, 1 + _random.Next(16)));
                    break;
                case 4 when data.Count > 1:
                    int start = _random.Next(data.Count);
                    int length = Math.Min(data.Count - start, 1 + _random.Next(32));
                    data.InsertRange(_random.Next(data.Count + 1), data.GetRange(start, length));
                    break;
                case 5 or 6 when _tokens.Length > 0:
                    data.InsertRange(_random.Next(data.Count + 1), _tokens[_random.Next(_tokens.Length)]);
                    break;
                case 7:
                    byte[] other = _pool[_random.Next(_pool.Count)];
                    int cut = _random.Next(data.Count + 1);
                    int otherCut = _random.Next(other.Length + 1);
                    data.RemoveRange(cut, data.Count - cut);
                    data.AddRange(other.AsSpan(otherCut).ToArray());
                    break;
                default:
                    if (data.Count > 0)
                    {
                        // Repeat a byte: long runs find quadratic behaviour and length limits.
                        int at = _random.Next(data.Count);
                        data.InsertRange(at, Enumerable.Repeat(data[at], 1 + _random.Next(512)));
                    }

                    break;
            }
        }

        return data.Count > MaxLength ? [.. data.Take(MaxLength)] : [.. data];
    }

    private byte Interesting()
    {
        ReadOnlySpan<byte> interesting = [0x00, 0x01, 0x0A, 0x0D, 0x20, 0x22, 0x27, 0x2C, 0x3A, 0x3D, 0x5B, 0x5C, 0x5D, 0x7B, 0x7D, 0x7F, 0x80, 0xC0, 0xE2, 0xED, 0xF0, 0xFF];
        return _random.Next(3) == 0 ? (byte)_random.Next(256) : interesting[_random.Next(interesting.Length)];
    }
}

/// <summary>An input that broke a target's contract.</summary>
/// <param name="Target">The target.</param>
/// <param name="Input">The input.</param>
/// <param name="Exception">What it threw.</param>
public sealed record FuzzFinding(string Target, byte[] Input, Exception Exception)
{
    /// <summary>A stable name for the input file.</summary>
    public string Name => Target + "-" + Convert.ToHexString(SHA256.HashData(Input))[..16].ToLowerInvariant();

    /// <inheritdoc/>
    public override string ToString()
    {
        return $"{Target}: {Exception.GetType().FullName}: {Exception.Message}{Environment.NewLine}input (base64): {Convert.ToBase64String(Input)}{Environment.NewLine}{Exception.StackTrace}";
    }
}

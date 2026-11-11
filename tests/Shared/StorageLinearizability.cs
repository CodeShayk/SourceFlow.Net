using System.Text;
using System.Text.Json;
using CsCheck;
using SourceFlow.Messaging;
using SourceFlow.Storage;
using SourceFlow.Testing.Conformance;

namespace SourceFlow.Tests.Shared;

/// <summary>
/// CsCheck linearisability of appends and the outbox (WP-2.9, plan 06 §2). Parallel threads run a random mix of
/// <c>Any</c> appends, <c>NoStream</c> appends (which fail on an existing stream), and appends that publish an outbox
/// row. The resulting streams and outbox must equal the model after some sequential ordering of the same operations.
/// That checks the compare-and-set and that an append and its outbox row commit or fail together.
/// </summary>
internal static class StorageLinearizability
{
    private const string Tenant = MessageEnvelope.DefaultTenant;
    private static readonly string[] Keys = ["k1", "k2"];

    public static void AppendsAndOutbox(Func<IStorageFixture> create, int iterations, int threads)
    {
        Gen<(Actual, Model)> initial = Gen.Const(0).Select(_ => (new Actual(create), new Model()));
        Gen<(int Stream, int Payload, int Key)> args = Gen.Select(Gen.Int[0, 2], Gen.Int[1, 1_000_000], Gen.Int[0, 1]);

        GenOperation<Actual, Model> any = GenOperation.Create<Actual, Model, (int Stream, int Payload, int Key)>(
            args, a => $"Any(s{a.Stream}, {a.Payload})",
            (actual, a) => actual.Append(a.Stream, ExpectedVersion.Any, a.Payload, key: null),
            (model, a) => model.Append(a.Stream, noStream: false, a.Payload, key: null));
        GenOperation<Actual, Model> noStream = GenOperation.Create<Actual, Model, (int Stream, int Payload, int Key)>(
            args, a => $"NoStream(s{a.Stream}, {a.Payload})",
            (actual, a) => actual.Append(a.Stream, ExpectedVersion.NoStream, a.Payload, key: null),
            (model, a) => model.Append(a.Stream, noStream: true, a.Payload, key: null));
        GenOperation<Actual, Model> publish = GenOperation.Create<Actual, Model, (int Stream, int Payload, int Key)>(
            args, a => $"Publish(s{a.Stream}, {a.Payload}, {Keys[a.Key]})",
            (actual, a) => actual.Append(a.Stream, ExpectedVersion.NoStream, a.Payload, Keys[a.Key]),
            (model, a) => model.Append(a.Stream, noStream: true, a.Payload, Keys[a.Key]));

        CsCheck.Check.SampleParallel(initial, [any, noStream, publish], (actual, model) => actual.Final.Equals(model.Final),
            iter: iterations, threads: threads, printActual: a => a.Final.ToString(), printModel: m => m.Final.ToString());
    }

    /// <summary>The final observable state: stream payloads in version order and outbox payloads per key in order.</summary>
    private sealed record State(SortedDictionary<int, List<int>> Streams, SortedDictionary<string, List<int>> Outbox)
    {
        public bool Equals(State? other)
        {
            return other is not null && Same(Streams, other.Streams) && Same(Outbox, other.Outbox);
        }

        public override int GetHashCode()
        {
            return Streams.Count;
        }

        public override string ToString()
        {
            return string.Join("; ", Streams.Select(s => $"s{s.Key}=[{string.Join(",", s.Value)}]"))
                + " | " + string.Join("; ", Outbox.Select(o => $"{o.Key}=[{string.Join(",", o.Value)}]"));
        }

        private static bool Same<TKey>(SortedDictionary<TKey, List<int>> a, SortedDictionary<TKey, List<int>> b)
            where TKey : notnull
        {
            return a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out List<int>? other) && pair.Value.SequenceEqual(other));
        }
    }

    private sealed class Model
    {
        private readonly SortedDictionary<int, List<int>> _streams = [];
        private readonly SortedDictionary<string, List<int>> _outbox = new(StringComparer.Ordinal);

        public State Final => new(_streams, _outbox);

        public void Append(int stream, bool noStream, int payload, string? key)
        {
            if (noStream && _streams.ContainsKey(stream))
            {
                return;
            }

            (_streams.TryGetValue(stream, out List<int>? list) ? list : _streams[stream] = []).Add(payload);
            if (key is not null)
            {
                (_outbox.TryGetValue(key, out List<int>? rows) ? rows : _outbox[key] = []).Add(payload);
            }
        }
    }

    /// <summary>The store under test, created on first use so CsCheck's model-only replays never touch a database.</summary>
    private sealed class Actual
    {
        private readonly Lazy<IStorageFixture> _fixture;
        private readonly string _prefix = "lin" + Guid.CreateVersion7().ToString("N")[..8];
        private readonly Lazy<State> _final;

        // CsCheck compares against orderings on parallel threads; draining the outbox is destructive, so read once.
        public State Final => _final.Value;

        public Actual(Func<IStorageFixture> create)
        {
            _fixture = new Lazy<IStorageFixture>(create, LazyThreadSafetyMode.ExecutionAndPublication);
            _final = new Lazy<State>(Read, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public void Append(int stream, ExpectedVersion expected, int payload, string? key)
        {
            try
            {
                AppendAsync(stream, expected, payload, key).GetAwaiter().GetResult();
            }
            catch (ConcurrencyException)
            {
                // NoStream on an existing stream: nothing is written.
            }
        }

        private async Task AppendAsync(int stream, ExpectedVersion expected, int payload, string? key)
        {
            await using IStorageSession session = await _fixture.Value.Sessions.BeginAsync(Tenant, CancellationToken.None);
            session.Append(Stream(stream), "lin", expected, [StorageConformance.Event(data: $"{{\"n\":{payload}}}")]);
            if (key is not null)
            {
                var envelope = new MessageEnvelope
                {
                    MessageId = new MessageId(Guid.CreateVersion7()),
                    Type = "lin.published",
                    TypeVersion = 1,
                    Source = "lin",
                    Time = DateTimeOffset.UnixEpoch,
                    PartitionKey = key,
                    Body = Encoding.UTF8.GetBytes($"{{\"n\":{payload}}}"),
                };
                session.EnqueueOutbox(new OutboxMessage(envelope, "dest", OutboxKind.Publish, DateTimeOffset.MinValue, 0));
            }

            await session.CommitAsync(CancellationToken.None);
        }

        private State Read()
        {
            return ReadAsync().GetAwaiter().GetResult();
        }

        private async Task<State> ReadAsync()
        {
            IStorageFixture f = _fixture.Value;
            var streams = new SortedDictionary<int, List<int>>();
            for (int s = 0; s < 3; s++)
            {
                StreamSlice slice = await f.Events.ReadStreamAsync(Tenant, Stream(s), new StreamVersion(1), 1000, CancellationToken.None);
                if (slice.Events.Count > 0)
                {
                    streams[s] = [.. slice.Events.Select(e => Payload(e.Data))];
                }
            }

            // Drain the outbox through the relay contract: only heads are claimable, so this also checks per-key order.
            var outbox = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
            LeaseToken lease = (await f.Leases.TryAcquireAsync("outbox:0", "lin-check", TimeSpan.FromMinutes(5), CancellationToken.None))!;
            while (true)
            {
                IReadOnlyList<OutboxRecord> claimed = await f.Outbox.ClaimAsync(0, lease, 100, CancellationToken.None);
                if (claimed.Count == 0)
                {
                    break;
                }

                foreach (OutboxRecord record in claimed)
                {
                    string key = record.Message.Envelope.PartitionKey!;
                    (outbox.TryGetValue(key, out List<int>? rows) ? rows : outbox[key] = []).Add(Payload(record.Message.Envelope.Body));
                }

                await f.Outbox.CompleteAsync(lease, [.. claimed.Select(r => r.Sequence)], CancellationToken.None);
            }

            // The final state is read once; release the store's connections now.
            await f.DisposeAsync();
            return new State(streams, outbox);
        }

        private StreamId Stream(int index)
        {
            return new StreamId($"{_prefix}-{index}");
        }

        private static int Payload(ReadOnlyMemory<byte> data)
        {
            return JsonDocument.Parse(data).RootElement.GetProperty("n").GetInt32();
        }
    }
}

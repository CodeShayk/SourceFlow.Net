using System.Text.Json;
using SourceFlow.Core;
using SourceFlow.Core.Tests.Events;

namespace SourceFlow.Core.Tests.Impl
{
    public class InMemoryEventStore : IEventStore
    {
        private readonly Dictionary<string, List<EventStoreEntry>> _streams = new Dictionary<string, List<EventStoreEntry>>();
        private long _globalSequenceNumber = 0;

        public Task SaveEventsAsync(string streamId, IEnumerable<IEvent> events, int expectedVersion)
        {
            lock (_streams)
            {
                if (!_streams.ContainsKey(streamId))
                    _streams[streamId] = new List<EventStoreEntry>();

                var stream = _streams[streamId];

                // Optimistic concurrency check
                if (stream.Count != expectedVersion)
                    throw new InvalidOperationException($"Concurrency conflict. Expected version {expectedVersion}, but stream has {stream.Count} events.");

                foreach (var @event in events)
                {
                    var entry = new EventStoreEntry
                    {
                        EventId = @event.EventId,
                        StreamId = streamId,
                        EventType = @event.EventType,
                        EventData = JsonSerializer.Serialize(@event, @event.GetType()),
                        Timestamp = @event.Timestamp,
                        Version = @event.Version,
                        SequenceNumber = ++_globalSequenceNumber
                    };

                    stream.Add(entry);
                }
            }

            return Task.CompletedTask;
        }

        public Task<IEnumerable<IEvent>> GetEventsAsync(string streamId)
        {
            return GetEventsAsync(streamId, 0);
        }

        public Task<IEnumerable<IEvent>> GetEventsAsync(string streamId, int fromVersion)
        {
            lock (_streams)
            {
                if (!_streams.ContainsKey(streamId))
                    return Task.FromResult(Enumerable.Empty<IEvent>());

                var events = _streams[streamId]
                    .Skip(fromVersion)
                    .Select(DeserializeEvent)
                    .Where(e => e != null)
                    .Cast<IEvent>()
                    .ToList();

                return Task.FromResult<IEnumerable<IEvent>>(events);
            }
        }

        private IEvent DeserializeEvent(EventStoreEntry entry)
        {
            // Simple type mapping - in production, use a proper type resolver
            Type eventType;
            switch (entry.EventType)
            {
                case "BankAccountCreated":
                    eventType = typeof(BankAccountCreated);
                    break;

                case "MoneyDeposited":
                    eventType = typeof(MoneyDeposited);
                    break;

                case "MoneyWithdrawn":
                    eventType = typeof(MoneyWithdrawn);
                    break;

                case "AccountClosed":
                    eventType = typeof(AccountClosed);
                    break;

                default:
                    eventType = null;
                    break;
            }

            if (eventType == null)
                return null;

            return JsonSerializer.Deserialize(entry.EventData, eventType) as IEvent;
        }
    }
}
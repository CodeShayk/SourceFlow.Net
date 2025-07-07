// ====================================================================================
// CORE EVENT SOURCING ABSTRACTIONS
// ====================================================================================

using System.Collections.Generic;

namespace SourceFlow.Core
{
    public abstract class AggregateRoot
    {
        private readonly List<IEvent> _uncommittedEvents = new List<IEvent>();

        public string Id { get; protected set; } = string.Empty;
        public int Version { get; protected set; }

        public IReadOnlyList<IEvent> UncommittedEvents => _uncommittedEvents.AsReadOnly();

        protected void RaiseEvent(IEvent @event)
        {
            _uncommittedEvents.Add(@event);
            ApplyEvent(@event);
            Version++;
        }

        public void MarkEventsAsCommitted()
        {
            _uncommittedEvents.Clear();
        }

        public void LoadFromHistory(IEnumerable<IEvent> events)
        {
            foreach (var @event in events)
            {
                ApplyEvent(@event);
                Version++;
            }
        }

        protected abstract void ApplyEvent(IEvent @event);
    }
}
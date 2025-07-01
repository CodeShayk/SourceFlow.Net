using System;

namespace SourceFlow
{
    public class BaseEvent : IEvent
    {
        public BaseEvent(Guid aggregateId)
        {
            EventId = Guid.NewGuid();
            OccurredOn = DateTime.UtcNow;
            AggregateId = aggregateId;
        }

        public Guid EventId { get; }
        public Guid AggregateId { get; }
        public DateTime OccurredOn { get; }
        bool IEvent.IsReplay { get; set; }
        int IEvent.SequenceNo { get; set; }
    }
}
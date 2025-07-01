using System;

namespace SourceFlow
{
    public interface IEvent
    {
        Guid EventId { get; }
        Guid AggregateId { get; }
        bool IsReplay { get; set; }

        //IAggregateRoot Source { get; set; }
        DateTime OccurredOn { get; }

        int SequenceNo { get; set; }
    }
}
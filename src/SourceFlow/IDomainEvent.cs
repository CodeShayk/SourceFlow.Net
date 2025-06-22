using System;

namespace SourceFlow
{
    public interface IDomainEvent
    {
        Guid EventId { get; }
        IAggregateRoot Source { get; set; }
        DateTime OccurredOn { get; }
        int SequenceNo { get; }
    }

    public class AggregateReference
    {
        public AggregateReference(int id, Type type)
        {
            Id = id;
            Type = type ?? throw new ArgumentNullException(nameof(type));
        }

        public int Id { get; }
        public Type Type { get; }
    }
}
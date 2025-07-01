using System;

namespace SourceFlow
{
    public class AggregateReference
    {
        public AggregateReference(Guid id, Type type)
        {
            Id = id;
            Type = type ?? throw new ArgumentNullException(nameof(type));
        }

        public Guid Id { get; }
        public Type Type { get; }
    }
}
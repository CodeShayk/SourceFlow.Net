using System;

namespace SourceFlow
{
    public interface IEvent
    {
        string Name { get; }
    }

    public interface IEvent<TEntity> : IEvent
        where TEntity : class, IEntity
    {
        TEntity Payload { get; }
    }
}
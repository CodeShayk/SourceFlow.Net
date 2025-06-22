using System.Threading.Tasks;

namespace SourceFlow
{
    public interface ISaga<TAggregateRoot> : ISaga
        where TAggregateRoot : IAggregateRoot
    {
        Task Replay(TAggregateRoot aggregateRoot);
    }

    public interface ISaga
    {
        Task<bool> CanHandleEvent(IDomainEvent @event);

        Task HandleAsync(IDomainEvent @event);
    }
}
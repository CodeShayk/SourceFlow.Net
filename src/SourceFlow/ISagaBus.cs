using System.Threading.Tasks;

namespace SourceFlow
{
    public interface ISagaBus
    {
        Task PublishAsync<TEvent>(TEvent @event)
              where TEvent : IDomainEvent;

        void RegisterSaga<TAggregateRoot>(ISaga<TAggregateRoot> saga) where TAggregateRoot : IAggregateRoot;

        Task Replay<TAggregateRoot>(TAggregateRoot baseAggregateRoot) where TAggregateRoot : IAggregateRoot;
    }
}
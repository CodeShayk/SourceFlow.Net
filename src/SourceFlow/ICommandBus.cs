using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    public interface ICommandBus
    {
        Task ReplayEvents(Guid aggregateId);

        Task PublishAsync<TEvent>(TEvent @event)
             where TEvent : IEvent;

        void RegisterSaga(ISagaHandler saga);
    }
}
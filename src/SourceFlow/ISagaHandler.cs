using System.Threading.Tasks;

namespace SourceFlow
{
    public interface ISagaHandler
    {
        Task<bool> CanHandleEvent<TEvent>(TEvent @event)
            where TEvent : IEvent;

        Task HandleAsync<TEvent>(TEvent @event)
            where TEvent : IEvent;
    }
}
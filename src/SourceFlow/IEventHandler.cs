using System.Threading.Tasks;

namespace SourceFlow
{
    public interface IEventHandler
    {
        // Task HandleAsync(IEvent @event);
    }

    public interface IEventHandler<in TEvent> : IEventHandler
        where TEvent : IEvent
    {
        Task HandleAsync(TEvent @event);
    }
}
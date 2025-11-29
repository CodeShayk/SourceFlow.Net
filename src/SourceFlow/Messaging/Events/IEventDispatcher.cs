using System.Threading.Tasks;

namespace SourceFlow.Messaging.Events
{
    public interface IEventDispatcher
    {
        Task Dispatch<TEvent>(TEvent @event) where TEvent : IEvent;
    }
}

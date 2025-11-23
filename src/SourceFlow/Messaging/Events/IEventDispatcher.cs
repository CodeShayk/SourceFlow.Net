using System.Threading.Tasks;

namespace SourceFlow.Messaging.Events
{
    public interface IEventDispatcher
    {
        Task Dispatch(IEvent @event);
    }
}
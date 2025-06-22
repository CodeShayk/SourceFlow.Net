using System.Threading.Tasks;

namespace SourceFlow
{
    public interface IEventHandler
    {
    }

    public interface IEventHandler<in TEvent> : IEventHandler
        where TEvent : IDomainEvent
    {
        Task HandleAsync(TEvent @event);
    }
}
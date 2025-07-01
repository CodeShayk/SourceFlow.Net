using System.Threading.Tasks;

namespace SourceFlow
{
    public interface IBusPublisher
    {
        Task PublishAsync<TEvent>(TEvent @event)
              where TEvent : IEvent;
    }
}
using System.Threading.Tasks;

namespace SourceFlow
{
    public class BusPublisher : IBusPublisher
    {
        private readonly ICommandBus commandBus;

        public BusPublisher(ICommandBus commandBus)
        {
            this.commandBus = commandBus;
        }

        public async Task PublishAsync<TEvent>(TEvent @event) where TEvent : IEvent
        {
            await commandBus.PublishAsync(@event);
        }
    }
}
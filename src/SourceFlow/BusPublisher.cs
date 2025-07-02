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

        async Task IBusPublisher.PublishAsync<TEvent>(TEvent @event)
        {
            await commandBus.PublishAsync(@event);
        }
    }
}
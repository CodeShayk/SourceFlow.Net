using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Implementation of the IBusPublisher interface for publishing events to subscribers.
    /// </summary>
    public class BusPublisher : IBusPublisher
    {
        /// <summary>
        /// The command bus used to publish events.
        /// </summary>
        private readonly ICommandBus commandBus;

        /// <summary>
        /// Initializes a new instance of the <see cref="BusPublisher"/> class.
        /// </summary>
        /// <param name="commandBus"></param>
        public BusPublisher(ICommandBus commandBus)
        {
            this.commandBus = commandBus;
        }

        /// <summary>
        /// Publishes an event to all subscribers.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        async Task IBusPublisher.PublishAsync<TEvent>(TEvent @event)
        {
            await commandBus.PublishAsync(@event);
        }
    }
}
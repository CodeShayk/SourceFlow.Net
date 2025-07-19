using SourceFlow.Saga;

namespace SourceFlow.Messaging.Bus
{
    /// <summary>
    /// Interface for subscribing sagas to the event bus.
    /// </summary>
    internal interface IBusSubscriber
    {
        /// <summary>
        /// Subscribes a saga to the bus.
        /// </summary>
        /// <param name="saga"></param>
        void Subscribe(ISaga saga);
    }
}
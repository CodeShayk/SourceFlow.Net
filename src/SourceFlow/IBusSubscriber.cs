namespace SourceFlow
{
    /// <summary>
    /// Interface for subscribing sagas to the event bus.
    /// </summary>
    public interface IBusSubscriber
    {
        /// <summary>
        /// Subscribes a saga to the bus.
        /// </summary>
        /// <param name="saga"></param>
        void Subscribe(ISagaHandler saga);
    }
}
namespace SourceFlow
{
    /// <summary>
    /// Implementation of the IBusSubscriber interface for subscribing sagas to the command bus.
    /// </summary>
    internal class BusSubscriber : IBusSubscriber
    {
        /// <summary>
        /// The command bus used to register sagas.
        /// </summary>
        private readonly ICommandBus commandBus;

        /// <summary>
        /// Initializes a new instance of the <see cref="BusSubscriber"/> class.
        /// </summary>
        /// <param name="commandBus"></param>
        public BusSubscriber(ICommandBus commandBus)
        {
            this.commandBus = commandBus;
        }

        /// <summary>
        /// Subscribes a saga to the command bus.
        /// </summary>
        /// <param name="saga"></param>
        void IBusSubscriber.Subscribe(ISaga saga)
        {
            commandBus.RegisterSaga(saga);
        }
    }
}
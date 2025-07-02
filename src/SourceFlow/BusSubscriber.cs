namespace SourceFlow
{
    public class BusSubscriber : IBusSubscriber
    {
        private readonly ICommandBus commandBus;

        public BusSubscriber(ICommandBus commandBus)
        {
            this.commandBus = commandBus;
        }

        void IBusSubscriber.Subscribe(ISagaHandler saga)
        {
            commandBus.RegisterSaga(saga);
        }
    }
}
namespace SourceFlow
{
    public class BusSubscriber : IBusSubscriber
    {
        private readonly ICommandBus commandBus;

        public BusSubscriber(ICommandBus commandBus)
        {
            this.commandBus = commandBus;
        }

        public void Subscribe(ISagaHandler saga)
        {
            commandBus.RegisterSaga(saga);
        }
    }
}
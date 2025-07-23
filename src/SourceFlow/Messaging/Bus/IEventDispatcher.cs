namespace SourceFlow.Messaging.Bus
{
    public interface IEventDispatcher
    {
        void Dispatch(object sender, IEvent @event);
    }
}
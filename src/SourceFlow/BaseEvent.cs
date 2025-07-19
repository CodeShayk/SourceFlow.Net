namespace SourceFlow
{
    public class BaseEvent<T> : IEvent
        where T : IEntity
    {
        public string Name { get; set; }
        public T Payload { get; set; }
        IEntity IEvent.Payload => Payload;

        public BaseEvent(T payload)
        {
            Name = this.GetType().Name;
            Payload = payload;
        }
    }
}
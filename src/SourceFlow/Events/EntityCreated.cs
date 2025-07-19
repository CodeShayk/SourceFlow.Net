namespace SourceFlow.Events
{
    public class EntityCreated<T> : BaseEvent<T>
        where T : class, IEntity
    {
        public EntityCreated(T payload) : base(typeof(T).Name + "Created", payload)
        {
        }
    }
}
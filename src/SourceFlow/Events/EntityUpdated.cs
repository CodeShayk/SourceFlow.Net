namespace SourceFlow.Events
{
    public class EntityUpdated<T> : BaseEvent<T>
        where T : class, IEntity
    {
        public EntityUpdated(T payload) : base(typeof(T).Name + "Updated", payload)
        {
        }
    }
}
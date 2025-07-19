namespace SourceFlow.Events
{
    public class EntityDeleted<T> : BaseEvent<T>
        where T : class, IEntity
    {
        public EntityDeleted(T payload) : base(typeof(T).Name + "Deleted", payload)
        {
        }
    }
}
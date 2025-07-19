namespace SourceFlow
{
    public class BaseEvent<TEntity> : IEvent<TEntity>
        where TEntity : class, IEntity
    {
        public string Name { get; set; }
        public TEntity Payload { get; set; }

        public BaseEvent(string name, TEntity payload)
        {
            Name = name;
            Payload = payload;
        }
    }
}
namespace SourceFlow.ConsoleApp.Events
{
    public abstract class AccountEvent : BaseEvent
    {
        protected AccountEvent(Guid aggregateId) : base(aggregateId)
        {
        }

        public new Guid AggregateId { get; set; }
        public string EventType => this.GetType().Name;
    }
}
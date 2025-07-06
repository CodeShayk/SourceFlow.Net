namespace SourceFlow.ConsoleApp.Events
{
    public abstract class AccountEvent : BaseEvent
    {
        protected AccountEvent(Source source) : base(source)
        {
        }

        public new int AggregateId { get; set; }
        public string EventType => this.GetType().Name;
    }
}
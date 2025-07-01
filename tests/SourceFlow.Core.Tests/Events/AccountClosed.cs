namespace SourceFlow.Core.Tests.Events
{
    public class AccountClosed : BaseEvent
    {
        public override string EventType => nameof(AccountClosed);
        public string AccountId { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }
}
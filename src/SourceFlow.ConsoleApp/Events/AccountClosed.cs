namespace SourceFlow.ConsoleApp.Events
{
    public class AccountClosed : AccountEvent
    {
        public AccountClosed(Guid aggregateId) : base(aggregateId)
        {
        }

        public string Reason { get; set; } = string.Empty;
    }
}
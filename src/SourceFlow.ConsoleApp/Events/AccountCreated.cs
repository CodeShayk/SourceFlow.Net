namespace SourceFlow.ConsoleApp.Events
{
    public class AccountCreated : AccountEvent
    {
        public AccountCreated(Guid aggregateId) : base(aggregateId)
        {
        }

        public string AccountHolderName { get; set; } = string.Empty;
        public decimal InitialBalance { get; set; }
    }
}
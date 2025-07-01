namespace SourceFlow.Core.Tests.Events
{
    public class BankAccountCreated : BaseEvent

    {
        public override string EventType => nameof(BankAccountCreated);
        public string AccountId { get; set; } = string.Empty;
        public string AccountHolderName { get; set; } = string.Empty;
        public decimal InitialBalance { get; set; }
    }
}
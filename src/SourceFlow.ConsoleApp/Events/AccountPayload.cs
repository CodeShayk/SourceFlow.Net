namespace SourceFlow.ConsoleApp.Events
{
    public class AccountPayload : IEventPayload
    {
        public decimal InitialAmount { get; set; }
        public string AccountName { get; set; }
    }

    public class TransactPayload : IEventPayload
    {
        public decimal Amount { get; set; }
    }

    public class ClosurePayload : IEventPayload
    {
        public bool IsClosed { get; set; }
        public string ClosureReason { get; set; } = string.Empty;
    }
}
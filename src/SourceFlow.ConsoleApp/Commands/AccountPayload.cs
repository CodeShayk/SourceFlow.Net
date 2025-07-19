using SourceFlow.ConsoleApp.Aggregates;

namespace SourceFlow.ConsoleApp.Commands
{
    public class AccountPayload : IPayload
    {
        public decimal InitialAmount { get; set; }
        public string AccountName { get; set; }
    }

    public class TransactPayload : IPayload
    {
        public TransactionType Type { get; set; }
        public decimal Amount { get; set; }
        public decimal CurrentBalance { get; set; }
    }

    public class ClosurePayload : IPayload
    {
        public bool IsClosed { get; set; }
        public string ClosureReason { get; set; } = string.Empty;
    }
}
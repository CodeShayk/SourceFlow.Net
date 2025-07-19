using SourceFlow.ConsoleApp.Aggregates;
using SourceFlow.Messaging;

namespace SourceFlow.ConsoleApp.Commands
{
    public class ActivationPayload : IPayload
    {
        public int Id { get; set; }
        public DateTime ActiveOn { get; set; }
    }

    public class Payload : IPayload
    {
        public int Id { get; set; }
        public decimal InitialAmount { get; set; }
        public string AccountName { get; set; }
    }

    public class TransactPayload : IPayload
    {
        public int Id { get; set; }
        public TransactionType Type { get; set; }
        public decimal Amount { get; set; }
        public decimal CurrentBalance { get; set; }
    }

    public class ClosurePayload : IPayload
    {
        public int Id { get; set; }
        public bool IsClosed { get; set; }
        public string ClosureReason { get; set; } = string.Empty;
    }
}
using SourceFlow.Core.Tests.E2E.Aggregates;
using SourceFlow.Messaging;

namespace SourceFlow.Core.Tests.E2E.Commands
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
        public string AccountName { get; set; } = string.Empty;
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

using SourceFlow.Core;

namespace SourceFlow.Core.Tests.Events
{
    public class MoneyWithdrawn : BaseEvent
    {
        public override string EventType => nameof(MoneyWithdrawn);
        public string AccountId { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public decimal NewBalance { get; set; }
    }
}
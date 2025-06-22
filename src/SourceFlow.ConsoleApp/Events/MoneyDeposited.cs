using SourceFlow.Core;

namespace SourceFlow.ConsoleApp.Events
{
    public class MoneyDeposited : BaseEvent

    {
        public override string EventType => nameof(MoneyDeposited);
        public string AccountId { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public decimal NewBalance { get; set; }
    }
}
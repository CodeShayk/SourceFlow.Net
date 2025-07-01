namespace SourceFlow.ConsoleApp.Events
{
    public class MoneyDeposited : AccountEvent
    {
        public MoneyDeposited(Guid aggregateId) : base(aggregateId)
        {
        }

        public decimal Amount { get; set; }
        public decimal NewBalance { get; set; }
    }
}
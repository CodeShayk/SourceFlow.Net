namespace SourceFlow.ConsoleApp.Events
{
    public class MoneyWithdrawn : AccountEvent
    {
        public MoneyWithdrawn(Guid aggregateId) : base(aggregateId)
        {
        }

        public decimal Amount { get; set; }
        public decimal NewBalance { get; set; }
    }
}
namespace SourceFlow.ConsoleApp.Events
{
    public class MoneyWithdrawn : AccountEvent
    {
        public MoneyWithdrawn(Source source) : base(source)
        {
        }

        public decimal Amount { get; set; }
        public decimal NewBalance { get; set; }
    }
}
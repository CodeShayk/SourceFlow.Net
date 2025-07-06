namespace SourceFlow.ConsoleApp.Events
{
    public class MoneyDeposited : AccountEvent
    {
        public MoneyDeposited(Source source) : base(source)
        {
        }

        public decimal Amount { get; set; }
        public decimal NewBalance { get; set; }
    }
}
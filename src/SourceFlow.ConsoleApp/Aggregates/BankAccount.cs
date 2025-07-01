namespace SourceFlow.ConsoleApp.Aggregates
{
    public class BankAccount : IIdentity
    {
        public Guid Id { get; set; }
        public string AccountHolderName { get; set; } = string.Empty;
        public decimal Balance { get; set; }
        public bool IsClosed { get; set; }
    }
}
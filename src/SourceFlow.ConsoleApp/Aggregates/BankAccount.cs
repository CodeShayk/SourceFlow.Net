namespace SourceFlow.ConsoleApp.Aggregates
{
    public class BankAccount : IEntity
    {
        public int Id { get; set; }
        public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
        public string AccountName { get; set; } = string.Empty;
        public decimal Balance { get; set; }
        public bool IsClosed { get; set; }
        public string ClosureReason { get; internal set; }
    }
}
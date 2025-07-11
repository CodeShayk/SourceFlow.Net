namespace SourceFlow.Core.Tests.E2E.Aggregates
{
    public class BankAccount : IEntity
    {
        public int Id { get; set; }
        public string AccountName { get; set; } = string.Empty;
        public decimal Balance { get; set; }
        public bool IsClosed { get; set; }
        public string ClosureReason { get; internal set; }
    }
}
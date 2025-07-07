namespace SourceFlow.Core.Tests.Projections
{
    // ====================================================================================
    // PROJECTION / READ MODEL EXAMPLE
    // ====================================================================================

    public class AccountSummaryProjection
    {
        public string AccountId { get; set; } = string.Empty;
        public string AccountHolderName { get; set; } = string.Empty;
        public decimal CurrentBalance { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime LastUpdated { get; set; }
        public int TransactionCount { get; set; }
        public bool IsClosed { get; set; }
        public string ClosureReason { get; set; }
    }
}
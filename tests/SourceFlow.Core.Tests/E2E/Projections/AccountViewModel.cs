using SourceFlow.Projections;

namespace SourceFlow.Core.Tests.E2E.Projections
{
    public class AccountViewModel : IViewModel
    {
        public int Id { get; set; }
        public string AccountName { get; set; } = string.Empty;
        public decimal CurrentBalance { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime LastUpdated { get; set; }
        public int TransactionCount { get; set; }
        public bool IsClosed { get; set; }
        public string ClosureReason { get; set; }
        public int Version { get; set; }
        public DateTime ActiveOn { get; set; }
    }
}
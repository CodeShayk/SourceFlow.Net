using SourceFlow.Projections;

namespace SourceFlow.Net.SQL.Tests.TestModels
{
    /// <summary>
    /// Test view model for integration tests.
    /// </summary>
    public class TestViewModel : IViewModel
    {
        public int Id { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int Count { get; set; }
    }
}

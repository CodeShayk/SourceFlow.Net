namespace SourceFlow.Net.SQL.Tests.TestModels
{
    /// <summary>
    /// Test entity for integration tests.
    /// </summary>
    public class TestEntity : IEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int Value { get; set; }
    }
}

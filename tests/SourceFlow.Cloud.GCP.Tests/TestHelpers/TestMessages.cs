using SourceFlow;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Cloud.GCP.Tests.TestHelpers;

/// <summary>Test categories used as xUnit traits for filtering.</summary>
public static class TestCategories
{
    public const string Unit = "Unit";
    public const string Integration = "Integration";
}

public class TestPayload : IPayload
{
    public string Data { get; set; } = string.Empty;
    public int Value { get; set; }
}

public class TestEntity : IEntity
{
    public int Id { get; set; }
}

public class TestCommand : ICommand
{
    public string Name { get; set; } = "TestCommand";
    public IPayload Payload { get; set; } = new TestPayload();
    public EntityRef Entity { get; set; } = new EntityRef { Id = 1 };
    public Metadata Metadata { get; set; } = new Metadata();
}

public class TestEvent : IEvent
{
    public string Name { get; set; } = "TestEvent";
    public IEntity Payload { get; set; } = new TestEntity { Id = 1 };
    public Metadata Metadata { get; set; } = new Metadata();
}

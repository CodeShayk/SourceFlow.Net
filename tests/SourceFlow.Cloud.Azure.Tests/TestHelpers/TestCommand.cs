using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Cloud.Azure.Tests.TestHelpers;

public class TestCommand : ICommand
{
    public IPayload Payload { get; set; } = new TestPayload();
    public EntityRef Entity { get; set; } = new EntityRef { Id = 1 };
    public string Name { get; set; } = string.Empty;
    public Metadata Metadata { get; set; } = new Metadata();
}

public class TestPayload : IPayload
{
    public string Data { get; set; } = string.Empty;
    public int Value { get; set; }
}

public class TestEvent : IEvent
{
    public string Name { get; set; } = null!;
    public IEntity Payload { get; set; } = null!;
    public Metadata Metadata { get; set; } = null!;
}

public class TestEntity : IEntity
{
    public int Id { get; set; }
}

public class TestCommandMetadata : Metadata
{
    public TestCommandMetadata()
    {
    }
}

public class TestEventMetadata : Metadata
{
    public TestEventMetadata()
    {
    }
}

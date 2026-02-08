using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Cloud.Azure.Tests.TestHelpers;

public class TestCommand : ICommand
{
    public IPayload Payload { get; set; } = null!;
    public EntityRef Entity { get; set; } = null!;
    public string Name { get; set; } = null!;
    public Metadata Metadata { get; set; } = null!;
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
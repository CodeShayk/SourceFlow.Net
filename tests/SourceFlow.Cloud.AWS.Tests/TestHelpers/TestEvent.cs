using SourceFlow;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Cloud.AWS.Tests.TestHelpers;

public class TestEvent : Event<TestEventData>
{
    public TestEvent() : base(new TestEventData { Id = 1 })
    {
    }

    public TestEvent(TestEventData data) : base(data)
    {
    }
}

public class TestEventData : IEntity
{
    public int Id { get; set; }
    public string Message { get; set; } = "";
    public int Value { get; set; }
}

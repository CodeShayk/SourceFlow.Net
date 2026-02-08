using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;

namespace SourceFlow.Cloud.AWS.Tests.TestHelpers;

public class TestCommand : Command<TestCommandData>
{
}

public class TestCommandData : IPayload
{
    public string Message { get; set; } = "";
    public int Value { get; set; }
}
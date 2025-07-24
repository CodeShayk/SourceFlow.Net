using SourceFlow.Messaging;

namespace SourceFlow.Core.Tests.Impl
{
    public class DummyCommand : ICommand
    {
        public DummyCommand()
        {
            Payload = new DummyPayload();
            Name = "DummyCommand";
            Metadata = new Metadata();
        }

        public IPayload Payload { get; set; }
        public string Name { get; set; }
        public Metadata Metadata { get; set; }
    }

    internal class DummyPayload : IPayload
    {
        public int Id { get; set; }
    }
}
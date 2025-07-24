using SourceFlow.Messaging;

namespace SourceFlow.Core.Tests.Ioc
{
    public class DummyCommandStore : ICommandStore
    {
        public Task Append(ICommand command)
        {
            // Simulate appending command
            return Task.CompletedTask;
        }
        public Task<IEnumerable<ICommand>> Load(int aggregateId)
        {
            // Simulate loading commands
            return Task.FromResult<IEnumerable<ICommand>>(null);
        }
        public Task<int> GetNextSequenceNo(int aggregateId)
        {
            // Simulate getting next sequence number
            return Task.FromResult(1);
        }
    }
}
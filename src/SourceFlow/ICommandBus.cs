using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    public interface ICommandBus : IBusPublisher
    {
        Task Replay(Guid aggregateId);
    }
}
using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    public interface IEventReplayer
    {
        Task ReplayEventsAsync(Guid aggregateId);
    }
}
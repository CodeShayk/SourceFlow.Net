using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    public interface IBusReplayer
    {
        Task ReplayEventsAsync(Guid aggregateId);
    }
}
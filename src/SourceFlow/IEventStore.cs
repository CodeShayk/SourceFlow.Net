using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceFlow
{
    public interface IEventStore
    {
        Task AppendAsync(IEvent @event);

        Task<IEnumerable<IEvent>> LoadAsync(Guid aggregateId);

        Task<int> GetNextSequenceNo(Guid aggregateId);
    }
}
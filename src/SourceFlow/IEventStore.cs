using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceFlow
{
    public interface IEventStore
    {
        Task AppendAsync(IDomainEvent @event);

        Task<List<IDomainEvent>> LoadAsync(int aggregateId);
    }
}
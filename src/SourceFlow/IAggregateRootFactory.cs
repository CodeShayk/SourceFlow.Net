using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    public interface IAggregateRootFactory
    {
        Task<TAggregateRoot> CreateAsync<TAggregateRoot>(IIdentity state = null)
            where TAggregateRoot : IAggregateRoot;
    }
}
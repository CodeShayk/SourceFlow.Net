using System.Threading.Tasks;

namespace SourceFlow
{
    public interface IAggregateFactory
    {
        Task<TAggregateRoot> CreateAsync<TAggregateRoot>(IIdentity state = null)
            where TAggregateRoot : IAggregateRoot;
    }
}
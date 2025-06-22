using System.Threading.Tasks;

namespace SourceFlow
{
    public interface IDomainRepository
    {
        Task<IIdentity> GetByIdAsync(int id);

        Task SaveAsync(IIdentity state);
    }
}
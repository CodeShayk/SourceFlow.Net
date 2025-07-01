using System.Threading.Tasks;

namespace SourceFlow
{
    public interface IAggregateRoot
    {
        Task ApplyAsync(IEvent @event);

        IIdentity State { get; set; }
    }
}
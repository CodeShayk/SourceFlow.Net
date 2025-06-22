using System.Threading.Tasks;

namespace SourceFlow
{
    public interface IAggregateRoot
    {
        Task ApplyAsync(IDomainEvent @event);

        IIdentity State { get; }
        int SequenceNo { get; set; }
    }
}
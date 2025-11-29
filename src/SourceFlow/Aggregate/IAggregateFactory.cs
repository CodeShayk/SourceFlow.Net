using System.Threading.Tasks;

namespace SourceFlow.Aggregate
{
    /// <summary>
    /// Interface for creating aggregate roots in the event-driven architecture.
    /// </summary>
    public interface IAggregateFactory
    {
        /// <summary>
        /// Creates a new instance of an aggregate root with the specified state.
        /// </summary>
        /// <typeparam name="TAggregate">Type Implementation of IAgrregate</typeparam>
        /// <param name="state"></param>
        /// <returns></returns>
        Task<TAggregate> Create<TAggregate>()
            where TAggregate : IAggregate;
    }
}

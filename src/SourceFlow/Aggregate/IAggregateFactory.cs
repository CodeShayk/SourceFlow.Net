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
        /// <typeparam name="TAggregateRoot"></typeparam>
        /// <param name="state"></param>
        /// <returns></returns>
        Task<TAggregateRoot> Create<TAggregateRoot>()
            where TAggregateRoot : IAggregateRoot;
    }
}
using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Interface for the service layer in the event-driven architecture.
    /// </summary>
    public interface IService
    {
        /// <summary>
        /// Creates an initialised aggregate root instance.
        /// </summary>
        /// <typeparam name="TAggregateRoot"></typeparam>
        /// <returns></returns>
        Task<TAggregateRoot> CreateAggregate<TAggregateRoot>() where TAggregateRoot : IAggregateRoot;
    }
}
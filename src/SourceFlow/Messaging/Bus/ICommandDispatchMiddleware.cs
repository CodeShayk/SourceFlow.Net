using System;
using System.Threading.Tasks;
using SourceFlow.Messaging.Commands;

namespace SourceFlow.Messaging.Bus
{
    /// <summary>
    /// Defines middleware that can intercept command dispatch operations in the command bus pipeline.
    /// </summary>
    public interface ICommandDispatchMiddleware
    {
        /// <summary>
        /// Invokes the middleware logic for a command dispatch operation.
        /// </summary>
        /// <typeparam name="TCommand">The type of command being dispatched.</typeparam>
        /// <param name="command">The command being dispatched.</param>
        /// <param name="next">A delegate to invoke the next middleware or the core dispatch logic.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task InvokeAsync<TCommand>(TCommand command, Func<TCommand, Task> next) where TCommand : ICommand;
    }
}

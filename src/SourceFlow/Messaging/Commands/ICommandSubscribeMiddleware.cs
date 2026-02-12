using System;
using System.Threading.Tasks;

namespace SourceFlow.Messaging.Commands
{
    /// <summary>
    /// Defines middleware that can intercept command subscribe operations in the command subscriber pipeline.
    /// </summary>
    public interface ICommandSubscribeMiddleware
    {
        /// <summary>
        /// Invokes the middleware logic for a command subscribe operation.
        /// </summary>
        /// <typeparam name="TCommand">The type of command being subscribed.</typeparam>
        /// <param name="command">The command being subscribed.</param>
        /// <param name="next">A delegate to invoke the next middleware or the core subscribe logic.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task InvokeAsync<TCommand>(TCommand command, Func<TCommand, Task> next) where TCommand : ICommand;
    }
}

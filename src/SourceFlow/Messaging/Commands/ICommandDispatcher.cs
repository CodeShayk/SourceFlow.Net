using System.Threading.Tasks;
using SourceFlow.Saga;

namespace SourceFlow.Messaging.Commands
{
    public interface ICommandDispatcher
    {
        /// <summary>
        /// Dispatches a command to the registered sagas.
        /// </summary>
        /// <param name="command"></param>
        Task Dispatch(ICommand command);
    }
}
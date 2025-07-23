using SourceFlow.Saga;

namespace SourceFlow.Messaging.Bus
{
    public interface ICommandDispatcher
    {
        /// <summary>
        /// Registers a saga with the command bus.
        /// </summary>
        /// <param name="saga"></param>
        void Register(ISaga saga);

        /// <summary>
        /// Dispatches a command to the registered sagas.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="command"></param>
        void Dispatch(object sender, ICommand command);
    }
}
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Saga
{
    /// <summary>
    /// Interface for handling command and producing event in the event-driven saga.
    /// </summary>
    /// <typeparam name="TCommand">On the Command of type TCommand.</typeparam>
    /// <typeparam name="TEvent">Raises event of type TEvent upon success.</typeparam>
    public interface IHandlesWithEvent<in TCommand, TEvent> : IHandles<TCommand>
        where TCommand : ICommand
        where TEvent : IEvent
    {
    }
}

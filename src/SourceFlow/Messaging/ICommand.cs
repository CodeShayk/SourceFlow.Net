namespace SourceFlow.Messaging
{
    /// <summary>
    /// Interface for commands in the event-driven architecture.
    /// </summary>
    public interface ICommand : IName, IMetadata
    {
        /// <summary>
        /// Payload of the command, which is an entity that contains the data associated with the command.
        /// </summary>
        IPayload Payload { get; set; }
    }
}
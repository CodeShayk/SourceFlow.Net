namespace SourceFlow.Messaging.Commands
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

        /// <summary>
        /// Reference to the entity associated with the command.
        /// </summary>
        EntityRef Entity { get; set; }
    }

    /// <summary>
    /// Reference to an entity in the event-driven architecture.
    /// </summary>
    public class EntityRef : IEntity
    {
        public int Id { get; set; }
        public bool IsNew { get; set; }
    }
}

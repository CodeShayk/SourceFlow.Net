namespace SourceFlow.Messaging
{
    /// <summary>
    /// Represents an event in the event-driven architecture.
    /// </summary>
    public interface IName
    {
        /// <summary>
        /// Gets the name for the event.
        /// </summary>
        string Name { get; set; }
    }
}

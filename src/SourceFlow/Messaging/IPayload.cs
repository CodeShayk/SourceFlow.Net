namespace SourceFlow.Messaging
{
    /// <summary>
    /// IPayload interface represents a command payload in the messaging system.
    /// </summary>
    public interface IPayload
    {
        /// <summary>
        /// Unique identifier for the Aggregate.
        /// </summary>
        int Id { get; set; }
    }
}
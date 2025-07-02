namespace SourceFlow
{
    /// <summary>
    /// Interface for a saga that handles events related to a specific aggregate root.
    /// </summary>
    /// <typeparam name="TAggregateRoot"></typeparam>
    public interface ISaga<TAggregateRoot> : ISagaHandler
        where TAggregateRoot : IAggregateRoot
    {
    }
}
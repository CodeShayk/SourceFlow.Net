namespace SourceFlow
{
    public interface ISaga<TAggregateRoot> : ISagaHandler
        where TAggregateRoot : IAggregateRoot
    {
    }
}
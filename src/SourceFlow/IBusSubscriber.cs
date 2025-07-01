namespace SourceFlow
{
    public interface IBusSubscriber
    {
        void Subscribe(ISagaHandler saga);
    }
}
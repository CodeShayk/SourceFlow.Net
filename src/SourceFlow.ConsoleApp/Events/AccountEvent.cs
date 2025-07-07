namespace SourceFlow.ConsoleApp.Events
{
    public abstract class AccountEvent<TPayload> : BaseEvent<TPayload> where TPayload : class, IEventPayload, new()
    {
    }
}
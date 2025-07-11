namespace SourceFlow.Core.Tests.E2E.Events
{
    public abstract class AccountEvent<TPayload> : BaseEvent<TPayload> where TPayload : class, IEventPayload, new()
    {
    }
}
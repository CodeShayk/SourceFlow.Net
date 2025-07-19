namespace SourceFlow.Core.Tests.E2E.Events
{
    public abstract class AccountEvent<TPayload> : BaseCommand<TPayload> where TPayload : class, IPayload, new()
    {
    }
}
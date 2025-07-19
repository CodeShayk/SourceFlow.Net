namespace SourceFlow.ConsoleApp.Commands
{
    public abstract class AccountCommand<TPayload> : BaseCommand<TPayload> where TPayload : class, IPayload, new()
    {
    }
}
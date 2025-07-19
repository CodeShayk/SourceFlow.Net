using SourceFlow.Messaging;

namespace SourceFlow.ConsoleApp.Commands
{
    public class CreateAccount : BaseCommand<AccountPayload>
    {
        public CreateAccount(AccountPayload payload) : base(payload)
        {
        }
    }
}
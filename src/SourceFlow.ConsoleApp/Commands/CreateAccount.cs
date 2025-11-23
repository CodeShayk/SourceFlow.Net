using SourceFlow.Messaging.Commands;

namespace SourceFlow.ConsoleApp.Commands
{
    public class CreateAccount : Command<Payload>
    {
        public CreateAccount(Payload payload) : base(true, payload)
        {
        }
    }
}
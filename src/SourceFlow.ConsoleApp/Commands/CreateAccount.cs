using SourceFlow.Messaging;

namespace SourceFlow.ConsoleApp.Commands
{
    public class CreateAccount : Command<Payload>
    {
        public CreateAccount(Payload payload) : base(payload)
        {
        }
    }
}
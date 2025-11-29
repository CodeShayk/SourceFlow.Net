using SourceFlow.Messaging.Commands;

namespace SourceFlow.Core.Tests.E2E.Commands
{
    public class CreateAccount : Command<Payload>
    {
        public CreateAccount(Payload payload) : base(true, payload)
        {
        }
    }
}

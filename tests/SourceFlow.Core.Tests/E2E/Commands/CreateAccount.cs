using SourceFlow.Messaging;

namespace SourceFlow.Core.Tests.E2E.Commands
{
    public class CreateAccount : Command<Payload>
    {
        public CreateAccount(Payload payload) : base(payload)
        {
        }
    }
}
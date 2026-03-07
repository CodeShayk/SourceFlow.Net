using SourceFlow.Messaging.Commands;

namespace SourceFlow.Stores.EntityFramework.Tests.E2E.Commands
{
    public class CreateAccount : Command<Payload>
    {
        // Parameterless constructor for deserialization
        public CreateAccount() : base()
        {
        }

        public CreateAccount(int entityId, Payload payload) : base(entityId, true, payload)
        {
        }
    }
}

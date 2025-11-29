using SourceFlow.Messaging.Commands;

namespace SourceFlow.Stores.EntityFramework.Tests.E2E.Commands
{
    public class ActivateAccount : Command<ActivationPayload>
    {
        // Parameterless constructor for deserialization
        public ActivateAccount() : base()
        {
        }

        public ActivateAccount(int entityId, ActivationPayload payload) : base(entityId, payload)
        {
        }
    }
}
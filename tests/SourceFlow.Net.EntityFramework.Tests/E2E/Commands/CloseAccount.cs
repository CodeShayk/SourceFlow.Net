using SourceFlow.Messaging.Commands;

namespace SourceFlow.Stores.EntityFramework.Tests.E2E.Commands
{
    public class CloseAccount : Command<ClosurePayload>
    {
        // Parameterless constructor for deserialization
        public CloseAccount() : base()
        {
        }

        public CloseAccount(int entityId, ClosurePayload payload) : base(entityId, payload)
        {
        }
    }
}

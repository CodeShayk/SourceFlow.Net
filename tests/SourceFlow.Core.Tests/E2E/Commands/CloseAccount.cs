using SourceFlow.Messaging.Commands;

namespace SourceFlow.Core.Tests.E2E.Commands
{
    public class CloseAccount : Command<ClosurePayload>
    {
        public CloseAccount(int entityId, ClosurePayload payload) : base(entityId, payload)
        {
        }
    }
}

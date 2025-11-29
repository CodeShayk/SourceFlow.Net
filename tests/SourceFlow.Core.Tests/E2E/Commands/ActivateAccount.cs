using SourceFlow.Messaging.Commands;

namespace SourceFlow.Core.Tests.E2E.Commands
{
    public class ActivateAccount : Command<ActivationPayload>
    {
        public ActivateAccount(int entityId, ActivationPayload payload) : base(entityId, payload)
        {
        }
    }
}

using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;

namespace SourceFlow.ConsoleApp.Commands
{
    public class ActivateAccount : Command<ActivationPayload>
    {
        public ActivateAccount(int entityId, ActivationPayload payload) : base(entityId, payload)
        {
        }
    }
}
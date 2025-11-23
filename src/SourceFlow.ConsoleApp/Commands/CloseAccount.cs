using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;

namespace SourceFlow.ConsoleApp.Commands
{
    public class CloseAccount : Command<ClosurePayload>
    {
        public CloseAccount(int entityId, ClosurePayload payload) : base(entityId, payload)
        {
        }
    }
}
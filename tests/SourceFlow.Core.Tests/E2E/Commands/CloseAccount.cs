using SourceFlow.Messaging;

namespace SourceFlow.Core.Tests.E2E.Commands
{
    public class CloseAccount : Command<ClosurePayload>
    {
        public CloseAccount(ClosurePayload payload) : base(payload)
        {
        }
    }
}
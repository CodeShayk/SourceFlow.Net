using SourceFlow.Messaging;

namespace SourceFlow.Core.Tests.E2E.Commands
{
    public class ActivateAccount : Command<ActivationPayload>
    {
        public ActivateAccount(ActivationPayload payload) : base(payload)
        {
        }
    }
}
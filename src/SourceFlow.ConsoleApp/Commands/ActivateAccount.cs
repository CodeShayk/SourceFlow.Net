using SourceFlow.Messaging;

namespace SourceFlow.ConsoleApp.Commands
{
    public class ActivateAccount : Command<ActivationPayload>
    {
        public ActivateAccount(ActivationPayload payload) : base(payload)
        {
        }
    }
}
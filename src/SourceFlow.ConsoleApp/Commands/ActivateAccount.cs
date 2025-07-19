using SourceFlow.Messaging;

namespace SourceFlow.ConsoleApp.Commands
{
    public class ActivateAccount : BaseCommand<ActivationPayload>
    {
        public ActivateAccount(ActivationPayload payload) : base(payload)
        {
        }
    }
}
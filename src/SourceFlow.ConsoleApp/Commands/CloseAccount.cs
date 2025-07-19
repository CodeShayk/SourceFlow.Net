using SourceFlow.Messaging;

namespace SourceFlow.ConsoleApp.Commands
{
    public class CloseAccount : BaseCommand<ClosurePayload>
    {
        public CloseAccount(ClosurePayload payload) : base(payload)
        {
        }
    }
}
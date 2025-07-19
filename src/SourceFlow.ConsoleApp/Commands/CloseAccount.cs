using SourceFlow.Messaging;

namespace SourceFlow.ConsoleApp.Commands
{
    public class CloseAccount : Command<ClosurePayload>
    {
        public CloseAccount(ClosurePayload payload) : base(payload)
        {
        }
    }
}
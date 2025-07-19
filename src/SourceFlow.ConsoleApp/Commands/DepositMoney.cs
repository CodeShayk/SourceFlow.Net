using SourceFlow.Messaging;

namespace SourceFlow.ConsoleApp.Commands
{
    public class DepositMoney : Command<TransactPayload>
    {
        public DepositMoney(TransactPayload payload) : base(payload)
        {
        }
    }
}
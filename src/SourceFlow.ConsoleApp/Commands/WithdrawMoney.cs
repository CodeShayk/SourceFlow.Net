using SourceFlow.Messaging;

namespace SourceFlow.ConsoleApp.Commands
{
    public class WithdrawMoney : Command<TransactPayload>
    {
        public WithdrawMoney(TransactPayload payload) : base(payload)
        {
        }
    }
}
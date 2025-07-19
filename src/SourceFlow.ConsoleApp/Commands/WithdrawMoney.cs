using SourceFlow.Messaging;

namespace SourceFlow.ConsoleApp.Commands
{
    public class WithdrawMoney : BaseCommand<TransactPayload>
    {
        public WithdrawMoney(TransactPayload payload) : base(payload)
        {
        }
    }
}
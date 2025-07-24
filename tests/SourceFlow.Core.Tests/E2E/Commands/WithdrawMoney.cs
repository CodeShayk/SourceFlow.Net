using SourceFlow.Messaging;

namespace SourceFlow.Core.Tests.E2E.Commands
{
    public class WithdrawMoney : Command<TransactPayload>
    {
        public WithdrawMoney(TransactPayload payload) : base(payload)
        {
        }
    }
}
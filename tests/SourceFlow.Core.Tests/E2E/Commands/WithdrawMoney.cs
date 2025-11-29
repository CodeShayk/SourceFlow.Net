using SourceFlow.Messaging.Commands;

namespace SourceFlow.Core.Tests.E2E.Commands
{
    public class WithdrawMoney : Command<TransactPayload>
    {
        public WithdrawMoney(int entityId, TransactPayload payload) : base(entityId, payload)
        {
        }
    }
}

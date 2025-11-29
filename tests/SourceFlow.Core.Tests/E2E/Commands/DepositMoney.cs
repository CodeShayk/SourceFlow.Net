using SourceFlow.Messaging.Commands;

namespace SourceFlow.Core.Tests.E2E.Commands
{
    public class DepositMoney : Command<TransactPayload>
    {
        public DepositMoney(int entityId, TransactPayload payload) : base(entityId, payload)
        {
        }
    }
}

using SourceFlow.Messaging.Commands;

namespace SourceFlow.Stores.EntityFramework.Tests.E2E.Commands
{
    public class WithdrawMoney : Command<TransactPayload>
    {
        // Parameterless constructor for deserialization
        public WithdrawMoney() : base()
        {
        }

        public WithdrawMoney(int entityId, TransactPayload payload) : base(entityId, payload)
        {
        }
    }
}

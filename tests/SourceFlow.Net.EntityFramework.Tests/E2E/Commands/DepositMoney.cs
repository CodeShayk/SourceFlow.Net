using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;

namespace SourceFlow.Stores.EntityFramework.Tests.E2E.Commands
{
    public class DepositMoney : Command<TransactPayload>
    {
        // Parameterless constructor for deserialization
        public DepositMoney() : base()
        {
        }

        public DepositMoney(int entityId, TransactPayload payload) : base(entityId, payload)
        {
        }
    }
}
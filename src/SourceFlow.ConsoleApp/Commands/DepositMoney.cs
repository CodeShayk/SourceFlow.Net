using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;

namespace SourceFlow.ConsoleApp.Commands
{
    public class DepositMoney : Command<TransactPayload>
    {
        public DepositMoney(int entityId, TransactPayload payload) : base(entityId, payload)
        {
        }
    }
}
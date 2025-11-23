using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;

namespace SourceFlow.ConsoleApp.Commands
{
    public class WithdrawMoney : Command<TransactPayload>
    {
        public WithdrawMoney(int entityId, TransactPayload payload) : base(entityId, payload)
        {
        }
    }
}
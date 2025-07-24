using SourceFlow.Messaging;

namespace SourceFlow.Core.Tests.E2E.Commands
{
    public class DepositMoney : Command<TransactPayload>
    {
        public DepositMoney(TransactPayload payload) : base(payload)
        {
        }
    }
}
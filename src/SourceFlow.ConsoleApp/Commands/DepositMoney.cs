using SourceFlow.Messaging;

namespace SourceFlow.ConsoleApp.Commands
{
    public class DepositMoney : BaseCommand<TransactPayload>
    {
        public DepositMoney(TransactPayload payload) : base(payload)
        {
        }
    }
}
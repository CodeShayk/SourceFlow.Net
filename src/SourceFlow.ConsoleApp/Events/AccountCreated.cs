using SourceFlow.ConsoleApp.Aggregates;
using SourceFlow.Messaging;

namespace SourceFlow.ConsoleApp.Events
{
    public class AccountCreated : BaseEvent<BankAccount>
    {
        public AccountCreated(BankAccount payload) : base(payload)
        {
        }
    }
}
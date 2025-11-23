using SourceFlow.ConsoleApp.Aggregates;
using SourceFlow.Messaging.Events;

namespace SourceFlow.ConsoleApp.Events
{
    public class AccountCreated : Event<BankAccount>
    {
        public AccountCreated(BankAccount payload) : base(payload)
        {
        }
    }
}
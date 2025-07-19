using SourceFlow.ConsoleApp.Aggregates;

namespace SourceFlow.ConsoleApp.Events
{
    public class AccountCreated : BaseEvent<BankAccount>
    {
        public AccountCreated(BankAccount payload) : base(payload)
        {
        }
    }
}
using SourceFlow.ConsoleApp.Aggregates;

namespace SourceFlow.ConsoleApp.Events
{
    public class AccountUpdated : BaseEvent<BankAccount>
    {
        public AccountUpdated(BankAccount payload) : base(payload)
        {
        }
    }
}
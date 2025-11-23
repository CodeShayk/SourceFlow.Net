using SourceFlow.ConsoleApp.Aggregates;
using SourceFlow.Messaging.Events;

namespace SourceFlow.ConsoleApp.Events
{
    public class AccountUpdated : Event<BankAccount>
    {
        public AccountUpdated(BankAccount payload) : base(payload)
        {
        }
    }
}
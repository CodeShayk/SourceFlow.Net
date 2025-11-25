using SourceFlow.Messaging.Events;
using SourceFlow.Stores.EntityFramework.Tests.E2E.Aggregates;

namespace SourceFlow.Stores.EntityFramework.Tests.E2E.Events
{
    public class AccountCreated : Event<BankAccount>
    {
        public AccountCreated(BankAccount payload) : base(payload)
        {
        }
    }
}
using SourceFlow.Messaging.Events;
using SourceFlow.Stores.EntityFramework.Tests.E2E.Aggregates;

namespace SourceFlow.Stores.EntityFramework.Tests.E2E.Events
{
    public class AccountUpdated : Event<BankAccount>
    {
        public AccountUpdated(BankAccount payload) : base(payload)
        {
        }
    }
}

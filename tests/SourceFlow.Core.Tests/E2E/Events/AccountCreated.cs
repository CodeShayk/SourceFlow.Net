using SourceFlow.Core.Tests.E2E.Aggregates;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Core.Tests.E2E.Events
{
    public class AccountCreated : Event<BankAccount>
    {
        public AccountCreated(BankAccount payload) : base(payload)
        {
        }
    }
}
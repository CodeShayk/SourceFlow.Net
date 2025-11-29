using SourceFlow.Core.Tests.E2E.Aggregates;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Core.Tests.E2E.Events
{
    public class AccountUpdated : Event<BankAccount>
    {
        public AccountUpdated(BankAccount payload) : base(payload)
        {
        }
    }
}

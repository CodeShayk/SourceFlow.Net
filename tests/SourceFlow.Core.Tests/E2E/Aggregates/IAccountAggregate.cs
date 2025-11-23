using SourceFlow.Core.Tests.E2E.Events;

namespace SourceFlow.Core.Tests.E2E.Aggregates
{
    public interface IAccountAggregate
    {
        Task CloseAccount(int accountId, string reason);
        Task CreateAccount(int accountId, string holder, decimal amount);
        Task Deposit(int accountId, decimal amount);
        Task Handle(AccountCreated @event);
        Task Withdraw(int accountId, decimal amount);
        Task RepayHistory(int accountId);
    }
}
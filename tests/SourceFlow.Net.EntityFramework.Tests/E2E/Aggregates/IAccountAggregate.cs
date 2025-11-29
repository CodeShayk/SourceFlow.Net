using System.Threading.Tasks;
using SourceFlow.Stores.EntityFramework.Tests.E2E.Events;

namespace SourceFlow.Stores.EntityFramework.Tests.E2E.Aggregates
{
    public interface IAccountAggregate
    {
        Task CloseAccount(int accountId, string reason);
        Task CreateAccount(int accountId, string holder, decimal amount);
        Task Deposit(int accountId, decimal amount);
        Task On(AccountCreated @event);
        Task Withdraw(int accountId, decimal amount);
        Task RepayHistory(int accountId);
    }
}
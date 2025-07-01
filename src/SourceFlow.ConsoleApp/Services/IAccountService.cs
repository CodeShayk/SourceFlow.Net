using SourceFlow.ConsoleApp.Aggregates;

namespace SourceFlow.ConsoleApp.Services
{
    public interface IAccountService
    {
        Task CloseAccountAsync(Guid accountId, string reason);
        Task<Guid> CreateAccountAsync(string accountHolderName, decimal initialBalance);
        Task DepositAsync(Guid accountId, decimal amount);
        Task<BankAccount> GetAccountAsync(Guid accountId);
        Task WithdrawAsync(Guid accountId, decimal amount);
    }
}
namespace SourceFlow.ConsoleApp.Services
{
    public interface IAccountService
    {
        Task CloseAccountAsync(int accountId, string reason);

        Task<int> CreateAccountAsync(string accountHolderName, decimal initialBalance);

        Task DepositAsync(int accountId, decimal amount);

        Task WithdrawAsync(int accountId, decimal amount);

        Task ReplayHistoryAsync(int accountId);
    }
}
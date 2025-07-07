using SourceFlow.Core.Tests.Aggregates;

namespace SourceFlow.Core.Tests.Services
{
    // ====================================================================================
    // APPLICATION SERVICE / COMMAND HANDLERS
    // ====================================================================================

    public class BankAccountService
    {
        private readonly IRepository<BankAccount> _repository;

        public BankAccountService(IRepository<BankAccount> repository)
        {
            _repository = repository;
        }

        public async Task<string> CreateAccountAsync(string accountHolderName, decimal initialBalance)
        {
            var accountId = Guid.NewGuid().ToString();
            var account = BankAccount.Create(accountId, accountHolderName, initialBalance);

            await _repository.SaveAsync(account);

            return accountId;
        }

        public async Task DepositAsync(string accountId, decimal amount)
        {
            var account = await _repository.GetByIdAsync(accountId);
            if (account == null)
                throw new InvalidOperationException($"Account {accountId} not found");

            account.Deposit(amount);
            await _repository.SaveAsync(account);
        }

        public async Task WithdrawAsync(string accountId, decimal amount)
        {
            var account = await _repository.GetByIdAsync(accountId);
            if (account == null)
                throw new InvalidOperationException($"Account {accountId} not found");

            account.Withdraw(amount);
            await _repository.SaveAsync(account);
        }

        public async Task CloseAccountAsync(string accountId, string reason)
        {
            var account = await _repository.GetByIdAsync(accountId);
            if (account == null)
                throw new InvalidOperationException($"Account {accountId} not found");

            account.Close(reason);
            await _repository.SaveAsync(account);
        }

        public async Task<BankAccount> GetAccountAsync(string accountId)
        {
            return await _repository.GetByIdAsync(accountId);
        }
    }
}
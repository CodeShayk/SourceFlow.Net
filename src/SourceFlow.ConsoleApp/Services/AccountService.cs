using SourceFlow.ConsoleApp.Aggregates;

namespace SourceFlow.ConsoleApp.Services
{
    // ====================================================================================
    // APPLICATION SERVICE / COMMAND HANDLERS
    // ====================================================================================
    public class AccountService : BaseCommandService, IAccountService
    {
        public AccountService()
        {
        }

        public async Task<Guid> CreateAccountAsync(string accountHolderName, decimal initialBalance)
        {
            var accountId = Guid.NewGuid(); // Simulating a unique account ID generation

            var account = await InitializeAggregate<AccountAggregate>(new BankAccount
            {
                Id = accountId,
                AccountHolderName = accountHolderName,
                Balance = initialBalance,
                IsClosed = false
            });

            account.AccountCreated();

            await SaveAggregate(account);

            return accountId;
        }

        public async Task DepositAsync(Guid accountId, decimal amount)
        {
            var account = await GetAggregate<AccountAggregate>(accountId);

            if (account == null)
                throw new InvalidOperationException($"Account {accountId} not found");

            account.Deposit(amount);

            await SaveAggregate(account);
        }

        public async Task WithdrawAsync(Guid accountId, decimal amount)
        {
            var account = await GetAggregate<AccountAggregate>(accountId);

            if (account == null)
                throw new InvalidOperationException($"Account {accountId} not found");

            account.Withdraw(amount);

            await SaveAggregate(account);
        }

        public async Task CloseAccountAsync(Guid accountId, string reason)
        {
            var account = await GetAggregate<AccountAggregate>(accountId);

            if (account == null)
                throw new InvalidOperationException($"Account {accountId} not found");

            account.Close(reason);

            await SaveAggregate(account);
        }

        public async Task<BankAccount> GetAccountAsync(Guid accountId)
        {
            var account = await GetAggregate<AccountAggregate>(accountId);

            if (account == null)
                throw new InvalidOperationException($"Account {accountId} not found");

            return account.State;
        }
    }
}
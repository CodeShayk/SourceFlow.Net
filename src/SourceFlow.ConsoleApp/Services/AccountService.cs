using SourceFlow.ConsoleApp.Aggregates;

namespace SourceFlow.ConsoleApp.Services
{
    public class AccountService : BaseService, IAccountService
    {
        public async Task<int> CreateAccountAsync(string accountHolderName, decimal initialBalance)
        {
            if (string.IsNullOrEmpty(accountHolderName))
                throw new ArgumentException("Account create requires account holder name.", nameof(accountHolderName));

            if (initialBalance <= 0)
                throw new ArgumentException("Account create requires initial amount.", nameof(initialBalance));

            var account = await CreateAggregate<AccountAggregate>();
            if (account == null)
                throw new InvalidOperationException("Failed to create account aggregate");

            var accountId = new Random().Next(); // Simulating a unique account ID generation

            account.CreateAccount(accountId, accountHolderName, initialBalance);

            return accountId;
        }

        public async Task DepositAsync(int accountId, decimal amount)
        {
            if (accountId <= 0)
                throw new ArgumentException("Deposit amount must need account id", nameof(amount));

            if (amount <= 0)
                throw new ArgumentException("Deposit amount must be positive", nameof(amount));

            var account = await CreateAggregate<AccountAggregate>();

            account.Deposit(accountId, amount);
        }

        public async Task WithdrawAsync(int accountId, decimal amount)
        {
            if (accountId <= 0)
                throw new ArgumentException("Withdraw amount must need account id", nameof(amount));

            if (amount <= 0)
                throw new ArgumentException("Withdraw amount must be positive", nameof(amount));

            var account = await CreateAggregate<AccountAggregate>();
            if (account == null)
                throw new InvalidOperationException("Failed to create account aggregate");

            account.Withdraw(accountId, amount);
        }

        public async Task CloseAccountAsync(int accountId, string reason)
        {
            if (accountId <= 0)
                throw new ArgumentException("Close account requires valid account id", nameof(accountId));

            if (string.IsNullOrEmpty(reason))
                throw new ArgumentException("Close account requires reason", nameof(reason));

            var account = await CreateAggregate<AccountAggregate>();

            account.Close(accountId, reason);
        }

        public async Task ReplayHistoryAsync(int accountId)
        {
            if (accountId <= 0)
                throw new ArgumentException("Account history requires valid account id", nameof(accountId));

            var account = await CreateAggregate<AccountAggregate>();

            await account.ReplayEvents(accountId);
        }
    }
}
using SourceFlow.Core.Tests.E2E.Projections;

namespace SourceFlow.Core.Tests.E2E.Services
{
    public class AccountViewFinder : BaseViewModelFinder, IAccountFinder
    {
        public AccountViewFinder(IViewModelRepository repository) : base(repository)
        {
        }

        public async Task<AccountViewModel> GetAccountSummaryAsync(int accountId)
        {
            if (accountId <= 0)
                throw new ArgumentException("Account summary requires valid account id", nameof(accountId));
            var summary = await Find<AccountViewModel>(accountId);
            if (summary == null)
                throw new InvalidOperationException($"No account found with ID {accountId}");
            return summary;
        }
    }
}
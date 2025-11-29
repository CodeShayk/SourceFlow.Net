using Microsoft.Extensions.Logging;
using SourceFlow.Core.Tests.E2E.Events;
using SourceFlow.Projections;

namespace SourceFlow.Core.Tests.E2E.Projections
{
    public class AccountView : View<AccountViewModel>,
                               IProjectOn<AccountCreated>,
                               IProjectOn<AccountUpdated>
    {
        public AccountView(IViewModelStoreAdapter viewModelStore, ILogger<IView> logger) : base(viewModelStore, logger)
        {
        }

        public async Task<IViewModel> On(AccountCreated @event)
        {
            var viewModel = new AccountViewModel
            {
                Id = @event.Payload.Id,
                AccountName = @event.Payload.AccountName,
                CurrentBalance = @event.Payload.Balance,
                IsClosed = false,
                CreatedDate = @event.Payload.CreatedOn,
                LastUpdated = DateTime.UtcNow,
                TransactionCount = 0,
                ClosureReason = null!,
                Version = 1
            };

            return viewModel;
        }

        public async Task<IViewModel> On(AccountUpdated @event)
        {
            var viewModel = await Find<AccountViewModel>(@event.Payload.Id);

            viewModel.CurrentBalance = @event.Payload.Balance;
            viewModel.LastUpdated = DateTime.UtcNow;
            viewModel.AccountName = @event.Payload.AccountName;
            viewModel.IsClosed = @event.Payload.IsClosed;
            viewModel.ClosureReason = @event.Payload.ClosureReason;
            viewModel.ActiveOn = @event.Payload.ActiveOn;
            viewModel.Version++;
            viewModel.TransactionCount++;

            return viewModel;
        }
    }
}

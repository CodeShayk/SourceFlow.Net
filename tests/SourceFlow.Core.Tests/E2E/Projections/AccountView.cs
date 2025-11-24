using Microsoft.Extensions.Logging;
using SourceFlow.Core.Tests.E2E.Events;
using SourceFlow.Projections;

namespace SourceFlow.Core.Tests.E2E.Projections
{
    public class AccountView : View,
                               IProjectOn<AccountCreated>,
                               IProjectOn<AccountUpdated>
    {
        public AccountView(IViewModelStoreAdapter viewModelStore, ILogger<IView> logger): base(viewModelStore, logger)
        {
            
        }

        public async Task Apply(AccountCreated @event)
        {
            var view = new AccountViewModel
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

            await viewModelStore.Persist(view);
        }

        public async Task Apply(AccountUpdated @event)
        {
            var view = await viewModelStore.Find<AccountViewModel>(@event.Payload.Id);

            view.CurrentBalance = @event.Payload.Balance;
            view.LastUpdated = DateTime.UtcNow;
            view.AccountName = @event.Payload.AccountName;
            view.IsClosed = @event.Payload.IsClosed;
            view.ClosureReason = @event.Payload.ClosureReason;
            view.ActiveOn = @event.Payload.ActiveOn;
            view.Version++;
            view.TransactionCount++;

            await viewModelStore.Persist(view);
        }
    }
}
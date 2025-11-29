using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SourceFlow.Projections;
using SourceFlow.Stores.EntityFramework.Tests.E2E.Events;

namespace SourceFlow.Stores.EntityFramework.Tests.E2E.Projections
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

            return await viewModelStore.Persist(view);
        }

        public async Task<IViewModel> On(AccountUpdated @event)
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

            return await viewModelStore.Persist(view);
        }
    }
}

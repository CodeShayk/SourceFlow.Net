using SourceFlow.ConsoleApp.Events;
using SourceFlow.ViewModel;

namespace SourceFlow.ConsoleApp.Views
{
    public class AccountView : IViewProjection<AccountCreated>,
                               IViewProjection<AccountUpdated>
    {
        private readonly IViewProvider provider;

        public AccountView(IViewProvider provider)
        {
            this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
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
                ClosureReason = null,
                Version = 1
            };

            await provider.Push(view);
        }

        public async Task Apply(AccountUpdated @event)
        {
            var view = await provider.Find<AccountViewModel>(@event.Payload.Id);

            if (view == null)
                throw new InvalidOperationException($"Account view not found for ID: {@event.Payload.Id}");

            view.CurrentBalance = @event.Payload.Balance;
            view.LastUpdated = DateTime.UtcNow;
            view.AccountName = @event.Payload.AccountName;
            view.IsClosed = @event.Payload.IsClosed;
            view.ClosureReason = @event.Payload.ClosureReason;
            view.ActiveOn = @event.Payload.ActiveOn;
            view.Version++;
            view.TransactionCount++;

            await provider.Push(view);
        }
    }
}
using SourceFlow.ConsoleApp.Events;

namespace SourceFlow.ConsoleApp.Views
{
    public class AccountView : IViewTransform<AccountCreated>,
                               IViewTransform<AccountUpdated>
    {
        private readonly IViewRepository repository;

        public AccountView(IViewRepository repository)
        {
            this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        public async Task Transform(AccountCreated @event)
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

            await repository.Persist(view);
        }

        public async Task Transform(AccountUpdated @event)
        {
            var view = await repository.Get<AccountViewModel>(@event.Payload.Id);

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

            await repository.Persist(view);
        }
    }
}
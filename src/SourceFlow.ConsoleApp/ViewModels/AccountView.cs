using SourceFlow.ConsoleApp.Aggregates;
using SourceFlow.Events;

namespace SourceFlow.ConsoleApp.ViewModels
{
    public class AccountView : IViewTransform<EntityCreated<BankAccount>>,
                               IViewTransform<EntityUpdated<BankAccount>>
    {
        private readonly IViewRepository repository;

        public AccountView(IViewRepository repository)
        {
            this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        public async Task Transform(EntityCreated<BankAccount> @event)
        {
            if (@event.Name != "BankAccountCreated")
                throw new InvalidOperationException($"Unexpected event type: {@event.Name}");

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

        public async Task Transform(EntityUpdated<BankAccount> @event)
        {
            if (@event.Name != "BankAccountUpdated")
                throw new InvalidOperationException($"Unexpected event type: {@event.Name}");

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
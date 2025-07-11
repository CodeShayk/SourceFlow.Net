using SourceFlow.ConsoleApp.Events;

namespace SourceFlow.ConsoleApp.ViewModels
{
    public class AccountViewTransforms : IViewModelTransform<AccountCreated>
                                       , IViewModelTransform<MoneyDeposited>
                                       , IViewModelTransform<MoneyWithdrawn>
                                       , IViewModelTransform<AccountClosed>
    {
        private readonly IViewModelRepository repository;

        public AccountViewTransforms(IViewModelRepository repository)
        {
            this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        public async Task Transform(AccountCreated @event)
        {
            var view = new AccountViewModel
            {
                Id = @event.Entity.Id,
                AccountName = @event.Payload.AccountName,
                CurrentBalance = @event.Payload.InitialAmount,
                IsClosed = false,
                CreatedDate = @event.OccurredOn,
                LastUpdated = @event.OccurredOn,
                TransactionCount = 1,
                ClosureReason = null,
                Version = @event.SequenceNo
            };

            await repository.Persist(view);
        }

        public async Task Transform(MoneyDeposited @event)
        {
            var view = await repository.Get<AccountViewModel>(@event.Entity.Id);

            if (view == null)
                throw new InvalidOperationException($"Account view not found for ID: {@event.Entity.Id}");

            view.CurrentBalance = @event.Payload.CurrentBalance;
            view.LastUpdated = @event.OccurredOn;
            view.Version = @event.SequenceNo;
            view.TransactionCount++;

            await repository.Persist(view);
        }

        public async Task Transform(MoneyWithdrawn @event)
        {
            var view = await repository.Get<AccountViewModel>(@event.Entity.Id);

            if (view == null)
                throw new InvalidOperationException($"Account view not found for ID: {@event.Entity.Id}");

            view.CurrentBalance = @event.Payload.CurrentBalance;
            view.LastUpdated = @event.OccurredOn;
            view.Version = @event.SequenceNo;
            view.TransactionCount++;

            await repository.Persist(view);
        }

        public async Task Transform(AccountClosed @event)
        {
            var view = await repository.Get<AccountViewModel>(@event.Entity.Id);

            if (view == null)
                throw new InvalidOperationException($"Account view not found for ID: {@event.Entity.Id}");

            view.ClosureReason = @event.Payload.ClosureReason;
            view.LastUpdated = @event.OccurredOn;
            view.Version = @event.SequenceNo;
            view.IsClosed = @event.Payload.IsClosed;

            await repository.Persist(view);
        }
    }
}
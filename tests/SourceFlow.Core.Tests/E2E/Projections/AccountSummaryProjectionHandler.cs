//using SourceFlow.ConsoleApp.Events;

//namespace SourceFlow.ConsoleApp.Projections
//{
//    public class AccountSummaryProjectionHandler :
//        IProjectionHandler<ActivateAccount>,
//        IProjectionHandler<MoneyDeposited>,
//        IProjectionHandler<MoneyWithdrawn>,
//        IProjectionHandler<AccountClosed>
//    {
//        private readonly Dictionary<string, AccountViewModel> _projections = new Dictionary<string, AccountViewModel>();

//        public Task Handle(ActivateAccount @event)
//        {
//            _projections[@event.AggregateId] = new AccountViewModel
//            {
//                AggregateId = @event.AggregateId,
//                AccountName = @event.AccountName,
//                CurrentBalance = @event.InitialBalance,
//                CreatedDate = @event.Timestamp,
//                LastUpdated = @event.Timestamp,
//                TransactionCount = 1
//            };

//            return Task.CompletedTask;
//        }

//        public Task Handle(MoneyDeposited @event)
//        {
//            if (_projections.TryGetValue(@event.AggregateId, out var projection))
//            {
//                projection.CurrentBalance = @event.NewBalance;
//                projection.LastUpdated = @event.Timestamp;
//                projection.TransactionCount++;
//            }

//            return Task.CompletedTask;
//        }

//        public Task Handle(MoneyWithdrawn @event)
//        {
//            if (_projections.TryGetValue(@event.AggregateId, out var projection))
//            {
//                projection.CurrentBalance = @event.NewBalance;
//                projection.LastUpdated = @event.Timestamp;
//                projection.TransactionCount++;
//            }

//            return Task.CompletedTask;
//        }

//        public Task Handle(AccountClosed @event)
//        {
//            if (_projections.TryGetValue(@event.AggregateId, out var projection))
//            {
//                projection.IsClosed = true;
//                projection.ClosureReason = @event.Reason;
//                projection.LastUpdated = @event.Timestamp;
//            }

//            return Task.CompletedTask;
//        }

//        public AccountViewModel GetProjection(string accountId)
//        {
//            return _projections.TryGetValue(accountId, out var projection) ? projection : null;
//        }

//        public IEnumerable<AccountViewModel> GetAllProjections()
//        {
//            return _projections.Values;
//        }
//    }
//}
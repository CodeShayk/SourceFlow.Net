//using SourceFlow.ConsoleApp.Events;

//namespace SourceFlow.ConsoleApp.Projections
//{
//    public class AccountSummaryProjectionHandler :
//        IProjectionHandler<ActivateAccount>,
//        IProjectionHandler<MoneyDeposited>,
//        IProjectionHandler<MoneyWithdrawn>,
//        IProjectionHandler<AccountClosed>
//    {
//        private readonly Dictionary<string, AccountSummaryProjection> _projections = new Dictionary<string, AccountSummaryProjection>();

//        public Task HandleAsync(ActivateAccount @event)
//        {
//            _projections[@event.AggregateId] = new AccountSummaryProjection
//            {
//                AggregateId = @event.AggregateId,
//                AccountHolderName = @event.AccountHolderName,
//                CurrentBalance = @event.InitialBalance,
//                CreatedDate = @event.Timestamp,
//                LastUpdated = @event.Timestamp,
//                TransactionCount = 1
//            };

//            return Task.CompletedTask;
//        }

//        public Task HandleAsync(MoneyDeposited @event)
//        {
//            if (_projections.TryGetValue(@event.AggregateId, out var projection))
//            {
//                projection.CurrentBalance = @event.NewBalance;
//                projection.LastUpdated = @event.Timestamp;
//                projection.TransactionCount++;
//            }

//            return Task.CompletedTask;
//        }

//        public Task HandleAsync(MoneyWithdrawn @event)
//        {
//            if (_projections.TryGetValue(@event.AggregateId, out var projection))
//            {
//                projection.CurrentBalance = @event.NewBalance;
//                projection.LastUpdated = @event.Timestamp;
//                projection.TransactionCount++;
//            }

//            return Task.CompletedTask;
//        }

//        public Task HandleAsync(AccountClosed @event)
//        {
//            if (_projections.TryGetValue(@event.AggregateId, out var projection))
//            {
//                projection.IsClosed = true;
//                projection.ClosureReason = @event.Reason;
//                projection.LastUpdated = @event.Timestamp;
//            }

//            return Task.CompletedTask;
//        }

//        public AccountSummaryProjection GetProjection(string accountId)
//        {
//            return _projections.TryGetValue(accountId, out var projection) ? projection : null;
//        }

//        public IEnumerable<AccountSummaryProjection> GetAllProjections()
//        {
//            return _projections.Values;
//        }
//    }
//}
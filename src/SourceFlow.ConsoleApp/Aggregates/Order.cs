using SourceFlow.ConsoleApp.Events;
using SourceFlow.Core;

namespace SourceFlow.ConsoleApp.Aggregates
{
    public class Order
    {
        public Guid Id { get; private set; }
        public decimal Amount { get; private set; }
        public bool IsCompleted { get; private set; }
        public bool IsCanceled { get; private set; }

        private readonly List<IDomainEvent> _uncommittedEvents = new();

        public IEnumerable<IDomainEvent> UncommittedEvents => _uncommittedEvents;

        public static Order Create(Guid orderId, decimal amount)
        {
            var order = new Order();
            order.Apply(new OrderPlaced(orderId, amount));
            return order;
        }

        public void Apply(IDomainEvent @event)
        {
            switch (@event)
            {
                case OrderPlaced e:
                    Id = e.OrderId;
                    Amount = e.Amount;
                    break;
                case OrderCompleted:
                    IsCompleted = true;
                    break;
                case OrderCanceled:
                    IsCanceled = true;
                    break;
            }

            _uncommittedEvents.Add(@event);
        }

        public void MarkEventsAsCommitted() => _uncommittedEvents.Clear();
    }

}
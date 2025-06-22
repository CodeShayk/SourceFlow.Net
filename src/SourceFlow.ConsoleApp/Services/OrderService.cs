using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SourceFlow.ConsoleApp.Aggregates;
using SourceFlow.ConsoleApp.Events;
using SourceFlow.ConsoleApp.Sagas;
using SourceFlow.Core;

namespace SourceFlow.ConsoleApp.Services
{
    public class OrderService
    {
        private readonly IDomainEventStore _store;
        private readonly OrderSaga _saga;

        public OrderService(IDomainEventStore store, OrderSaga saga)
        {
            _store = store;
            _saga = saga;
        }

        public void PlaceOrder(Guid orderId, decimal amount)
        {
            var order = Order.Create(orderId, amount);
            foreach (var e in order.UncommittedEvents)
            {
                _store.Append(orderId, e);
                _saga.Handle(e);
            }
            order.MarkEventsAsCommitted();
        }

        public void ReceivePayment(Guid orderId)
        {
            var evt = new PaymentReceived(orderId);
            _store.Append(orderId, evt);
            _saga.Handle(evt);
        }

        public void FailPayment(Guid orderId)
        {
            var evt = new PaymentFailed(orderId);
            _store.Append(orderId, evt);
            _saga.Handle(evt);
        }
    }
}
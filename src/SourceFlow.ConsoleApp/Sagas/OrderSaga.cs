using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SourceFlow.ConsoleApp.Events;
using SourceFlow.Core;

namespace SourceFlow.ConsoleApp.Sagas
{
    public class OrderSaga
    {
        private readonly IDomainEventStore _eventStore;

        public OrderSaga(IDomainEventStore eventStore)
        {
            _eventStore = eventStore;
        }

        public void Handle(IDomainEvent @event)
        {
            switch (@event)
            {
                case OrderPlaced placed:
                    // Wait for payment → do nothing
                    break;

                case PaymentReceived payment:
                    var completeEvent = new OrderCompleted(payment.OrderId);
                    _eventStore.Append(payment.OrderId, completeEvent);
                    break;

                case PaymentFailed failed:
                    var cancelEvent = new OrderCanceled(failed.OrderId);
                    _eventStore.Append(failed.OrderId, cancelEvent);
                    break;
            }
        }
    }
}
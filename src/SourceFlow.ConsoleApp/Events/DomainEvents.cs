using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SourceFlow.Core;

namespace SourceFlow.ConsoleApp.Events
{
    public record OrderPlaced(Guid OrderId, decimal Amount) : IDomainEvent
    {
        public Guid EventId => Guid.NewGuid();
        public DateTime OccurredOn => DateTime.UtcNow;
    }

    public record PaymentReceived(Guid OrderId) : IDomainEvent
    {
        public Guid EventId => Guid.NewGuid();
        public DateTime OccurredOn => DateTime.UtcNow;
    }

    public record PaymentFailed(Guid OrderId) : IDomainEvent
    {
        public Guid EventId => Guid.NewGuid();
        public DateTime OccurredOn => DateTime.UtcNow;
    }

    public record OrderCompleted(Guid OrderId) : IDomainEvent
    {
        public Guid EventId => Guid.NewGuid();
        public DateTime OccurredOn => DateTime.UtcNow;
    }

    public record OrderCanceled(Guid OrderId) : IDomainEvent
    {
        public Guid EventId => Guid.NewGuid();
        public DateTime OccurredOn => DateTime.UtcNow;
    }
}
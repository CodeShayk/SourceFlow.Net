using Moq;

namespace SourceFlow.Tests
{
    public class SampleSagaTests
    {
        public class DummyEvent : IEvent
        {
            public Guid EventId { get; set; } = Guid.NewGuid();
            public Source Entity { get; set; } = new Source(1, typeof(object));
            public bool IsReplay { get; set; }
            public DateTime OccurredOn { get; set; } = DateTime.UtcNow;
            public int SequenceNo { get; set; }
            public DateTime Timestamp => OccurredOn;
            public string EventType => nameof(DummyEvent);
            public int Version => 1;
        }

        [Test]
        public async Task HandleAsync_CallsRegisteredHandler()
        {
            var handlerMock = new Mock<IEventHandler<DummyEvent>>();
            handlerMock.Setup(h => h.HandleAsync(It.IsAny<DummyEvent>())).Returns(Task.CompletedTask);

            var saga = new SampleSaga();
            saga.Handlers.Add(new SagaHandler(typeof(DummyEvent), (IEventHandler)handlerMock.Object));

            var dummyEvent = new DummyEvent();
            await saga.HandleAsync(dummyEvent);

            handlerMock.Verify(h => h.HandleAsync(dummyEvent), Times.Once);
        }

        public class SampleSaga : ISaga
        {
            public ICollection<SagaHandler> Handlers { get; } = new List<SagaHandler>();

            public Task HandleAsync<TEvent>(TEvent @event) where TEvent : IEvent
            {
                // Example: just call all handlers for the event type
                foreach (var handler in Handlers)
                {
                    if (handler.EventType == typeof(TEvent))
                        ((IEventHandler<TEvent>)handler.Handler).HandleAsync(@event);
                }
                return Task.CompletedTask;
            }
        }
    }
}
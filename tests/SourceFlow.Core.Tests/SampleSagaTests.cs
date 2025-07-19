//using Moq;

//namespace SourceFlow.Tests
//{
//    public class SampleSagaTests
//    {
//        public class DummyEvent : ICommand
//        {
//            public Guid EventId { get; set; } = Guid.NewGuid();
//            public Source Entity { get; set; } = new Source(1, typeof(object));
//            public bool IsReplay { get; set; }
//            public DateTime OccurredOn { get; set; } = DateTime.UtcNow;
//            public int SequenceNo { get; set; }
//            public DateTime Timestamp => OccurredOn;
//            public string EventType => nameof(DummyEvent);
//            public int Version => 1;
//        }

//        [Test]
//        public async Task HandleAsync_CallsRegisteredHandler()
//        {
//            var handlerMock = new Mock<IHandles<DummyEvent>>();
//            handlerMock.Setup(h => h.Handle(It.IsAny<DummyEvent>())).Returns(Task.CompletedTask);

//            var saga = new SampleSaga();

//            var dummyEvent = new DummyEvent();
//            await saga.Handle(dummyEvent);

//            handlerMock.Verify(h => h.Handle(dummyEvent), Times.Once);
//        }

//        public class SampleSaga : ISaga
//        {
//            public Task Handle<TEvent>(TEvent @event) where TEvent : ICommand
//            {
//                // Example: just call all handlers for the event type
//                foreach (var handler in Handlers)
//                {
//                    if (handler.EventType == typeof(TEvent))
//                        ((IHandles<TEvent>)handler.Handler).Handle(@event);
//                }
//                return Task.CompletedTask;
//            }
//        }
//    }
//}
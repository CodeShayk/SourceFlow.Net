using Microsoft.Extensions.Logging;
using Moq;
using SourceFlow.Aggregate;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Core.Tests.Impl
{
    [TestFixture]
    public class AggregateSubscriberTests
    {
        [Test]
        public void Constructor_NullAggregates_ThrowsArgumentNullException()
        {
            var loggerMock = new Mock<ILogger<IEventSubscriber>>();
            Assert.Throws<ArgumentNullException>(() => new Aggregate.EventSubscriber(null, loggerMock.Object));
        }

        [Test]
        public void Constructor_NullLogger_ThrowsArgumentNullException()
        {
            var aggregates = new List<IAggregate>();
            Assert.Throws<ArgumentNullException>(() => new Aggregate.EventSubscriber(aggregates, null));
        }

        [Test]
        public async Task Dispatch_ValidEvent_LogsInformation()
        {
            var loggerMock = new Mock<ILogger<IEventSubscriber>>();
            var aggregateMock = new Mock<IAggregate>();
            // Make the aggregate implement ISubscribes<DummyEvent> so it gets called
            aggregateMock.As<ISubscribes<DummyEvent>>()
                .Setup(a => a.On(It.IsAny<DummyEvent>()))
                .Returns(Task.CompletedTask);
            var aggregates = new List<IAggregate> { aggregateMock.Object };
            var dispatcher = new Aggregate.EventSubscriber(aggregates, loggerMock.Object);
            var eventMock = new DummyEvent();
            await dispatcher.Subscribe(eventMock);
            loggerMock.Verify(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()),
                Times.AtLeastOnce);
        }
    }
}

using Microsoft.Extensions.Logging;
using Moq;
using SourceFlow.Messaging.Events;
using SourceFlow.Projections;

namespace SourceFlow.Core.Tests.Impl
{
    [TestFixture]
    public class ProjectionSubscriberTests
    {
        [Test]
        public void Constructor_NullProjections_ThrowsArgumentNullException()
        {
            var logger = new Mock<ILogger<IEventSubscriber>>().Object;
            Assert.Throws<ArgumentNullException>(() => new Projections.EventSubscriber(null, logger));
        }

        [Test]
        public void Constructor_NullLogger_ThrowsArgumentNullException()
        {
            var projections = new List<IProjection>();
            Assert.Throws<ArgumentNullException>(() => new Projections.EventSubscriber(projections, null));
        }

        [Test]
        public async Task Dispatch_ValidEvent_LogsInformation()
        {
            var loggerMock = new Mock<ILogger<IEventSubscriber>>();
            var projectionMock = new Mock<IProjection>();
            // Make the projection implement IProjectOn<IEvent> so it gets called
            var eventMock = new Mock<IEvent>();
            eventMock.Setup(e => e.Name).Returns("TestEvent");
            projectionMock.As<IProjectOn<IEvent>>()
                .Setup(p => p.Apply(It.IsAny<IEvent>()))
                .Returns(Task.CompletedTask);
            var projections = new List<IProjection> { projectionMock.Object };
            var dispatcher = new Projections.EventSubscriber(projections, loggerMock.Object);
            await dispatcher.Subscribe(eventMock.Object);
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
using Microsoft.Extensions.Logging;
using Moq;
using SourceFlow.Impl;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Bus;
using SourceFlow.Projections;

namespace SourceFlow.Core.Tests.Impl
{
    [TestFixture]
    public class ProjectionDispatcherTests
    {
        [Test]
        public void Constructor_NullProjections_ThrowsArgumentNullException()
        {
            var logger = new Mock<ILogger<IEventDispatcher>>().Object;
            Assert.Throws<ArgumentNullException>(() => new ProjectionDispatcher(null, logger));
        }

        [Test]
        public void Constructor_NullLogger_ThrowsArgumentNullException()
        {
            var projections = new List<IProjection>();
            Assert.Throws<ArgumentNullException>(() => new ProjectionDispatcher(projections, null));
        }

        [Test]
        public void Dispatch_ValidEvent_LogsInformation()
        {
            var loggerMock = new Mock<ILogger<IEventDispatcher>>();
            var projectionMock = new Mock<IProjection>();
            var projections = new List<IProjection> { projectionMock.Object };
            var dispatcher = new ProjectionDispatcher(projections, loggerMock.Object);
            var eventMock = new Mock<IEvent>();
            eventMock.Setup(e => e.Name).Returns("TestEvent");
            dispatcher.Dispatch(this, eventMock.Object);
            loggerMock.Verify(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                (Func<It.IsAnyType, Exception, string>)It.IsAny<object>()),
                Times.AtLeastOnce);
        }
    }
}
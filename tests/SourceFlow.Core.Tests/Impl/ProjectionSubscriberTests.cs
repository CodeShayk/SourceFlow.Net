using Microsoft.Extensions.Logging;
using Moq;
using SourceFlow.Messaging;
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
            Assert.Throws<ArgumentNullException>(() => new SourceFlow.Projections.EventSubscriber(null, logger));
        }

        [Test]
        public void Constructor_NullLogger_ThrowsArgumentNullException()
        {
            var projections = new List<IView>();
            Assert.Throws<ArgumentNullException>(() => new SourceFlow.Projections.EventSubscriber(projections, null));
        }

        [Test]
        public async Task Dispatch_ValidEvent_LogsInformation()
        {
            var loggerMock = new Mock<ILogger<IEventSubscriber>>();

            // Create a concrete test event implementation instead of a mock
            var metadata = new Metadata { SequenceNo = 1 };
            var payload = new TestEntity { Id = 1 };
            var testEvent = new TestEvent
            {
                Name = "TestEvent",
                Metadata = metadata,
                Payload = payload
            };

            // Create a concrete test projection instead of a mock
            var testProjection = new TestProjection();
            var projections = new List<IView> { testProjection };

            var dispatcher = new SourceFlow.Projections.EventSubscriber(projections, loggerMock.Object);
            await dispatcher.Subscribe(testEvent);

            loggerMock.Verify(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()),
                Times.AtLeastOnce);
        }

        // Test entity implementation
        private class TestEntity : IEntity
        {
            public int Id { get; set; }
        }

        // Test event implementation
        private class TestEvent : IEvent
        {
            public string Name { get; set; } = string.Empty;
            public IEntity Payload { get; set; } = null!;
            public Metadata Metadata { get; set; } = null!;
        }

        // Test projection implementation
        private class TestProjection : View<TestProjectionViewModel>, IProjectOn<TestEvent>
        {
            public TestProjection() : base(new Mock<IViewModelStoreAdapter>().Object, new Mock<ILogger<IView>>().Object)
            {
            }

            public Task<IViewModel> On(TestEvent @event)
            {
                return Task.FromResult<IViewModel>(new TestProjectionViewModel { Id = 1 });
            }
        }

        private class TestProjectionViewModel : IViewModel
        {
            public int Id { get; set; }
        }
    }
}

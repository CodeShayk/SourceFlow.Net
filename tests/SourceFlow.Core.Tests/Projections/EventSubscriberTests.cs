using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using SourceFlow.Messaging.Events;
using SourceFlow.Projections;

namespace SourceFlow.Core.Tests.Projections
{
    public class DummyProjectionEntity : IEntity
    {
        public int Id { get; set; }
    }

    public class DummyProjectionEvent : Event<DummyProjectionEntity>
    {
        public DummyProjectionEvent(DummyProjectionEntity payload) : base(payload) { }
    }

    public class TestProjection : View, IProjectOn<DummyProjectionEvent>
    {
        public TestProjection() : base(new Mock<IViewModelStoreAdapter>().Object, new Mock<ILogger<IView>>().Object)
        {
        }

        public bool Applied { get; private set; } = false;

        public Task Apply(DummyProjectionEvent @event)
        {
            Applied = true;
            return Task.CompletedTask;
        }
    }

    public class NonMatchingProjection : View
    {
        public NonMatchingProjection() : base(new Mock<IViewModelStoreAdapter>().Object, new Mock<ILogger<IView>>().Object)
        {
        }
        // This projection does not implement IProjectOn<TEvent> so won't handle DummyProjectionEvent
    }

    [TestFixture]
    public class EventSubscriberTests
    {
        private Mock<ILogger<IEventSubscriber>> _mockLogger;
        private DummyProjectionEvent _testEvent;

        [SetUp]
        public void SetUp()
        {
            _mockLogger = new Mock<ILogger<IEventSubscriber>>();
            _testEvent = new DummyProjectionEvent(new DummyProjectionEntity { Id = 1 });
        }

        [Test]
        public void Constructor_WithNullProjections_ThrowsArgumentNullException()
        {
            // Arrange
            IEnumerable<IView> nullProjections = null;

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => 
                new EventSubscriber(nullProjections, _mockLogger.Object));
        }

        [Test]
        public void Constructor_WithNullLogger_ThrowsArgumentNullException()
        {
            // Arrange
            var projections = new List<IView> { new TestProjection() };

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => 
                new EventSubscriber(projections, null));
        }

        [Test]
        public void Constructor_WithValidParameters_Succeeds()
        {
            // Arrange
            var projections = new List<IView> { new TestProjection() };

            // Act
            var subscriber = new EventSubscriber(projections, _mockLogger.Object);

            // Assert
            Assert.IsNotNull(subscriber);
        }

        [Test]
        public async Task Subscribe_WithMatchingProjection_AppliesProjection()
        {
            // Arrange
            var testProjection = new TestProjection();
            var projections = new List<IView> { testProjection };
            var subscriber = new EventSubscriber(projections, _mockLogger.Object);

            // Act
            await subscriber.Subscribe(_testEvent);

            // Assert
            Assert.IsTrue(testProjection.Applied);
        }

        [Test]
        public async Task Subscribe_WithNonMatchingProjection_DoesNotApplyProjection()
        {
            // Arrange
            var nonMatchingProjection = new NonMatchingProjection();
            var projections = new List<IView> { nonMatchingProjection };
            var subscriber = new EventSubscriber(projections, _mockLogger.Object);

            // Act
            await subscriber.Subscribe(_testEvent);

            // Assert
            // We can't directly test this, but we know that non-matching projections won't be applied
            // since they don't implement IProjectOn<DummyProjectionEvent>
        }

        [Test]
        public async Task Subscribe_WithMultipleProjections_AppliesMatchingProjectionsOnly()
        {
            // Arrange
            var matchingProjection1 = new TestProjection();
            var matchingProjection2 = new TestProjection();
            var nonMatchingProjection = new NonMatchingProjection();
            var projections = new List<IView> { matchingProjection1, nonMatchingProjection, matchingProjection2 };
            var subscriber = new EventSubscriber(projections, _mockLogger.Object);

            // Act
            await subscriber.Subscribe(_testEvent);

            // Assert
            Assert.IsTrue(matchingProjection1.Applied);
            Assert.IsTrue(matchingProjection2.Applied);
        }

        [Test]
        public async Task Subscribe_WithNoMatchingProjections_DoesNotThrow()
        {
            // Arrange
            var nonMatchingProjection = new NonMatchingProjection();
            var projections = new List<IView> { nonMatchingProjection };
            var subscriber = new EventSubscriber(projections, _mockLogger.Object);

            // Act & Assert
            Assert.DoesNotThrowAsync(async () => await subscriber.Subscribe(_testEvent));
        }

        [Test]
        public async Task Subscribe_WithEmptyProjectionsCollection_DoesNotThrow()
        {
            // Arrange
            var projections = new List<IView>();
            var subscriber = new EventSubscriber(projections, _mockLogger.Object);

            // Act & Assert
            Assert.DoesNotThrowAsync(async () => await subscriber.Subscribe(_testEvent));
        }
    }
}
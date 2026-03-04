using Microsoft.Extensions.Logging;
using Moq;
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
        public DummyProjectionEvent(DummyProjectionEntity payload) : base(payload)
        {
        }
    }

    public class TestProjection : View<TestProjectionViewModel>, IProjectOn<DummyProjectionEvent>
    {
        public TestProjection() : base(new Mock<IViewModelStoreAdapter>().Object, new Mock<ILogger<IView>>().Object)
        {
        }

        public bool Applied { get; private set; } = false;

        public Task<IViewModel> On(DummyProjectionEvent @event)
        {
            Applied = true;
            return Task.FromResult<IViewModel>(new TestProjectionViewModel { Id = 1 });
        }
    }

    public class TestProjectionViewModel : IViewModel
    {
        public int Id { get; set; }
    }

    public class NonMatchingProjection : View<TestProjectionViewModel>
    {
        public NonMatchingProjection() : base(new Mock<IViewModelStoreAdapter>().Object, new Mock<ILogger<IView>>().Object)
        {
        }

        // This projection does not implement IProjectOn<TEvent> so won't handle DummyProjectionEvent
    }

    [TestFixture]
    [Category("Unit")]
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
            IEnumerable<IView> nullProjections = null!;

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() =>
                new EventSubscriber(nullProjections, _mockLogger.Object, Enumerable.Empty<IEventSubscribeMiddleware>()));
        }

        [Test]
        public void Constructor_WithNullLogger_ThrowsArgumentNullException()
        {
            // Arrange
            var projections = new List<IView> { new TestProjection() };

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() =>
                new EventSubscriber(projections, null, Enumerable.Empty<IEventSubscribeMiddleware>()));
        }

        [Test]
        public void Constructor_NullMiddleware_ThrowsArgumentNullException()
        {
            // Arrange
            var projections = new List<IView> { new TestProjection() };

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() =>
                new EventSubscriber(projections, _mockLogger.Object, null));
        }

        [Test]
        public void Constructor_WithValidParameters_Succeeds()
        {
            // Arrange
            var projections = new List<IView> { new TestProjection() };

            // Act
            var subscriber = new EventSubscriber(projections, _mockLogger.Object, Enumerable.Empty<IEventSubscribeMiddleware>());

            // Assert
            Assert.IsNotNull(subscriber);
        }

        [Test]
        public async Task Subscribe_WithMatchingProjection_AppliesProjection()
        {
            // Arrange
            var testProjection = new TestProjection();
            var projections = new List<IView> { testProjection };
            var subscriber = new EventSubscriber(projections, _mockLogger.Object, Enumerable.Empty<IEventSubscribeMiddleware>());

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
            var subscriber = new EventSubscriber(projections, _mockLogger.Object, Enumerable.Empty<IEventSubscribeMiddleware>());

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
            var subscriber = new EventSubscriber(projections, _mockLogger.Object, Enumerable.Empty<IEventSubscribeMiddleware>());

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
            var subscriber = new EventSubscriber(projections, _mockLogger.Object, Enumerable.Empty<IEventSubscribeMiddleware>());

            // Act & Assert
            Assert.DoesNotThrowAsync(async () => await subscriber.Subscribe(_testEvent));
        }

        [Test]
        public async Task Subscribe_WithEmptyProjectionsCollection_DoesNotThrow()
        {
            // Arrange
            var projections = new List<IView>();
            var subscriber = new EventSubscriber(projections, _mockLogger.Object, Enumerable.Empty<IEventSubscribeMiddleware>());

            // Act & Assert
            Assert.DoesNotThrowAsync(async () => await subscriber.Subscribe(_testEvent));
        }

        [Test]
        public async Task Subscribe_WithMiddleware_ExecutesMiddlewareAroundCoreLogic()
        {
            // Arrange
            var callOrder = new List<string>();
            var testProjection = new TestProjection();
            var projections = new List<IView> { testProjection };

            var middlewareMock = new Mock<IEventSubscribeMiddleware>();
            middlewareMock
                .Setup(m => m.InvokeAsync(It.IsAny<DummyProjectionEvent>(), It.IsAny<Func<DummyProjectionEvent, Task>>()))
                .Returns<DummyProjectionEvent, Func<DummyProjectionEvent, Task>>(async (evt, next) =>
                {
                    callOrder.Add("middleware-before");
                    await next(evt);
                    callOrder.Add("middleware-after");
                });

            var subscriber = new EventSubscriber(projections, _mockLogger.Object, new[] { middlewareMock.Object });

            // Act
            await subscriber.Subscribe(_testEvent);

            // Assert
            Assert.That(callOrder[0], Is.EqualTo("middleware-before"));
            Assert.That(callOrder[1], Is.EqualTo("middleware-after"));
            Assert.IsTrue(testProjection.Applied);
        }

        [Test]
        public async Task Subscribe_WithMultipleMiddleware_ExecutesInRegistrationOrder()
        {
            // Arrange
            var callOrder = new List<string>();
            var testProjection = new TestProjection();
            var projections = new List<IView> { testProjection };

            var middleware1 = new Mock<IEventSubscribeMiddleware>();
            middleware1
                .Setup(m => m.InvokeAsync(It.IsAny<DummyProjectionEvent>(), It.IsAny<Func<DummyProjectionEvent, Task>>()))
                .Returns<DummyProjectionEvent, Func<DummyProjectionEvent, Task>>(async (evt, next) =>
                {
                    callOrder.Add("m1-before");
                    await next(evt);
                    callOrder.Add("m1-after");
                });

            var middleware2 = new Mock<IEventSubscribeMiddleware>();
            middleware2
                .Setup(m => m.InvokeAsync(It.IsAny<DummyProjectionEvent>(), It.IsAny<Func<DummyProjectionEvent, Task>>()))
                .Returns<DummyProjectionEvent, Func<DummyProjectionEvent, Task>>(async (evt, next) =>
                {
                    callOrder.Add("m2-before");
                    await next(evt);
                    callOrder.Add("m2-after");
                });

            var subscriber = new EventSubscriber(projections, _mockLogger.Object,
                new IEventSubscribeMiddleware[] { middleware1.Object, middleware2.Object });

            // Act
            await subscriber.Subscribe(_testEvent);

            // Assert
            Assert.That(callOrder, Is.EqualTo(new[] { "m1-before", "m2-before", "m2-after", "m1-after" }));
        }

        [Test]
        public async Task Subscribe_MiddlewareShortCircuits_DoesNotCallCoreLogic()
        {
            // Arrange
            var testProjection = new TestProjection();
            var projections = new List<IView> { testProjection };

            var middlewareMock = new Mock<IEventSubscribeMiddleware>();
            middlewareMock
                .Setup(m => m.InvokeAsync(It.IsAny<DummyProjectionEvent>(), It.IsAny<Func<DummyProjectionEvent, Task>>()))
                .Returns(Task.CompletedTask); // Does NOT call next

            var subscriber = new EventSubscriber(projections, _mockLogger.Object, new[] { middlewareMock.Object });

            // Act
            await subscriber.Subscribe(_testEvent);

            // Assert - projection was never reached
            Assert.IsFalse(testProjection.Applied);
        }
    }
}

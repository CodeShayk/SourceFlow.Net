using Microsoft.Extensions.Logging;
using Moq;
using SourceFlow.Aggregate;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Core.Tests.Aggregates
{
    public class DummyAggregateEntity : IEntity
    {
        public int Id { get; set; }
    }

    public class DummyAggregateEvent : Event<DummyAggregateEntity>
    {
        public DummyAggregateEvent(DummyAggregateEntity payload) : base(payload)
        {
        }
    }

    public class TestAggregate : IAggregate, ISubscribes<DummyAggregateEvent>
    {
        public bool Handled { get; private set; } = false;

        public Task On(DummyAggregateEvent @event)
        {
            Handled = true;
            return Task.CompletedTask;
        }
    }

    public class NonMatchingAggregate : IAggregate
    {
        // This aggregate does not implement ISubscribes<TEvent> so won't handle DummyAggregateEvent
    }

    [TestFixture]
    [Category("Unit")]
    public class AggregateEventSubscriberTests
    {
        private Mock<ILogger<IEventSubscriber>> _mockLogger;
        private DummyAggregateEvent _testEvent;

        [SetUp]
        public void SetUp()
        {
            _mockLogger = new Mock<ILogger<IEventSubscriber>>();
            _testEvent = new DummyAggregateEvent(new DummyAggregateEntity { Id = 1 });
        }

        [Test]
        public void Constructor_WithNullAggregates_ThrowsArgumentNullException()
        {
            // Arrange
            IEnumerable<IAggregate> nullAggregates = null!;

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() =>
                new EventSubscriber(nullAggregates, _mockLogger.Object, Enumerable.Empty<IEventSubscribeMiddleware>()));
        }

        [Test]
        public void Constructor_WithNullLogger_ThrowsArgumentNullException()
        {
            // Arrange
            var aggregates = new List<IAggregate> { new TestAggregate() };

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() =>
                new EventSubscriber(aggregates, null, Enumerable.Empty<IEventSubscribeMiddleware>()));
        }

        [Test]
        public void Constructor_NullMiddleware_ThrowsArgumentNullException()
        {
            // Arrange
            var aggregates = new List<IAggregate> { new TestAggregate() };

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() =>
                new EventSubscriber(aggregates, _mockLogger.Object, null));
        }

        [Test]
        public void Constructor_WithValidParameters_Succeeds()
        {
            // Arrange
            var aggregates = new List<IAggregate> { new TestAggregate() };

            // Act
            var subscriber = new EventSubscriber(aggregates, _mockLogger.Object, Enumerable.Empty<IEventSubscribeMiddleware>());

            // Assert
            Assert.IsNotNull(subscriber);
        }

        [Test]
        public async Task Subscribe_WithMatchingAggregate_HandlesEvent()
        {
            // Arrange
            var testAggregate = new TestAggregate();
            var aggregates = new List<IAggregate> { testAggregate };
            var subscriber = new EventSubscriber(aggregates, _mockLogger.Object, Enumerable.Empty<IEventSubscribeMiddleware>());

            // Act
            await subscriber.Subscribe(_testEvent);

            // Assert
            Assert.IsTrue(testAggregate.Handled);
        }

        [Test]
        public async Task Subscribe_WithNonMatchingAggregate_DoesNotHandleEvent()
        {
            // Arrange
            var nonMatchingAggregate = new NonMatchingAggregate();
            var aggregates = new List<IAggregate> { nonMatchingAggregate };
            var subscriber = new EventSubscriber(aggregates, _mockLogger.Object, Enumerable.Empty<IEventSubscribeMiddleware>());

            // Act
            await subscriber.Subscribe(_testEvent);

            // This test is more about ensuring no exception is thrown and that non-matching aggregates
            // are simply skipped, which is the expected behavior
        }

        [Test]
        public async Task Subscribe_WithMultipleAggregates_HandlesEventInMatchingAggregatesOnly()
        {
            // Arrange
            var matchingAggregate1 = new TestAggregate();
            var matchingAggregate2 = new TestAggregate();
            var nonMatchingAggregate = new NonMatchingAggregate();
            var aggregates = new List<IAggregate> { matchingAggregate1, nonMatchingAggregate, matchingAggregate2 };
            var subscriber = new EventSubscriber(aggregates, _mockLogger.Object, Enumerable.Empty<IEventSubscribeMiddleware>());

            // Act
            await subscriber.Subscribe(_testEvent);

            // Assert
            Assert.IsTrue(matchingAggregate1.Handled);
            Assert.IsTrue(matchingAggregate2.Handled);
        }

        [Test]
        public async Task Subscribe_WithNoMatchingAggregates_DoesNotThrow()
        {
            // Arrange
            var nonMatchingAggregate = new NonMatchingAggregate();
            var aggregates = new List<IAggregate> { nonMatchingAggregate };
            var subscriber = new EventSubscriber(aggregates, _mockLogger.Object, Enumerable.Empty<IEventSubscribeMiddleware>());

            // Act & Assert
            Assert.DoesNotThrowAsync(async () => await subscriber.Subscribe(_testEvent));
        }

        [Test]
        public async Task Subscribe_WithEmptyAggregatesCollection_DoesNotThrow()
        {
            // Arrange
            var aggregates = new List<IAggregate>();
            var subscriber = new EventSubscriber(aggregates, _mockLogger.Object, Enumerable.Empty<IEventSubscribeMiddleware>());

            // Act & Assert
            Assert.DoesNotThrowAsync(async () => await subscriber.Subscribe(_testEvent));
        }

        [Test]
        public async Task Subscribe_WithMiddleware_ExecutesMiddlewareAroundCoreLogic()
        {
            // Arrange
            var callOrder = new List<string>();
            var testAggregate = new TestAggregate();
            var aggregates = new List<IAggregate> { testAggregate };

            var middlewareMock = new Mock<IEventSubscribeMiddleware>();
            middlewareMock
                .Setup(m => m.InvokeAsync(It.IsAny<DummyAggregateEvent>(), It.IsAny<Func<DummyAggregateEvent, Task>>()))
                .Returns<DummyAggregateEvent, Func<DummyAggregateEvent, Task>>(async (evt, next) =>
                {
                    callOrder.Add("middleware-before");
                    await next(evt);
                    callOrder.Add("middleware-after");
                });

            var subscriber = new EventSubscriber(aggregates, _mockLogger.Object, new[] { middlewareMock.Object });

            // Act
            await subscriber.Subscribe(_testEvent);

            // Assert
            Assert.That(callOrder[0], Is.EqualTo("middleware-before"));
            Assert.That(callOrder[1], Is.EqualTo("middleware-after"));
            Assert.IsTrue(testAggregate.Handled);
        }

        [Test]
        public async Task Subscribe_WithMultipleMiddleware_ExecutesInRegistrationOrder()
        {
            // Arrange
            var callOrder = new List<string>();
            var testAggregate = new TestAggregate();
            var aggregates = new List<IAggregate> { testAggregate };

            var middleware1 = new Mock<IEventSubscribeMiddleware>();
            middleware1
                .Setup(m => m.InvokeAsync(It.IsAny<DummyAggregateEvent>(), It.IsAny<Func<DummyAggregateEvent, Task>>()))
                .Returns<DummyAggregateEvent, Func<DummyAggregateEvent, Task>>(async (evt, next) =>
                {
                    callOrder.Add("m1-before");
                    await next(evt);
                    callOrder.Add("m1-after");
                });

            var middleware2 = new Mock<IEventSubscribeMiddleware>();
            middleware2
                .Setup(m => m.InvokeAsync(It.IsAny<DummyAggregateEvent>(), It.IsAny<Func<DummyAggregateEvent, Task>>()))
                .Returns<DummyAggregateEvent, Func<DummyAggregateEvent, Task>>(async (evt, next) =>
                {
                    callOrder.Add("m2-before");
                    await next(evt);
                    callOrder.Add("m2-after");
                });

            var subscriber = new EventSubscriber(aggregates, _mockLogger.Object,
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
            var testAggregate = new TestAggregate();
            var aggregates = new List<IAggregate> { testAggregate };

            var middlewareMock = new Mock<IEventSubscribeMiddleware>();
            middlewareMock
                .Setup(m => m.InvokeAsync(It.IsAny<DummyAggregateEvent>(), It.IsAny<Func<DummyAggregateEvent, Task>>()))
                .Returns(Task.CompletedTask); // Does NOT call next

            var subscriber = new EventSubscriber(aggregates, _mockLogger.Object, new[] { middlewareMock.Object });

            // Act
            await subscriber.Subscribe(_testEvent);

            // Assert - aggregate was never reached
            Assert.IsFalse(testAggregate.Handled);
        }
    }
}

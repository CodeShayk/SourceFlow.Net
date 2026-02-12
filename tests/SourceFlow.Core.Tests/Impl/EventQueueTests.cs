using Microsoft.Extensions.Logging;
using Moq;
using SourceFlow.Messaging.Events;
using SourceFlow.Messaging.Events.Impl;
using SourceFlow.Observability;

namespace SourceFlow.Core.Tests.Impl
{
    [TestFixture]
    public class EventQueueTests
    {
        private Mock<ILogger<IEventQueue>> loggerMock;
        private Mock<IEventDispatcher> eventDispatcherMock;
        private Mock<IDomainTelemetryService> telemetryMock;
        private EventQueue eventQueue;

        [SetUp]
        public void Setup()
        {
            loggerMock = new Mock<ILogger<IEventQueue>>();
            eventDispatcherMock = new Mock<IEventDispatcher>();
            telemetryMock = new Mock<IDomainTelemetryService>();

            // Setup telemetry mock to execute operations directly
            telemetryMock.Setup(t => t.TraceAsync(It.IsAny<string>(), It.IsAny<Func<Task>>(), It.IsAny<Action<System.Diagnostics.Activity>>()))
                .Returns((string name, Func<Task> operation, Action<System.Diagnostics.Activity> enrich) => operation());

            eventQueue = new EventQueue(
                new[] { eventDispatcherMock.Object },
                loggerMock.Object,
                telemetryMock.Object,
                Enumerable.Empty<IEventDispatchMiddleware>());
        }

        [Test]
        public void Constructor_NullLogger_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new EventQueue(new[] { eventDispatcherMock.Object }, null, telemetryMock.Object, Enumerable.Empty<IEventDispatchMiddleware>()));
        }

        [Test]
        public void Constructor_NullEventDispatcher_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new EventQueue(null, loggerMock.Object, telemetryMock.Object, Enumerable.Empty<IEventDispatchMiddleware>()));
        }

        [Test]
        public void Constructor_NullMiddleware_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new EventQueue(new[] { eventDispatcherMock.Object }, loggerMock.Object, telemetryMock.Object, null));
        }

        [Test]
        public void Constructor_SetsDependencies()
        {
            Assert.That(eventQueue.eventDispatchers.ElementAt(0), Is.EqualTo(eventDispatcherMock.Object));
        }

        [Test]
        public async Task Enqueue_NullEvent_ThrowsArgumentNullException()
        {
            Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await eventQueue.Enqueue<DummyEvent>(null!));
        }

        [Test]
        public async Task Enqueue_ValidEvent_DispatchesToEventDispatcher()
        {
            // Arrange
            var @event = new DummyEvent();

            // Act
            await eventQueue.Enqueue(@event);

            // Assert
            eventDispatcherMock.Verify(ed => ed.Dispatch<DummyEvent>(@event), Times.Once);
        }

        [Test]
        public async Task Enqueue_ValidEvent_LogsInformation()
        {
            // Arrange
            var @event = new DummyEvent();

            // Act
            await eventQueue.Enqueue(@event);

            // Assert
            loggerMock.Verify(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()),
                Times.AtLeastOnce);
        }

        [Test]
        public async Task Enqueue_ValidEvent_DispatchesAfterLogging()
        {
            // Arrange
            var @event = new DummyEvent();
            var callSequence = new System.Collections.Generic.List<string>();

            eventDispatcherMock.Setup(ed => ed.Dispatch(It.IsAny<DummyEvent>()))
                .Callback(() => callSequence.Add("Dispatch"))
                .Returns(Task.CompletedTask);

            loggerMock.Setup(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
                .Callback(() => callSequence.Add("Log"));

            // Act
            await eventQueue.Enqueue(@event);

            // Assert
            Assert.That(callSequence[0], Is.EqualTo("Log"));
            Assert.That(callSequence[1], Is.EqualTo("Dispatch"));
        }

        [Test]
        public async Task Enqueue_MultipleEvents_DispatchesAll()
        {
            // Arrange
            var event1 = new DummyEvent();
            var event2 = new DummyEvent();
            var event3 = new DummyEvent();

            // Act
            await eventQueue.Enqueue(event1);
            await eventQueue.Enqueue(event2);
            await eventQueue.Enqueue(event3);

            // Assert
            eventDispatcherMock.Verify(ed => ed.Dispatch(It.IsAny<DummyEvent>()), Times.Exactly(3));
        }

        [Test]
        public async Task Enqueue_WithMiddleware_ExecutesMiddlewareAroundCoreLogic()
        {
            // Arrange
            var callOrder = new List<string>();
            var middlewareMock = new Mock<IEventDispatchMiddleware>();
            middlewareMock
                .Setup(m => m.InvokeAsync(It.IsAny<DummyEvent>(), It.IsAny<Func<DummyEvent, Task>>()))
                .Returns<DummyEvent, Func<DummyEvent, Task>>(async (evt, next) =>
                {
                    callOrder.Add("middleware-before");
                    await next(evt);
                    callOrder.Add("middleware-after");
                });

            eventDispatcherMock.Setup(ed => ed.Dispatch(It.IsAny<DummyEvent>()))
                .Callback(() => callOrder.Add("dispatch"))
                .Returns(Task.CompletedTask);

            var queue = new EventQueue(
                new[] { eventDispatcherMock.Object },
                loggerMock.Object,
                telemetryMock.Object,
                new[] { middlewareMock.Object });

            // Act
            await queue.Enqueue(new DummyEvent());

            // Assert
            Assert.That(callOrder[0], Is.EqualTo("middleware-before"));
            Assert.That(callOrder[1], Is.EqualTo("dispatch"));
            Assert.That(callOrder[2], Is.EqualTo("middleware-after"));
        }

        [Test]
        public async Task Enqueue_WithMultipleMiddleware_ExecutesInRegistrationOrder()
        {
            // Arrange
            var callOrder = new List<string>();

            var middleware1 = new Mock<IEventDispatchMiddleware>();
            middleware1
                .Setup(m => m.InvokeAsync(It.IsAny<DummyEvent>(), It.IsAny<Func<DummyEvent, Task>>()))
                .Returns<DummyEvent, Func<DummyEvent, Task>>(async (evt, next) =>
                {
                    callOrder.Add("m1-before");
                    await next(evt);
                    callOrder.Add("m1-after");
                });

            var middleware2 = new Mock<IEventDispatchMiddleware>();
            middleware2
                .Setup(m => m.InvokeAsync(It.IsAny<DummyEvent>(), It.IsAny<Func<DummyEvent, Task>>()))
                .Returns<DummyEvent, Func<DummyEvent, Task>>(async (evt, next) =>
                {
                    callOrder.Add("m2-before");
                    await next(evt);
                    callOrder.Add("m2-after");
                });

            var queue = new EventQueue(
                new[] { eventDispatcherMock.Object },
                loggerMock.Object,
                telemetryMock.Object,
                new IEventDispatchMiddleware[] { middleware1.Object, middleware2.Object });

            // Act
            await queue.Enqueue(new DummyEvent());

            // Assert
            Assert.That(callOrder, Is.EqualTo(new[] { "m1-before", "m2-before", "m2-after", "m1-after" }));
        }

        [Test]
        public async Task Enqueue_MiddlewareShortCircuits_DoesNotCallCoreLogic()
        {
            // Arrange
            var middlewareMock = new Mock<IEventDispatchMiddleware>();
            middlewareMock
                .Setup(m => m.InvokeAsync(It.IsAny<DummyEvent>(), It.IsAny<Func<DummyEvent, Task>>()))
                .Returns(Task.CompletedTask); // Does NOT call next

            var queue = new EventQueue(
                new[] { eventDispatcherMock.Object },
                loggerMock.Object,
                telemetryMock.Object,
                new[] { middlewareMock.Object });

            // Act
            await queue.Enqueue(new DummyEvent());

            // Assert
            eventDispatcherMock.Verify(ed => ed.Dispatch(It.IsAny<DummyEvent>()), Times.Never);
        }

        [Test]
        public async Task Enqueue_NoMiddleware_ExecutesCoreLogicDirectly()
        {
            // Arrange
            var @event = new DummyEvent();

            // Act
            await eventQueue.Enqueue(@event);

            // Assert
            eventDispatcherMock.Verify(ed => ed.Dispatch(@event), Times.Once);
        }
    }
}

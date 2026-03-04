using Microsoft.Extensions.Logging;
using Moq;
using SourceFlow.Messaging.Events;
using SourceFlow.Messaging.Events.Impl;
using SourceFlow.Observability;
using SourceFlow.Core.Tests.Impl;

namespace SourceFlow.Core.Tests.Middleware
{
    [TestFixture]
    [Category("Unit")]
    public class EventDispatchMiddlewareTests
    {
        private Mock<ILogger<IEventQueue>> loggerMock;
        private Mock<IEventDispatcher> eventDispatcherMock;
        private Mock<IDomainTelemetryService> telemetryMock;

        [SetUp]
        public void Setup()
        {
            loggerMock = new Mock<ILogger<IEventQueue>>();
            eventDispatcherMock = new Mock<IEventDispatcher>();
            telemetryMock = new Mock<IDomainTelemetryService>();

            telemetryMock.Setup(t => t.TraceAsync(It.IsAny<string>(), It.IsAny<Func<Task>>(), It.IsAny<Action<System.Diagnostics.Activity>>()))
                .Returns((string name, Func<Task> operation, Action<System.Diagnostics.Activity> enrich) => operation());
        }

        private EventQueue CreateQueue(params IEventDispatchMiddleware[] middlewares)
        {
            return new EventQueue(
                new[] { eventDispatcherMock.Object },
                loggerMock.Object,
                telemetryMock.Object,
                middlewares);
        }

        [Test]
        public async Task Middleware_ReceivesSameEventInstance()
        {
            // Arrange
            DummyEvent capturedEvent = null;
            var middleware = new Mock<IEventDispatchMiddleware>();
            middleware
                .Setup(m => m.InvokeAsync(It.IsAny<DummyEvent>(), It.IsAny<Func<DummyEvent, Task>>()))
                .Returns<DummyEvent, Func<DummyEvent, Task>>(async (evt, next) =>
                {
                    capturedEvent = evt;
                    await next(evt);
                });

            var queue = CreateQueue(middleware.Object);
            var @event = new DummyEvent();

            // Act
            await queue.Enqueue(@event);

            // Assert
            Assert.That(capturedEvent, Is.SameAs(@event));
        }

        [Test]
        public async Task ThreeMiddleware_ExecuteInCorrectNestingOrder()
        {
            // Arrange
            var callOrder = new List<string>();

            var m1 = CreateTracingMiddleware(callOrder, "m1");
            var m2 = CreateTracingMiddleware(callOrder, "m2");
            var m3 = CreateTracingMiddleware(callOrder, "m3");

            var queue = CreateQueue(m1, m2, m3);

            // Act
            await queue.Enqueue(new DummyEvent());

            // Assert
            Assert.That(callOrder, Is.EqualTo(new[]
            {
                "m1-before", "m2-before", "m3-before",
                "m3-after", "m2-after", "m1-after"
            }));
        }

        [Test]
        public async Task SecondMiddleware_ShortCircuits_ThirdNeverCalled()
        {
            // Arrange
            var callOrder = new List<string>();
            var m1 = CreateTracingMiddleware(callOrder, "m1");

            var m2 = new Mock<IEventDispatchMiddleware>();
            m2.Setup(m => m.InvokeAsync(It.IsAny<DummyEvent>(), It.IsAny<Func<DummyEvent, Task>>()))
                .Returns<DummyEvent, Func<DummyEvent, Task>>((evt, next) =>
                {
                    callOrder.Add("m2-shortcircuit");
                    return Task.CompletedTask;
                });

            var m3 = CreateTracingMiddleware(callOrder, "m3");

            var queue = CreateQueue(m1, m2.Object, m3);

            // Act
            await queue.Enqueue(new DummyEvent());

            // Assert
            Assert.That(callOrder, Is.EqualTo(new[] { "m1-before", "m2-shortcircuit", "m1-after" }));
            eventDispatcherMock.Verify(ed => ed.Dispatch(It.IsAny<DummyEvent>()), Times.Never);
        }

        [Test]
        public async Task Middleware_ExceptionPropagates()
        {
            // Arrange
            var middleware = new Mock<IEventDispatchMiddleware>();
            middleware
                .Setup(m => m.InvokeAsync(It.IsAny<DummyEvent>(), It.IsAny<Func<DummyEvent, Task>>()))
                .ThrowsAsync(new InvalidOperationException("middleware error"));

            var queue = CreateQueue(middleware.Object);

            // Act & Assert
            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await queue.Enqueue(new DummyEvent()));
            Assert.That(ex.Message, Is.EqualTo("middleware error"));
        }

        [Test]
        public async Task Middleware_CanCatchAndHandleExceptionFromNext()
        {
            // Arrange
            Exception caughtException = null;

            eventDispatcherMock
                .Setup(ed => ed.Dispatch(It.IsAny<DummyEvent>()))
                .ThrowsAsync(new InvalidOperationException("dispatch error"));

            var middleware = new Mock<IEventDispatchMiddleware>();
            middleware
                .Setup(m => m.InvokeAsync(It.IsAny<DummyEvent>(), It.IsAny<Func<DummyEvent, Task>>()))
                .Returns<DummyEvent, Func<DummyEvent, Task>>(async (evt, next) =>
                {
                    try
                    {
                        await next(evt);
                    }
                    catch (Exception ex)
                    {
                        caughtException = ex;
                    }
                });

            var queue = CreateQueue(middleware.Object);

            // Act
            await queue.Enqueue(new DummyEvent());

            // Assert
            Assert.That(caughtException, Is.Not.Null);
            Assert.That(caughtException.Message, Is.EqualTo("dispatch error"));
        }

        [Test]
        public async Task Middleware_CanModifyEventMetadataBeforeNext()
        {
            // Arrange
            var middleware = new Mock<IEventDispatchMiddleware>();
            middleware
                .Setup(m => m.InvokeAsync(It.IsAny<DummyEvent>(), It.IsAny<Func<DummyEvent, Task>>()))
                .Returns<DummyEvent, Func<DummyEvent, Task>>(async (evt, next) =>
                {
                    evt.Metadata.Properties = new Dictionary<string, object> { { "enriched", true } };
                    await next(evt);
                });

            DummyEvent dispatchedEvent = null;
            eventDispatcherMock
                .Setup(ed => ed.Dispatch(It.IsAny<DummyEvent>()))
                .Callback<DummyEvent>(evt => dispatchedEvent = evt)
                .Returns(Task.CompletedTask);

            var queue = CreateQueue(middleware.Object);
            var @event = new DummyEvent();

            // Act
            await queue.Enqueue(@event);

            // Assert
            Assert.That(dispatchedEvent.Metadata.Properties.ContainsKey("enriched"), Is.True);
        }

        [Test]
        public async Task Middleware_CallingNextTwice_DispatchesTwice()
        {
            // Arrange
            var middleware = new Mock<IEventDispatchMiddleware>();
            middleware
                .Setup(m => m.InvokeAsync(It.IsAny<DummyEvent>(), It.IsAny<Func<DummyEvent, Task>>()))
                .Returns<DummyEvent, Func<DummyEvent, Task>>(async (evt, next) =>
                {
                    await next(evt);
                    await next(evt);
                });

            var queue = CreateQueue(middleware.Object);

            // Act
            await queue.Enqueue(new DummyEvent());

            // Assert
            eventDispatcherMock.Verify(ed => ed.Dispatch(It.IsAny<DummyEvent>()), Times.Exactly(2));
        }

        private IEventDispatchMiddleware CreateTracingMiddleware(List<string> callOrder, string name)
        {
            var mock = new Mock<IEventDispatchMiddleware>();
            mock.Setup(m => m.InvokeAsync(It.IsAny<DummyEvent>(), It.IsAny<Func<DummyEvent, Task>>()))
                .Returns<DummyEvent, Func<DummyEvent, Task>>(async (evt, next) =>
                {
                    callOrder.Add($"{name}-before");
                    await next(evt);
                    callOrder.Add($"{name}-after");
                });
            return mock.Object;
        }
    }
}

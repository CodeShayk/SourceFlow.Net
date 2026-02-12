using Microsoft.Extensions.Logging;
using Moq;
using SourceFlow.Aggregate;
using SourceFlow.Messaging.Events;
using SourceFlow.Projections;

namespace SourceFlow.Core.Tests.Middleware
{
    public class MiddlewareTestEntity : IEntity
    {
        public int Id { get; set; }
    }

    public class MiddlewareTestEvent : Event<MiddlewareTestEntity>
    {
        public MiddlewareTestEvent(MiddlewareTestEntity payload) : base(payload)
        {
        }
    }

    public class MiddlewareTestAggregate : IAggregate, ISubscribes<MiddlewareTestEvent>
    {
        public bool Handled { get; private set; } = false;

        public Task On(MiddlewareTestEvent @event)
        {
            Handled = true;
            return Task.CompletedTask;
        }
    }

    public class MiddlewareTestViewModel : IViewModel
    {
        public int Id { get; set; }
    }

    public class MiddlewareTestProjection : View<MiddlewareTestViewModel>, IProjectOn<MiddlewareTestEvent>
    {
        public MiddlewareTestProjection() : base(new Mock<IViewModelStoreAdapter>().Object, new Mock<ILogger<IView>>().Object)
        {
        }

        public bool Applied { get; private set; } = false;

        public Task<IViewModel> On(MiddlewareTestEvent @event)
        {
            Applied = true;
            return Task.FromResult<IViewModel>(new MiddlewareTestViewModel { Id = 1 });
        }
    }

    [TestFixture]
    public class AggregateEventSubscribeMiddlewareTests
    {
        private Mock<ILogger<IEventSubscriber>> loggerMock;
        private MiddlewareTestEvent testEvent;

        [SetUp]
        public void Setup()
        {
            loggerMock = new Mock<ILogger<IEventSubscriber>>();
            testEvent = new MiddlewareTestEvent(new MiddlewareTestEntity { Id = 1 });
        }

        private Aggregate.EventSubscriber CreateSubscriber(IEnumerable<IAggregate> aggregates, params IEventSubscribeMiddleware[] middlewares)
        {
            return new Aggregate.EventSubscriber(aggregates.ToList(), loggerMock.Object, middlewares);
        }

        [Test]
        public async Task Middleware_ReceivesSameEventInstance()
        {
            // Arrange
            MiddlewareTestEvent capturedEvent = null;
            var middleware = new Mock<IEventSubscribeMiddleware>();
            middleware
                .Setup(m => m.InvokeAsync(It.IsAny<MiddlewareTestEvent>(), It.IsAny<Func<MiddlewareTestEvent, Task>>()))
                .Returns<MiddlewareTestEvent, Func<MiddlewareTestEvent, Task>>(async (evt, next) =>
                {
                    capturedEvent = evt;
                    await next(evt);
                });

            var subscriber = CreateSubscriber(new[] { new MiddlewareTestAggregate() }, middleware.Object);

            // Act
            await subscriber.Subscribe(testEvent);

            // Assert
            Assert.That(capturedEvent, Is.SameAs(testEvent));
        }

        [Test]
        public async Task ThreeMiddleware_ExecuteInCorrectNestingOrder()
        {
            // Arrange
            var callOrder = new List<string>();
            var aggregate = new MiddlewareTestAggregate();

            var m1 = CreateTracingMiddleware(callOrder, "m1");
            var m2 = CreateTracingMiddleware(callOrder, "m2");
            var m3 = CreateTracingMiddleware(callOrder, "m3");

            var subscriber = CreateSubscriber(new[] { aggregate }, m1, m2, m3);

            // Act
            await subscriber.Subscribe(testEvent);

            // Assert
            Assert.That(callOrder, Is.EqualTo(new[]
            {
                "m1-before", "m2-before", "m3-before",
                "m3-after", "m2-after", "m1-after"
            }));
            Assert.That(aggregate.Handled, Is.True);
        }

        [Test]
        public async Task SecondMiddleware_ShortCircuits_ThirdNeverCalledAndAggregateNotHandled()
        {
            // Arrange
            var callOrder = new List<string>();
            var aggregate = new MiddlewareTestAggregate();

            var m1 = CreateTracingMiddleware(callOrder, "m1");

            var m2 = new Mock<IEventSubscribeMiddleware>();
            m2.Setup(m => m.InvokeAsync(It.IsAny<MiddlewareTestEvent>(), It.IsAny<Func<MiddlewareTestEvent, Task>>()))
                .Returns<MiddlewareTestEvent, Func<MiddlewareTestEvent, Task>>((evt, next) =>
                {
                    callOrder.Add("m2-shortcircuit");
                    return Task.CompletedTask;
                });

            var m3 = CreateTracingMiddleware(callOrder, "m3");

            var subscriber = CreateSubscriber(new[] { aggregate }, m1, m2.Object, m3);

            // Act
            await subscriber.Subscribe(testEvent);

            // Assert
            Assert.That(callOrder, Is.EqualTo(new[] { "m1-before", "m2-shortcircuit", "m1-after" }));
            Assert.That(aggregate.Handled, Is.False);
        }

        [Test]
        public async Task Middleware_ExceptionPropagates()
        {
            // Arrange
            var middleware = new Mock<IEventSubscribeMiddleware>();
            middleware
                .Setup(m => m.InvokeAsync(It.IsAny<MiddlewareTestEvent>(), It.IsAny<Func<MiddlewareTestEvent, Task>>()))
                .ThrowsAsync(new InvalidOperationException("middleware error"));

            var subscriber = CreateSubscriber(new[] { new MiddlewareTestAggregate() }, middleware.Object);

            // Act & Assert
            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await subscriber.Subscribe(testEvent));
            Assert.That(ex.Message, Is.EqualTo("middleware error"));
        }

        [Test]
        public async Task Middleware_CanCatchAndHandleExceptionFromAggregate()
        {
            // Arrange
            Exception caughtException = null;
            var throwingAggregate = new ThrowingTestAggregate();

            var middleware = new Mock<IEventSubscribeMiddleware>();
            middleware
                .Setup(m => m.InvokeAsync(It.IsAny<MiddlewareTestEvent>(), It.IsAny<Func<MiddlewareTestEvent, Task>>()))
                .Returns<MiddlewareTestEvent, Func<MiddlewareTestEvent, Task>>(async (evt, next) =>
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

            var subscriber = CreateSubscriber(new IAggregate[] { throwingAggregate }, middleware.Object);

            // Act
            await subscriber.Subscribe(testEvent);

            // Assert
            Assert.That(caughtException, Is.Not.Null);
            Assert.That(caughtException.Message, Is.EqualTo("aggregate error"));
        }

        [Test]
        public async Task Middleware_WithEmptyAggregates_StillExecutes()
        {
            // Arrange
            var middlewareCalled = false;
            var middleware = new Mock<IEventSubscribeMiddleware>();
            middleware
                .Setup(m => m.InvokeAsync(It.IsAny<MiddlewareTestEvent>(), It.IsAny<Func<MiddlewareTestEvent, Task>>()))
                .Returns<MiddlewareTestEvent, Func<MiddlewareTestEvent, Task>>(async (evt, next) =>
                {
                    middlewareCalled = true;
                    await next(evt);
                });

            var subscriber = CreateSubscriber(Enumerable.Empty<IAggregate>(), middleware.Object);

            // Act
            await subscriber.Subscribe(testEvent);

            // Assert
            Assert.That(middlewareCalled, Is.True);
        }

        private IEventSubscribeMiddleware CreateTracingMiddleware(List<string> callOrder, string name)
        {
            var mock = new Mock<IEventSubscribeMiddleware>();
            mock.Setup(m => m.InvokeAsync(It.IsAny<MiddlewareTestEvent>(), It.IsAny<Func<MiddlewareTestEvent, Task>>()))
                .Returns<MiddlewareTestEvent, Func<MiddlewareTestEvent, Task>>(async (evt, next) =>
                {
                    callOrder.Add($"{name}-before");
                    await next(evt);
                    callOrder.Add($"{name}-after");
                });
            return mock.Object;
        }
    }

    public class ThrowingTestAggregate : IAggregate, ISubscribes<MiddlewareTestEvent>
    {
        public Task On(MiddlewareTestEvent @event)
        {
            throw new InvalidOperationException("aggregate error");
        }
    }

    [TestFixture]
    public class ProjectionEventSubscribeMiddlewareTests
    {
        private Mock<ILogger<IEventSubscriber>> loggerMock;
        private MiddlewareTestEvent testEvent;

        [SetUp]
        public void Setup()
        {
            loggerMock = new Mock<ILogger<IEventSubscriber>>();
            testEvent = new MiddlewareTestEvent(new MiddlewareTestEntity { Id = 1 });
        }

        private SourceFlow.Projections.EventSubscriber CreateSubscriber(IEnumerable<IView> views, params IEventSubscribeMiddleware[] middlewares)
        {
            return new SourceFlow.Projections.EventSubscriber(views.ToList(), loggerMock.Object, middlewares);
        }

        [Test]
        public async Task Middleware_ReceivesSameEventInstance()
        {
            // Arrange
            MiddlewareTestEvent capturedEvent = null;
            var middleware = new Mock<IEventSubscribeMiddleware>();
            middleware
                .Setup(m => m.InvokeAsync(It.IsAny<MiddlewareTestEvent>(), It.IsAny<Func<MiddlewareTestEvent, Task>>()))
                .Returns<MiddlewareTestEvent, Func<MiddlewareTestEvent, Task>>(async (evt, next) =>
                {
                    capturedEvent = evt;
                    await next(evt);
                });

            var subscriber = CreateSubscriber(new IView[] { new MiddlewareTestProjection() }, middleware.Object);

            // Act
            await subscriber.Subscribe(testEvent);

            // Assert
            Assert.That(capturedEvent, Is.SameAs(testEvent));
        }

        [Test]
        public async Task ThreeMiddleware_ExecuteInCorrectNestingOrder()
        {
            // Arrange
            var callOrder = new List<string>();
            var projection = new MiddlewareTestProjection();

            var m1 = CreateTracingMiddleware(callOrder, "m1");
            var m2 = CreateTracingMiddleware(callOrder, "m2");
            var m3 = CreateTracingMiddleware(callOrder, "m3");

            var subscriber = CreateSubscriber(new IView[] { projection }, m1, m2, m3);

            // Act
            await subscriber.Subscribe(testEvent);

            // Assert
            Assert.That(callOrder, Is.EqualTo(new[]
            {
                "m1-before", "m2-before", "m3-before",
                "m3-after", "m2-after", "m1-after"
            }));
            Assert.That(projection.Applied, Is.True);
        }

        [Test]
        public async Task SecondMiddleware_ShortCircuits_ThirdNeverCalledAndProjectionNotApplied()
        {
            // Arrange
            var callOrder = new List<string>();
            var projection = new MiddlewareTestProjection();

            var m1 = CreateTracingMiddleware(callOrder, "m1");

            var m2 = new Mock<IEventSubscribeMiddleware>();
            m2.Setup(m => m.InvokeAsync(It.IsAny<MiddlewareTestEvent>(), It.IsAny<Func<MiddlewareTestEvent, Task>>()))
                .Returns<MiddlewareTestEvent, Func<MiddlewareTestEvent, Task>>((evt, next) =>
                {
                    callOrder.Add("m2-shortcircuit");
                    return Task.CompletedTask;
                });

            var m3 = CreateTracingMiddleware(callOrder, "m3");

            var subscriber = CreateSubscriber(new IView[] { projection }, m1, m2.Object, m3);

            // Act
            await subscriber.Subscribe(testEvent);

            // Assert
            Assert.That(callOrder, Is.EqualTo(new[] { "m1-before", "m2-shortcircuit", "m1-after" }));
            Assert.That(projection.Applied, Is.False);
        }

        [Test]
        public async Task Middleware_ExceptionPropagates()
        {
            // Arrange
            var middleware = new Mock<IEventSubscribeMiddleware>();
            middleware
                .Setup(m => m.InvokeAsync(It.IsAny<MiddlewareTestEvent>(), It.IsAny<Func<MiddlewareTestEvent, Task>>()))
                .ThrowsAsync(new InvalidOperationException("middleware error"));

            var subscriber = CreateSubscriber(new IView[] { new MiddlewareTestProjection() }, middleware.Object);

            // Act & Assert
            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await subscriber.Subscribe(testEvent));
            Assert.That(ex.Message, Is.EqualTo("middleware error"));
        }

        [Test]
        public async Task Middleware_WithEmptyViews_StillExecutes()
        {
            // Arrange
            var middlewareCalled = false;
            var middleware = new Mock<IEventSubscribeMiddleware>();
            middleware
                .Setup(m => m.InvokeAsync(It.IsAny<MiddlewareTestEvent>(), It.IsAny<Func<MiddlewareTestEvent, Task>>()))
                .Returns<MiddlewareTestEvent, Func<MiddlewareTestEvent, Task>>(async (evt, next) =>
                {
                    middlewareCalled = true;
                    await next(evt);
                });

            var subscriber = CreateSubscriber(Enumerable.Empty<IView>(), middleware.Object);

            // Act
            await subscriber.Subscribe(testEvent);

            // Assert
            Assert.That(middlewareCalled, Is.True);
        }

        [Test]
        public async Task Middleware_CanCatchAndHandleExceptionFromProjection()
        {
            // Arrange
            Exception caughtException = null;
            var throwingProjection = new ThrowingTestProjection();

            var middleware = new Mock<IEventSubscribeMiddleware>();
            middleware
                .Setup(m => m.InvokeAsync(It.IsAny<MiddlewareTestEvent>(), It.IsAny<Func<MiddlewareTestEvent, Task>>()))
                .Returns<MiddlewareTestEvent, Func<MiddlewareTestEvent, Task>>(async (evt, next) =>
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

            var subscriber = CreateSubscriber(new IView[] { throwingProjection }, middleware.Object);

            // Act
            await subscriber.Subscribe(testEvent);

            // Assert
            Assert.That(caughtException, Is.Not.Null);
            Assert.That(caughtException.Message, Is.EqualTo("projection error"));
        }

        private IEventSubscribeMiddleware CreateTracingMiddleware(List<string> callOrder, string name)
        {
            var mock = new Mock<IEventSubscribeMiddleware>();
            mock.Setup(m => m.InvokeAsync(It.IsAny<MiddlewareTestEvent>(), It.IsAny<Func<MiddlewareTestEvent, Task>>()))
                .Returns<MiddlewareTestEvent, Func<MiddlewareTestEvent, Task>>(async (evt, next) =>
                {
                    callOrder.Add($"{name}-before");
                    await next(evt);
                    callOrder.Add($"{name}-after");
                });
            return mock.Object;
        }
    }

    public class ThrowingTestProjection : View<MiddlewareTestViewModel>, IProjectOn<MiddlewareTestEvent>
    {
        public ThrowingTestProjection() : base(new Mock<IViewModelStoreAdapter>().Object, new Mock<ILogger<IView>>().Object)
        {
        }

        public Task<IViewModel> On(MiddlewareTestEvent @event)
        {
            throw new InvalidOperationException("projection error");
        }
    }
}

using Microsoft.Extensions.Logging;
using Moq;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;
using SourceFlow.Saga;

namespace SourceFlow.Core.Tests.Middleware
{
    public class MiddlewareTestCommand : Command<MiddlewareTestPayload>
    {
        public MiddlewareTestCommand(MiddlewareTestPayload payload) : base(true, payload)
        {
        }
    }

    public class MiddlewareTestPayload : IPayload
    {
        public int Id { get; set; }
    }

    public class MiddlewareTestSaga : ISaga, IHandles<MiddlewareTestCommand>
    {
        public bool Handled { get; private set; } = false;

        public Task Handle<TCommand>(TCommand command) where TCommand : ICommand
        {
            if (this is IHandles<TCommand>)
                Handled = true;
            return Task.CompletedTask;
        }

        public Task<IEntity> Handle(IEntity entity, MiddlewareTestCommand command)
        {
            Handled = true;
            return Task.FromResult(entity);
        }
    }

    [TestFixture]
    [Category("Unit")]
    public class CommandSubscribeMiddlewareTests
    {
        private Mock<ILogger<ICommandSubscriber>> loggerMock;
        private MiddlewareTestCommand testCommand;

        [SetUp]
        public void Setup()
        {
            loggerMock = new Mock<ILogger<ICommandSubscriber>>();
            testCommand = new MiddlewareTestCommand(new MiddlewareTestPayload { Id = 1 });
        }

        private CommandSubscriber CreateSubscriber(IEnumerable<ISaga> sagas, params ICommandSubscribeMiddleware[] middlewares)
        {
            return new CommandSubscriber(sagas.ToList(), loggerMock.Object, middlewares);
        }

        [Test]
        public async Task Middleware_ReceivesSameCommandInstance()
        {
            // Arrange
            MiddlewareTestCommand capturedCommand = null;
            var middleware = new Mock<ICommandSubscribeMiddleware>();
            middleware
                .Setup(m => m.InvokeAsync(It.IsAny<MiddlewareTestCommand>(), It.IsAny<Func<MiddlewareTestCommand, Task>>()))
                .Returns<MiddlewareTestCommand, Func<MiddlewareTestCommand, Task>>(async (cmd, next) =>
                {
                    capturedCommand = cmd;
                    await next(cmd);
                });

            var subscriber = CreateSubscriber(new[] { new MiddlewareTestSaga() }, middleware.Object);

            // Act
            await subscriber.Subscribe(testCommand);

            // Assert
            Assert.That(capturedCommand, Is.SameAs(testCommand));
        }

        [Test]
        public async Task ThreeMiddleware_ExecuteInCorrectNestingOrder()
        {
            // Arrange
            var callOrder = new List<string>();
            var saga = new MiddlewareTestSaga();

            var m1 = CreateTracingMiddleware(callOrder, "m1");
            var m2 = CreateTracingMiddleware(callOrder, "m2");
            var m3 = CreateTracingMiddleware(callOrder, "m3");

            var subscriber = CreateSubscriber(new[] { saga }, m1, m2, m3);

            // Act
            await subscriber.Subscribe(testCommand);

            // Assert
            Assert.That(callOrder, Is.EqualTo(new[]
            {
                "m1-before", "m2-before", "m3-before",
                "m3-after", "m2-after", "m1-after"
            }));
            Assert.That(saga.Handled, Is.True);
        }

        [Test]
        public async Task SecondMiddleware_ShortCircuits_ThirdNeverCalledAndSagaNotHandled()
        {
            // Arrange
            var callOrder = new List<string>();
            var saga = new MiddlewareTestSaga();

            var m1 = CreateTracingMiddleware(callOrder, "m1");

            var m2 = new Mock<ICommandSubscribeMiddleware>();
            m2.Setup(m => m.InvokeAsync(It.IsAny<MiddlewareTestCommand>(), It.IsAny<Func<MiddlewareTestCommand, Task>>()))
                .Returns<MiddlewareTestCommand, Func<MiddlewareTestCommand, Task>>((cmd, next) =>
                {
                    callOrder.Add("m2-shortcircuit");
                    return Task.CompletedTask;
                });

            var m3 = CreateTracingMiddleware(callOrder, "m3");

            var subscriber = CreateSubscriber(new[] { saga }, m1, m2.Object, m3);

            // Act
            await subscriber.Subscribe(testCommand);

            // Assert
            Assert.That(callOrder, Is.EqualTo(new[] { "m1-before", "m2-shortcircuit", "m1-after" }));
            Assert.That(saga.Handled, Is.False);
        }

        [Test]
        public async Task Middleware_ExceptionPropagates()
        {
            // Arrange
            var middleware = new Mock<ICommandSubscribeMiddleware>();
            middleware
                .Setup(m => m.InvokeAsync(It.IsAny<MiddlewareTestCommand>(), It.IsAny<Func<MiddlewareTestCommand, Task>>()))
                .ThrowsAsync(new InvalidOperationException("middleware error"));

            var subscriber = CreateSubscriber(new[] { new MiddlewareTestSaga() }, middleware.Object);

            // Act & Assert
            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await subscriber.Subscribe(testCommand));
            Assert.That(ex.Message, Is.EqualTo("middleware error"));
        }

        [Test]
        public async Task Middleware_CanCatchAndHandleExceptionFromSaga()
        {
            // Arrange
            Exception caughtException = null;
            var faultySaga = new Mock<ISaga>();
            faultySaga.Setup(s => s.Handle(It.IsAny<MiddlewareTestCommand>()))
                .ThrowsAsync(new InvalidOperationException("saga error"));

            // Make faultySaga look like it handles MiddlewareTestCommand via Saga<IEntity>.CanHandle
            // We need to use a real saga that throws
            var throwingSaga = new ThrowingTestSaga();

            var middleware = new Mock<ICommandSubscribeMiddleware>();
            middleware
                .Setup(m => m.InvokeAsync(It.IsAny<MiddlewareTestCommand>(), It.IsAny<Func<MiddlewareTestCommand, Task>>()))
                .Returns<MiddlewareTestCommand, Func<MiddlewareTestCommand, Task>>(async (cmd, next) =>
                {
                    try
                    {
                        await next(cmd);
                    }
                    catch (Exception ex)
                    {
                        caughtException = ex;
                    }
                });

            var subscriber = CreateSubscriber(new ISaga[] { throwingSaga }, middleware.Object);

            // Act
            await subscriber.Subscribe(testCommand);

            // Assert
            Assert.That(caughtException, Is.Not.Null);
            Assert.That(caughtException.Message, Is.EqualTo("saga error"));
        }

        [Test]
        public async Task Middleware_WithEmptySagas_StillExecutes()
        {
            // Arrange
            var middlewareCalled = false;
            var middleware = new Mock<ICommandSubscribeMiddleware>();
            middleware
                .Setup(m => m.InvokeAsync(It.IsAny<MiddlewareTestCommand>(), It.IsAny<Func<MiddlewareTestCommand, Task>>()))
                .Returns<MiddlewareTestCommand, Func<MiddlewareTestCommand, Task>>(async (cmd, next) =>
                {
                    middlewareCalled = true;
                    await next(cmd);
                });

            var subscriber = CreateSubscriber(Enumerable.Empty<ISaga>(), middleware.Object);

            // Act
            await subscriber.Subscribe(testCommand);

            // Assert
            Assert.That(middlewareCalled, Is.True);
        }

        [Test]
        public async Task Middleware_CanModifyCommandMetadataBeforeNext()
        {
            // Arrange
            var saga = new MiddlewareTestSaga();
            var middleware = new Mock<ICommandSubscribeMiddleware>();
            middleware
                .Setup(m => m.InvokeAsync(It.IsAny<MiddlewareTestCommand>(), It.IsAny<Func<MiddlewareTestCommand, Task>>()))
                .Returns<MiddlewareTestCommand, Func<MiddlewareTestCommand, Task>>(async (cmd, next) =>
                {
                    cmd.Metadata.Properties = new Dictionary<string, object> { { "enriched", true } };
                    await next(cmd);
                });

            var subscriber = CreateSubscriber(new[] { saga }, middleware.Object);

            // Act
            await subscriber.Subscribe(testCommand);

            // Assert
            Assert.That(testCommand.Metadata.Properties.ContainsKey("enriched"), Is.True);
            Assert.That(saga.Handled, Is.True);
        }

        private ICommandSubscribeMiddleware CreateTracingMiddleware(List<string> callOrder, string name)
        {
            var mock = new Mock<ICommandSubscribeMiddleware>();
            mock.Setup(m => m.InvokeAsync(It.IsAny<MiddlewareTestCommand>(), It.IsAny<Func<MiddlewareTestCommand, Task>>()))
                .Returns<MiddlewareTestCommand, Func<MiddlewareTestCommand, Task>>(async (cmd, next) =>
                {
                    callOrder.Add($"{name}-before");
                    await next(cmd);
                    callOrder.Add($"{name}-after");
                });
            return mock.Object;
        }
    }

    public class ThrowingTestSaga : ISaga, IHandles<MiddlewareTestCommand>
    {
        public Task Handle<TCommand>(TCommand command) where TCommand : ICommand
        {
            throw new InvalidOperationException("saga error");
        }

        public Task<IEntity> Handle(IEntity entity, MiddlewareTestCommand command)
        {
            throw new InvalidOperationException("saga error");
        }
    }
}

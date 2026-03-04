using Microsoft.Extensions.Logging;
using Moq;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Bus;
using SourceFlow.Messaging.Bus.Impl;
using SourceFlow.Messaging.Commands;
using SourceFlow.Observability;
using SourceFlow.Core.Tests.Impl;

namespace SourceFlow.Core.Tests.Middleware
{
    [TestFixture]
    [Category("Unit")]
    public class CommandDispatchMiddlewareTests
    {
        private Mock<ICommandStoreAdapter> commandStoreMock;
        private Mock<ILogger<ICommandBus>> loggerMock;
        private Mock<ICommandDispatcher> commandDispatcherMock;
        private Mock<IDomainTelemetryService> telemetryMock;

        [SetUp]
        public void Setup()
        {
            commandStoreMock = new Mock<ICommandStoreAdapter>();
            loggerMock = new Mock<ILogger<ICommandBus>>();
            commandDispatcherMock = new Mock<ICommandDispatcher>();
            telemetryMock = new Mock<IDomainTelemetryService>();

            telemetryMock.Setup(t => t.TraceAsync(It.IsAny<string>(), It.IsAny<Func<Task>>(), It.IsAny<Action<System.Diagnostics.Activity>>()))
                .Returns((string name, Func<Task> operation, Action<System.Diagnostics.Activity> enrich) => operation());

            commandStoreMock.Setup(cs => cs.GetNextSequenceNo(It.IsAny<int>())).ReturnsAsync(1);
        }

        private CommandBus CreateBus(params ICommandDispatchMiddleware[] middlewares)
        {
            return new CommandBus(
                new[] { commandDispatcherMock.Object },
                commandStoreMock.Object,
                loggerMock.Object,
                telemetryMock.Object,
                middlewares);
        }

        [Test]
        public async Task Middleware_ReceivesSameCommandInstance()
        {
            // Arrange
            DummyCommand capturedCommand = null;
            var middleware = new Mock<ICommandDispatchMiddleware>();
            middleware
                .Setup(m => m.InvokeAsync(It.IsAny<DummyCommand>(), It.IsAny<Func<DummyCommand, Task>>()))
                .Returns<DummyCommand, Func<DummyCommand, Task>>(async (cmd, next) =>
                {
                    capturedCommand = cmd;
                    await next(cmd);
                });

            var bus = CreateBus(middleware.Object);
            var command = new DummyCommand();

            // Act
            await ((ICommandBus)bus).Publish(command);

            // Assert
            Assert.That(capturedCommand, Is.SameAs(command));
        }

        [Test]
        public async Task ThreeMiddleware_ExecuteInCorrectNestingOrder()
        {
            // Arrange
            var callOrder = new List<string>();

            var m1 = CreateTracingMiddleware(callOrder, "m1");
            var m2 = CreateTracingMiddleware(callOrder, "m2");
            var m3 = CreateTracingMiddleware(callOrder, "m3");

            var bus = CreateBus(m1, m2, m3);

            // Act
            await ((ICommandBus)bus).Publish(new DummyCommand());

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

            var m2 = new Mock<ICommandDispatchMiddleware>();
            m2.Setup(m => m.InvokeAsync(It.IsAny<DummyCommand>(), It.IsAny<Func<DummyCommand, Task>>()))
                .Returns<DummyCommand, Func<DummyCommand, Task>>((cmd, next) =>
                {
                    callOrder.Add("m2-shortcircuit");
                    return Task.CompletedTask; // Does NOT call next
                });

            var m3 = CreateTracingMiddleware(callOrder, "m3");

            var bus = CreateBus(m1, m2.Object, m3);

            // Act
            await ((ICommandBus)bus).Publish(new DummyCommand());

            // Assert
            Assert.That(callOrder, Is.EqualTo(new[] { "m1-before", "m2-shortcircuit", "m1-after" }));
            commandDispatcherMock.Verify(cd => cd.Dispatch(It.IsAny<DummyCommand>()), Times.Never);
        }

        [Test]
        public async Task Middleware_ExceptionPropagates()
        {
            // Arrange
            var middleware = new Mock<ICommandDispatchMiddleware>();
            middleware
                .Setup(m => m.InvokeAsync(It.IsAny<DummyCommand>(), It.IsAny<Func<DummyCommand, Task>>()))
                .ThrowsAsync(new InvalidOperationException("middleware error"));

            var bus = CreateBus(middleware.Object);

            // Act & Assert
            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await ((ICommandBus)bus).Publish(new DummyCommand()));
            Assert.That(ex.Message, Is.EqualTo("middleware error"));
        }

        [Test]
        public async Task Middleware_CanCatchAndHandleExceptionFromNext()
        {
            // Arrange
            Exception caughtException = null;

            commandDispatcherMock
                .Setup(cd => cd.Dispatch(It.IsAny<DummyCommand>()))
                .ThrowsAsync(new InvalidOperationException("dispatch error"));

            var middleware = new Mock<ICommandDispatchMiddleware>();
            middleware
                .Setup(m => m.InvokeAsync(It.IsAny<DummyCommand>(), It.IsAny<Func<DummyCommand, Task>>()))
                .Returns<DummyCommand, Func<DummyCommand, Task>>(async (cmd, next) =>
                {
                    try
                    {
                        await next(cmd);
                    }
                    catch (Exception ex)
                    {
                        caughtException = ex;
                        // Swallow the exception
                    }
                });

            var bus = CreateBus(middleware.Object);

            // Act - should not throw because middleware caught it
            await ((ICommandBus)bus).Publish(new DummyCommand());

            // Assert
            Assert.That(caughtException, Is.Not.Null);
            Assert.That(caughtException.Message, Is.EqualTo("dispatch error"));
        }

        [Test]
        public async Task Middleware_CanModifyCommandMetadataBeforeNext()
        {
            // Arrange
            var middleware = new Mock<ICommandDispatchMiddleware>();
            middleware
                .Setup(m => m.InvokeAsync(It.IsAny<DummyCommand>(), It.IsAny<Func<DummyCommand, Task>>()))
                .Returns<DummyCommand, Func<DummyCommand, Task>>(async (cmd, next) =>
                {
                    cmd.Metadata.Properties = new Dictionary<string, object> { { "enriched", true } };
                    await next(cmd);
                });

            DummyCommand dispatchedCommand = null;
            commandDispatcherMock
                .Setup(cd => cd.Dispatch(It.IsAny<DummyCommand>()))
                .Callback<DummyCommand>(cmd => dispatchedCommand = cmd)
                .Returns(Task.CompletedTask);

            var bus = CreateBus(middleware.Object);
            var command = new DummyCommand();

            // Act
            await ((ICommandBus)bus).Publish(command);

            // Assert
            Assert.That(dispatchedCommand.Metadata.Properties.ContainsKey("enriched"), Is.True);
        }

        [Test]
        public async Task Middleware_CalledOnReplayedCommands()
        {
            // Arrange
            var middlewareCalled = false;
            var middleware = new Mock<ICommandDispatchMiddleware>();
            middleware
                .Setup(m => m.InvokeAsync(It.IsAny<DummyCommand>(), It.IsAny<Func<DummyCommand, Task>>()))
                .Returns<DummyCommand, Func<DummyCommand, Task>>(async (cmd, next) =>
                {
                    middlewareCalled = true;
                    await next(cmd);
                });

            var replayCommand = new DummyCommand();
            replayCommand.Metadata.IsReplay = true;
            replayCommand.Metadata.SequenceNo = 5;

            commandStoreMock.Setup(cs => cs.Load(It.IsAny<int>()))
                .ReturnsAsync(new List<ICommand> { replayCommand });

            var bus = CreateBus(middleware.Object);

            // Act
            await ((ICommandBus)bus).Replay(1);

            // Assert
            Assert.That(middlewareCalled, Is.True);
        }

        [Test]
        public async Task Middleware_CallingNextTwice_DispatchesTwice()
        {
            // Arrange
            var middleware = new Mock<ICommandDispatchMiddleware>();
            middleware
                .Setup(m => m.InvokeAsync(It.IsAny<DummyCommand>(), It.IsAny<Func<DummyCommand, Task>>()))
                .Returns<DummyCommand, Func<DummyCommand, Task>>(async (cmd, next) =>
                {
                    await next(cmd);
                    await next(cmd);
                });

            var bus = CreateBus(middleware.Object);

            // Act
            await ((ICommandBus)bus).Publish(new DummyCommand());

            // Assert
            commandDispatcherMock.Verify(cd => cd.Dispatch(It.IsAny<DummyCommand>()), Times.Exactly(2));
        }

        private ICommandDispatchMiddleware CreateTracingMiddleware(List<string> callOrder, string name)
        {
            var mock = new Mock<ICommandDispatchMiddleware>();
            mock.Setup(m => m.InvokeAsync(It.IsAny<DummyCommand>(), It.IsAny<Func<DummyCommand, Task>>()))
                .Returns<DummyCommand, Func<DummyCommand, Task>>(async (cmd, next) =>
                {
                    callOrder.Add($"{name}-before");
                    await next(cmd);
                    callOrder.Add($"{name}-after");
                });
            return mock.Object;
        }
    }
}

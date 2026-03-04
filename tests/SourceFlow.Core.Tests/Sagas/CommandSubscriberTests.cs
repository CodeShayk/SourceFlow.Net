using Microsoft.Extensions.Logging;
using Moq;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;
using SourceFlow.Saga;

namespace SourceFlow.Core.Tests.Sagas
{
    public class DummyCommandPayload : IPayload
    {
        public int Id { get; set; }
        public string Data { get; set; } = string.Empty;
    }

    public class DummyCommand : Command<DummyCommandPayload>
    {
        public DummyCommand(DummyCommandPayload payload) : base(true, payload)
        {
        }
    }

    public class TestSaga : ISaga, IHandles<DummyCommand>
    {
        public bool Handled { get; private set; } = false;
        public Type LastHandledCommandType { get; private set; } = null!;
        public DummyCommand LastHandledCommand { get; private set; } = null!;

        public Task Handle<TCommand>(TCommand command) where TCommand : ICommand
        {
            if (this is IHandles<TCommand> handles)
            {
                Handled = true;
                LastHandledCommandType = typeof(TCommand);
                if (command is DummyCommand dummyCommand)
                {
                    LastHandledCommand = dummyCommand;
                }
            }
            return Task.CompletedTask;
        }

        public Task<IEntity> Handle(IEntity entity, DummyCommand command)
        {
            Handled = true;
            LastHandledCommandType = typeof(DummyCommand);
            LastHandledCommand = command;
            return Task.FromResult(entity);
        }
    }

    public class NonHandlingSaga : ISaga
    {
        public bool Handled { get; private set; } = false;

        public Task Handle<TCommand>(TCommand command) where TCommand : ICommand
        {
            // This saga doesn't implement IHandles<DummyCommand>, so it won't handle the command
            // But we still want to track if this method was called
            Handled = true; // This will be true if the ISaga.On method is called
            return Task.CompletedTask;
        }
    }

    [TestFixture]
    [Category("Unit")]
    public class CommandSubscriberTests
    {
        private Mock<ILogger<ICommandSubscriber>> _mockLogger;
        private DummyCommand _testCommand;

        [SetUp]
        public void SetUp()
        {
            _mockLogger = new Mock<ILogger<ICommandSubscriber>>();
            _testCommand = new DummyCommand(new DummyCommandPayload { Id = 1, Data = "Test" });
        }

        [Test]
        public void Constructor_WithValidParameters_Succeeds()
        {
            // Arrange
            var sagas = new List<ISaga> { new TestSaga() };

            // Act
            var subscriber = new CommandSubscriber(sagas, _mockLogger.Object, Enumerable.Empty<ICommandSubscribeMiddleware>());

            // Assert
            Assert.IsNotNull(subscriber);
        }

        [Test]
        public void Constructor_NullMiddleware_ThrowsArgumentNullException()
        {
            // Arrange
            var sagas = new List<ISaga> { new TestSaga() };

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() =>
                new CommandSubscriber(sagas, _mockLogger.Object, null));
        }

        [Test]
        public async Task Subscribe_WithMatchingSaga_HandlesCommand()
        {
            // Arrange
            var testSaga = new TestSaga();
            var sagas = new List<ISaga> { testSaga };
            var subscriber = new CommandSubscriber(sagas, _mockLogger.Object, Enumerable.Empty<ICommandSubscribeMiddleware>());

            // Act
            await subscriber.Subscribe(_testCommand);

            // Assert
            Assert.IsTrue(testSaga.Handled);
            Assert.That(testSaga.LastHandledCommandType, Is.EqualTo(typeof(DummyCommand)));
        }

        [Test]
        public async Task Subscribe_WithEmptySagasCollection_DoesNotThrow()
        {
            // Arrange
            var sagas = new List<ISaga>();

            // Act
            var subscriber = new CommandSubscriber(sagas, _mockLogger.Object, Enumerable.Empty<ICommandSubscribeMiddleware>());

            // Assert
            Assert.IsNotNull(subscriber);

            // Act & Assert - should not throw and should just return early
            Assert.DoesNotThrowAsync(async () => await subscriber.Subscribe(_testCommand));
        }

        [Test]
        public async Task Subscribe_WithMultipleSagas_HandlesCommandInAllMatchingSagas()
        {
            // Arrange
            var testSaga1 = new TestSaga();
            var testSaga2 = new TestSaga();
            var nonHandlingSaga = new NonHandlingSaga();
            var sagas = new List<ISaga> { testSaga1, nonHandlingSaga, testSaga2 };
            var subscriber = new CommandSubscriber(sagas, _mockLogger.Object, Enumerable.Empty<ICommandSubscribeMiddleware>());

            // Act
            await subscriber.Subscribe(_testCommand);

            // Assert
            Assert.IsTrue(testSaga1.Handled);
            Assert.IsTrue(testSaga2.Handled);
            Assert.IsFalse(nonHandlingSaga.Handled); // This saga doesn't implement IHandles<DummyCommand>
            Assert.That(testSaga1.LastHandledCommandType, Is.EqualTo(typeof(DummyCommand)));
            Assert.That(testSaga2.LastHandledCommandType, Is.EqualTo(typeof(DummyCommand)));
        }

        [Test]
        public async Task Subscribe_NullSagas_StillCreatesSubscriber()
        {
            // Arrange & Act
            var subscriber = new CommandSubscriber(null, _mockLogger.Object, Enumerable.Empty<ICommandSubscribeMiddleware>());

            // Assert
            Assert.IsNotNull(subscriber);

            // Note: The CommandSubscriber constructor doesn't validate null sagas,
            // so we just test that it doesn't throw during construction.
            // During Subscribe(), it would check sagas.Any() which would handle null.
        }

        [Test]
        public async Task Subscribe_WithMiddleware_ExecutesMiddlewareAroundCoreLogic()
        {
            // Arrange
            var callOrder = new List<string>();
            var testSaga = new TestSaga();
            var sagas = new List<ISaga> { testSaga };

            var middlewareMock = new Mock<ICommandSubscribeMiddleware>();
            middlewareMock
                .Setup(m => m.InvokeAsync(It.IsAny<DummyCommand>(), It.IsAny<Func<DummyCommand, Task>>()))
                .Returns<DummyCommand, Func<DummyCommand, Task>>(async (cmd, next) =>
                {
                    callOrder.Add("middleware-before");
                    await next(cmd);
                    callOrder.Add("middleware-after");
                });

            var subscriber = new CommandSubscriber(sagas, _mockLogger.Object, new[] { middlewareMock.Object });

            // Act
            await subscriber.Subscribe(_testCommand);

            // Assert
            Assert.That(callOrder[0], Is.EqualTo("middleware-before"));
            Assert.That(callOrder[1], Is.EqualTo("middleware-after"));
            Assert.IsTrue(testSaga.Handled);
        }

        [Test]
        public async Task Subscribe_WithMultipleMiddleware_ExecutesInRegistrationOrder()
        {
            // Arrange
            var callOrder = new List<string>();
            var testSaga = new TestSaga();
            var sagas = new List<ISaga> { testSaga };

            var middleware1 = new Mock<ICommandSubscribeMiddleware>();
            middleware1
                .Setup(m => m.InvokeAsync(It.IsAny<DummyCommand>(), It.IsAny<Func<DummyCommand, Task>>()))
                .Returns<DummyCommand, Func<DummyCommand, Task>>(async (cmd, next) =>
                {
                    callOrder.Add("m1-before");
                    await next(cmd);
                    callOrder.Add("m1-after");
                });

            var middleware2 = new Mock<ICommandSubscribeMiddleware>();
            middleware2
                .Setup(m => m.InvokeAsync(It.IsAny<DummyCommand>(), It.IsAny<Func<DummyCommand, Task>>()))
                .Returns<DummyCommand, Func<DummyCommand, Task>>(async (cmd, next) =>
                {
                    callOrder.Add("m2-before");
                    await next(cmd);
                    callOrder.Add("m2-after");
                });

            var subscriber = new CommandSubscriber(sagas, _mockLogger.Object,
                new ICommandSubscribeMiddleware[] { middleware1.Object, middleware2.Object });

            // Act
            await subscriber.Subscribe(_testCommand);

            // Assert
            Assert.That(callOrder, Is.EqualTo(new[] { "m1-before", "m2-before", "m2-after", "m1-after" }));
        }

        [Test]
        public async Task Subscribe_MiddlewareShortCircuits_DoesNotCallCoreLogic()
        {
            // Arrange
            var testSaga = new TestSaga();
            var sagas = new List<ISaga> { testSaga };

            var middlewareMock = new Mock<ICommandSubscribeMiddleware>();
            middlewareMock
                .Setup(m => m.InvokeAsync(It.IsAny<DummyCommand>(), It.IsAny<Func<DummyCommand, Task>>()))
                .Returns(Task.CompletedTask); // Does NOT call next

            var subscriber = new CommandSubscriber(sagas, _mockLogger.Object, new[] { middlewareMock.Object });

            // Act
            await subscriber.Subscribe(_testCommand);

            // Assert - saga was never reached
            Assert.IsFalse(testSaga.Handled);
        }
    }
}

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
            var subscriber = new CommandSubscriber(sagas, _mockLogger.Object);

            // Assert
            Assert.IsNotNull(subscriber);
        }

        [Test]
        public async Task Subscribe_WithMatchingSaga_HandlesCommand()
        {
            // Arrange
            var testSaga = new TestSaga();
            var sagas = new List<ISaga> { testSaga };
            var subscriber = new CommandSubscriber(sagas, _mockLogger.Object);

            // Act
            await subscriber.Subscribe(_testCommand);

            // Assert
            Assert.IsTrue(testSaga.Handled);
            Assert.AreEqual(typeof(DummyCommand), testSaga.LastHandledCommandType);
        }

        [Test]
        public async Task Subscribe_WithEmptySagasCollection_DoesNotThrow()
        {
            // Arrange
            var sagas = new List<ISaga>();

            // Act
            var subscriber = new CommandSubscriber(sagas, _mockLogger.Object);

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
            var subscriber = new CommandSubscriber(sagas, _mockLogger.Object);

            // Act
            await subscriber.Subscribe(_testCommand);

            // Assert
            Assert.IsTrue(testSaga1.Handled);
            Assert.IsTrue(testSaga2.Handled);
            Assert.IsFalse(nonHandlingSaga.Handled); // This saga doesn't implement IHandles<DummyCommand>
            Assert.AreEqual(typeof(DummyCommand), testSaga1.LastHandledCommandType);
            Assert.AreEqual(typeof(DummyCommand), testSaga2.LastHandledCommandType);
        }

        [Test]
        public async Task Subscribe_NullSagas_StillCreatesSubscriber()
        {
            // Arrange & Act
            var subscriber = new CommandSubscriber(null, _mockLogger.Object);

            // Assert
            Assert.IsNotNull(subscriber);

            // Note: The CommandSubscriber constructor doesn't validate null sagas,
            // so we just test that it doesn't throw during construction.
            // During Subscribe(), it would check sagas.Any() which would handle null.
        }
    }
}

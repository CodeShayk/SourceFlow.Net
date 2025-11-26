using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Bus;
using SourceFlow.Messaging.Bus.Impl;
using SourceFlow.Messaging.Commands;
using SourceFlow.Observability;

namespace SourceFlow.Core.Tests.Impl
{
    [TestFixture]
    public class CommandBusTests
    {
        private Mock<ICommandStoreAdapter> commandStoreMock;
        private Mock<ILogger<ICommandBus>> loggerMock;
        private Mock<ICommandDispatcher> commandDispatcherMock;
        private Mock<IDomainTelemetryService> telemetryMock;
        private CommandBus commandBus;

        [SetUp]
        public void Setup()
        {
            commandStoreMock = new Mock<ICommandStoreAdapter>();
            loggerMock = new Mock<ILogger<ICommandBus>>();
            commandDispatcherMock = new Mock<ICommandDispatcher>();
            telemetryMock = new Mock<IDomainTelemetryService>();

            // Setup telemetry mock to execute operations directly
            telemetryMock.Setup(t => t.TraceAsync(It.IsAny<string>(), It.IsAny<Func<Task>>(), It.IsAny<Action<System.Diagnostics.Activity>>()))
                .Returns((string name, Func<Task> operation, Action<System.Diagnostics.Activity> enrich) => operation());

            commandBus = new CommandBus(
                commandDispatcherMock.Object,
                commandStoreMock.Object,
                loggerMock.Object,
                telemetryMock.Object);
        }

        [Test]
        public void Constructor_NullCommandStore_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new CommandBus(commandDispatcherMock.Object, null, loggerMock.Object, telemetryMock.Object));
        }

        [Test]
        public void Constructor_NullLogger_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new CommandBus(commandDispatcherMock.Object, commandStoreMock.Object, null, telemetryMock.Object));
        }

        [Test]
        public void Constructor_NullCommandDispatcher_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new CommandBus(null, commandStoreMock.Object, loggerMock.Object, telemetryMock.Object));
        }

        [Test]
        public void Constructor_SetsDependencies()
        {
            Assert.That(commandBus.commandDispatcher, Is.EqualTo(commandDispatcherMock.Object));
        }

        [Test]
        public async Task Publish_NullCommand_ThrowsArgumentNullException()
        {
            ICommandBus bus = commandBus;
            Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await bus.Publish<DummyCommand>(null));
        }

        [Test]
        public async Task Publish_ValidCommand_SetsSequenceNumber()
        {
            // Arrange
            var command = new DummyCommand();
            commandStoreMock.Setup(cs => cs.GetNextSequenceNo(It.IsAny<int>()))
                .ReturnsAsync(42);

            // Act
            ICommandBus bus = commandBus;
            await bus.Publish(command);

            // Assert
            Assert.That(command.Metadata.SequenceNo, Is.EqualTo(42));
        }

        [Test]
        public async Task Publish_ValidCommand_DispatchesToCommandDispatcher()
        {
            // Arrange
            var command = new DummyCommand();
            commandStoreMock.Setup(cs => cs.GetNextSequenceNo(It.IsAny<int>()))
                .ReturnsAsync(1);

            // Act
            ICommandBus bus = commandBus;
            await bus.Publish(command);

            // Assert
            commandDispatcherMock.Verify(cd => cd.Dispatch(command), Times.Once);
        }

        [Test]
        public async Task Publish_ValidCommand_LogsInformation()
        {
            // Arrange
            var command = new DummyCommand();
            commandStoreMock.Setup(cs => cs.GetNextSequenceNo(It.IsAny<int>()))
                .ReturnsAsync(1);

            // Act
            ICommandBus bus = commandBus;
            await bus.Publish(command);

            // Assert
            loggerMock.Verify(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                (Func<It.IsAnyType, Exception, string>)It.IsAny<object>()),
                Times.AtLeastOnce);
        }

        [Test]
        public async Task Publish_ValidCommand_AppendsToStore()
        {
            // Arrange
            var command = new DummyCommand();
            commandStoreMock.Setup(cs => cs.GetNextSequenceNo(It.IsAny<int>()))
                .ReturnsAsync(1);

            // Act
            ICommandBus bus = commandBus;
            await bus.Publish(command);

            // Assert
            commandStoreMock.Verify(cs => cs.Append(command), Times.Once);
        }

        [Test]
        public async Task Publish_ReplayCommand_DoesNotSetSequenceNumber()
        {
            // Arrange
            var command = new DummyCommand();
            command.Metadata.IsReplay = true;
            command.Metadata.SequenceNo = 99;

            // Act
            ICommandBus bus = commandBus;
            await bus.Publish(command);

            // Assert
            Assert.That(command.Metadata.SequenceNo, Is.EqualTo(99));
            commandStoreMock.Verify(cs => cs.GetNextSequenceNo(It.IsAny<int>()), Times.Never);
        }

        [Test]
        public async Task Publish_ReplayCommand_DoesNotAppendToStore()
        {
            // Arrange
            var command = new DummyCommand();
            command.Metadata.IsReplay = true;

            // Act
            ICommandBus bus = commandBus;
            await bus.Publish(command);

            // Assert
            commandStoreMock.Verify(cs => cs.Append(It.IsAny<ICommand>()), Times.Never);
        }

        [Test]
        public async Task Replay_NoCommands_DoesNotDispatch()
        {
            // Arrange
            commandStoreMock.Setup(cs => cs.Load(It.IsAny<int>()))
                .ReturnsAsync((IEnumerable<ICommand>)null);

            // Act
            ICommandBus bus = commandBus;
            await bus.Replay(1);

            // Assert
            commandDispatcherMock.Verify(cd => cd.Dispatch(It.IsAny<ICommand>()), Times.Never);
        }

        [Test]
        public async Task Replay_WithCommands_DispatchesAllCommands()
        {
            // Arrange
            var commands = new List<ICommand>
            {
                new DummyCommand { Metadata = new Metadata() },
                new DummyCommand { Metadata = new Metadata() },
                new DummyCommand { Metadata = new Metadata() }
            };
            commandStoreMock.Setup(cs => cs.Load(It.IsAny<int>()))
                .ReturnsAsync(commands);

            // Act
            ICommandBus bus = commandBus;
            await bus.Replay(1);

            // Assert
            commandDispatcherMock.Verify(cd => cd.Dispatch(It.IsAny<ICommand>()), Times.Exactly(3));
        }

        [Test]
        public async Task Replay_WithCommands_MarksCommandsAsReplay()
        {
            // Arrange
            var commands = new List<ICommand>
            {
                new DummyCommand { Metadata = new Metadata() },
                new DummyCommand { Metadata = new Metadata() }
            };
            commandStoreMock.Setup(cs => cs.Load(It.IsAny<int>()))
                .ReturnsAsync(commands);

            // Act
            ICommandBus bus = commandBus;
            await bus.Replay(1);

            // Assert
            Assert.That(commands.All(c => c.Metadata.IsReplay), Is.True);
        }

        [Test]
        public async Task Replay_WithCommands_DoesNotAppendToStore()
        {
            // Arrange
            var commands = new List<ICommand>
            {
                new DummyCommand { Metadata = new Metadata() }
            };
            commandStoreMock.Setup(cs => cs.Load(It.IsAny<int>()))
                .ReturnsAsync(commands);

            // Act
            ICommandBus bus = commandBus;
            await bus.Replay(1);

            // Assert
            commandStoreMock.Verify(cs => cs.Append(It.IsAny<ICommand>()), Times.Never);
        }
    }
}

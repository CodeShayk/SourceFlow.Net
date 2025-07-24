using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using SourceFlow.Aggregate;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Bus;

namespace SourceFlow.Core.Tests.Aggregates
{
    [TestFixture]
    public class AggregateTests
    {
        public class DummyEntity : IEntity
        { public int Id { get; set; } }

        public class TestAggregate : Aggregate<DummyEntity>
        {
            public TestAggregate() : this(new Mock<ICommandPublisher>().Object, new Mock<ICommandReplayer>().Object, new Mock<ILogger>().Object)
            {
            }

            public TestAggregate(ICommandPublisher publisher, ICommandReplayer replayer, ILogger logger)
            {
                commandPublisher = publisher;
                commandReplayer = replayer;
                this.logger = logger;
            }

            public Task TestSend(ICommand command) => Send(command);
        }

        [Test]
        public async Task Replay_DelegatesToCommandReplayer()
        {
            var publisher = new Mock<ICommandPublisher>().Object;
            var replayerMock = new Mock<ICommandReplayer>();
            replayerMock.Setup(r => r.Replay(It.IsAny<int>())).Returns(Task.CompletedTask);
            var logger = new Mock<ILogger>().Object;
            var aggregate = new TestAggregate(publisher, replayerMock.Object, logger);
            await aggregate.Replay(42);
            replayerMock.Verify(r => r.Replay(42), Times.Once);
        }

        [Test]
        public void Send_NullCommand_ThrowsArgumentNullException()
        {
            var publisher = new Mock<ICommandPublisher>().Object;
            var replayer = new Mock<ICommandReplayer>().Object;
            var logger = new Mock<ILogger>().Object;
            var aggregate = new TestAggregate(publisher, replayer, logger);
            Assert.ThrowsAsync<ArgumentNullException>(async () => await aggregate.TestSend(null));
        }

        [Test]
        public void Send_NullPayload_ThrowsInvalidOperationException()
        {
            var publisher = new Mock<ICommandPublisher>().Object;
            var replayer = new Mock<ICommandReplayer>().Object;
            var logger = new Mock<ILogger>().Object;
            var aggregate = new TestAggregate(publisher, replayer, logger);
            var commandMock = new Mock<ICommand>();
            commandMock.Setup(c => c.Payload).Returns((IPayload)null);
            Assert.ThrowsAsync<InvalidOperationException>(async () => await aggregate.TestSend(commandMock.Object));
        }

        [Test]
        public async Task Send_ValidCommand_DelegatesToPublisher()
        {
            var publisherMock = new Mock<ICommandPublisher>();
            publisherMock.Setup(p => p.Publish(It.IsAny<ICommand>())).Returns(Task.CompletedTask);
            var replayer = new Mock<ICommandReplayer>().Object;
            var logger = new Mock<ILogger>().Object;
            var aggregate = new TestAggregate(publisherMock.Object, replayer, logger);
            var payloadMock = new Mock<IPayload>();
            payloadMock.Setup(p => p.Id).Returns(1);
            var commandMock = new Mock<ICommand>();
            commandMock.Setup(c => c.Payload).Returns(payloadMock.Object);
            await aggregate.TestSend(commandMock.Object);
            publisherMock.Verify(p => p.Publish(commandMock.Object), Times.Once);
        }
    }
}
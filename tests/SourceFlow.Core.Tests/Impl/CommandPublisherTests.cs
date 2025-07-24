using System;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using SourceFlow.Messaging.Bus;
using SourceFlow.Impl;
using SourceFlow.Messaging;

namespace SourceFlow.Core.Tests.Impl
{
    [TestFixture]
    public class CommandPublisherTests
    {
        [Test]
        public void Constructor_SetsCommandBus()
        {
            var bus = new Mock<ICommandBus>().Object;
            var publisher = new CommandPublisher(bus);
            Assert.IsNotNull(publisher);
        }

        [Test]
        public void Publish_NullCommand_ThrowsArgumentNullException()
        {
            var bus = new Mock<ICommandBus>().Object;
            var publisher = (ICommandPublisher)new CommandPublisher(bus);
            Assert.ThrowsAsync<ArgumentNullException>(async () => await publisher.Publish<ICommand>(null));
        }

        [Test]
        public void Publish_NullPayloadId_ThrowsInvalidOperationException()
        {
            var bus = new Mock<ICommandBus>().Object;
            var publisher = (ICommandPublisher)new CommandPublisher(bus);
            var commandMock = new Mock<ICommand>();
            commandMock.Setup(c => c.Payload).Returns((IPayload)null);
            Assert.ThrowsAsync<InvalidOperationException>(async () => await publisher.Publish(commandMock.Object));
        }

        [Test]
        public async Task Publish_ValidCommand_DelegatesToCommandBus()
        {
            var busMock = new Mock<ICommandBus>();
            busMock.Setup(b => b.Publish(It.IsAny<ICommand>())).Returns(Task.CompletedTask);
            var publisher = (ICommandPublisher)new CommandPublisher(busMock.Object);
            var payloadMock = new Mock<IPayload>();
            payloadMock.Setup(p => p.Id).Returns(1);
            var commandMock = new Mock<ICommand>();
            commandMock.Setup(c => c.Payload).Returns(payloadMock.Object);
            await publisher.Publish(commandMock.Object);
            busMock.Verify(b => b.Publish(commandMock.Object), Times.Once);
        }
    }
}
using Moq;
using SourceFlow.Impl;
using SourceFlow.Messaging.Bus;

namespace SourceFlow.Core.Tests.Impl
{
    [TestFixture]
    public class CommandReplayerTests
    {
        [Test]
        public void Constructor_SetsCommandBus()
        {
            var bus = new Mock<ICommandBus>().Object;
            var replayer = new CommandReplayer(bus);
            Assert.IsNotNull(replayer);
        }

        [Test]
        public async Task Replay_DelegatesToCommandBus()
        {
            var busMock = new Mock<ICommandBus>();
            busMock.Setup(b => b.Replay(It.IsAny<int>())).Returns(Task.CompletedTask);
            var replayer = (ICommandReplayer)new CommandReplayer(busMock.Object);
            await replayer.Replay(42);
            busMock.Verify(b => b.Replay(42), Times.Once);
        }
    }
}
using NUnit.Framework;
using Moq;
using System.Threading.Tasks;
using SourceFlow.Messaging.Bus;

namespace SourceFlow.Core.Tests.Interfaces
{
    public class IEventReplayerTests
    {
        [Test]
        public async Task ReplayEventsAsync_DoesNotThrow()
        {
            var mock = new Mock<ICommandReplayer>();
            mock.Setup(r => r.Replay(It.IsAny<int>())).Returns(Task.CompletedTask);
            Assert.DoesNotThrowAsync(async () => await mock.Object.Replay(42));
        }
    }
}
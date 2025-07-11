using NUnit.Framework;
using Moq;
using System.Threading.Tasks;

namespace SourceFlow.Core.Tests.Interfaces
{
    public class IEventReplayerTests
    {
        [Test]
        public async Task ReplayEventsAsync_DoesNotThrow()
        {
            var mock = new Mock<IEventReplayer>();
            mock.Setup(r => r.ReplayEvents(It.IsAny<int>())).Returns(Task.CompletedTask);
            Assert.DoesNotThrowAsync(async () => await mock.Object.ReplayEvents(42));
        }
    }
}
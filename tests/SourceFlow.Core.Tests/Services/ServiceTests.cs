using Microsoft.Extensions.Logging;
using Moq;
using SourceFlow.Aggregate;
using SourceFlow.Services;

namespace SourceFlow.Core.Tests.Services
{
    [TestFixture]
    public class ServiceTests
    {
        public class TestService : Service
        {
            public TestService() : this(new Mock<IAggregateFactory>().Object, new Mock<ILogger>().Object)
            {
            }

            public TestService(IAggregateFactory factory, ILogger logger)
            {
                aggregateFactory = factory;
                this.logger = logger;
            }

            public new Task<TAggregate> CreateAggregate<TAggregate>() where TAggregate : class, IAggregate => base.CreateAggregate<TAggregate>();
        }

        [Test]
        public async Task CreateAggregate_DelegatesToFactory()
        {
            var aggregateMock = new Mock<IAggregate>();
            var factoryMock = new Mock<IAggregateFactory>();
            factoryMock.Setup(f => f.Create<IAggregate>()).ReturnsAsync(aggregateMock.Object);
            var logger = new Mock<ILogger>().Object;
            var service = new TestService(factoryMock.Object, logger);
            var result = await service.CreateAggregate<IAggregate>();
            Assert.IsNotNull(result);
            Assert.AreSame(aggregateMock.Object, result);
        }
    }
}
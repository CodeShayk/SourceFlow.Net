using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using SourceFlow.Aggregate;
using SourceFlow.Impl;

namespace SourceFlow.Core.Tests.Impl
{
    [TestFixture]
    public class AggregateFactoryTests
    {
        [Test]
        public void Constructor_SetsServiceProvider()
        {
            var sp = new Mock<IServiceProvider>().Object;
            var factory = new AggregateFactory(sp);
            Assert.IsNotNull(factory);
        }

        [Test]
        public async Task Create_ReturnsAggregateInstance()
        {
            var aggregateMock = new Mock<IAggregate>();
            var spMock = new Mock<IServiceProvider>();
            spMock.Setup(sp => sp.GetService(typeof(IAggregate))).Returns(aggregateMock.Object);
            var factory = new AggregateFactory(spMock.Object);
            var result = await factory.Create<IAggregate>();
            Assert.IsNotNull(result);
            Assert.AreSame(aggregateMock.Object, result);
        }
    }
} 
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using SourceFlow.Aggregate;
using SourceFlow.Impl;
using SourceFlow.Messaging.Bus;

namespace SourceFlow.Core.Tests.Impl
{
    [TestFixture]
    public class AggregateDispatcherTests
    {
        [Test]
        public void Constructor_NullAggregates_ThrowsArgumentNullException()
        {
            var loggerMock = new Mock<ILogger<IEventDispatcher>>();
            Assert.Throws<ArgumentNullException>(() => new AggregateDispatcher(null, loggerMock.Object));
        }

        [Test]
        public void Constructor_NullLogger_ThrowsArgumentNullException()
        {
            var aggregates = new List<IAggregate>();
            Assert.Throws<ArgumentNullException>(() => new AggregateDispatcher(aggregates, null));
        }

        [Test]
        public void Dispatch_ValidEvent_LogsInformation()
        {
            var loggerMock = new Mock<ILogger<IEventDispatcher>>();
            var aggregateMock = new Mock<IAggregate>();
            var aggregates = new List<IAggregate> { aggregateMock.Object };
            var dispatcher = new AggregateDispatcher(aggregates, loggerMock.Object);
            var eventMock = new DummyEvent();
            dispatcher.Dispatch(this, eventMock);
            loggerMock.Verify(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                (Func<It.IsAnyType, Exception, string>)It.IsAny<object>()),
                Times.AtLeastOnce);
        }
    }
}
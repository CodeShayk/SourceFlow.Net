using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using SourceFlow.Aggregate;
using SourceFlow.Impl;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Bus;

namespace SourceFlow.Core.Tests.Impl
{
    [TestFixture]
    public class EventQueueTests
    {
        [Test]
        public void Constructor_NullLogger_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new EventQueue(null));
        }

        [Test]
        public async Task Enqueue_NullEvent_ThrowsArgumentNullException()
        {
            var logger = new Mock<ILogger<IEventQueue>>().Object;
            var queue = new EventQueue(logger);
            await Task.Yield();
            Assert.ThrowsAsync<ArgumentNullException>(async () => await queue.Enqueue<IEvent>(null));
        }

        [Test]
        public async Task Enqueue_ValidEvent_InvokesDispatchers()
        {
            var logger = new Mock<ILogger<IEventQueue>>().Object;
            var queue = new EventQueue(logger);
            var eventMock = new DummyEvent();
            bool dispatcherCalled = false;
            queue.Dispatchers += (s, e) => dispatcherCalled = true;
            await queue.Enqueue(eventMock);
            Assert.IsTrue(dispatcherCalled);
        }
    }
}
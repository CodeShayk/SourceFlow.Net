using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using SourceFlow.Messaging.Events;
using SourceFlow.Messaging.Events.Impl;

namespace SourceFlow.Core.Tests.Impl
{
    [TestFixture]
    public class EventQueueTests
    {
        private Mock<ILogger<IEventQueue>> loggerMock;
        private Mock<IEventDispatcher> eventDispatcherMock;
        private EventQueue eventQueue;

        [SetUp]
        public void Setup()
        {
            loggerMock = new Mock<ILogger<IEventQueue>>();
            eventDispatcherMock = new Mock<IEventDispatcher>();
            eventQueue = new EventQueue(eventDispatcherMock.Object, loggerMock.Object);
        }

        [Test]
        public void Constructor_NullLogger_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new EventQueue(eventDispatcherMock.Object, null));
        }

        [Test]
        public void Constructor_NullEventDispatcher_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new EventQueue(null, loggerMock.Object));
        }

        [Test]
        public void Constructor_SetsDependencies()
        {
            Assert.That(eventQueue.eventDispatcher, Is.EqualTo(eventDispatcherMock.Object));
        }

        [Test]
        public async Task Enqueue_NullEvent_ThrowsArgumentNullException()
        {
            Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await eventQueue.Enqueue<DummyEvent>(null));
        }

        [Test]
        public async Task Enqueue_ValidEvent_DispatchesToEventDispatcher()
        {
            // Arrange
            var @event = new DummyEvent();

            // Act
            await eventQueue.Enqueue(@event);

            // Assert
            eventDispatcherMock.Verify(ed => ed.Dispatch<DummyEvent>(@event), Times.Once);
        }

        [Test]
        public async Task Enqueue_ValidEvent_LogsInformation()
        {
            // Arrange
            var @event = new DummyEvent();

            // Act
            await eventQueue.Enqueue(@event);

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
        public async Task Enqueue_ValidEvent_DispatchesAfterLogging()
        {
            // Arrange
            var @event = new DummyEvent();
            var callSequence = new System.Collections.Generic.List<string>();

            eventDispatcherMock.Setup(ed => ed.Dispatch(It.IsAny<DummyEvent>()))
                .Callback(() => callSequence.Add("Dispatch"))
                .Returns(Task.CompletedTask);

            loggerMock.Setup(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()))
                .Callback(() => callSequence.Add("Log"));

            // Act
            await eventQueue.Enqueue(@event);

            // Assert
            Assert.That(callSequence[0], Is.EqualTo("Log"));
            Assert.That(callSequence[1], Is.EqualTo("Dispatch"));
        }

        [Test]
        public async Task Enqueue_MultipleEvents_DispatchesAll()
        {
            // Arrange
            var event1 = new DummyEvent();
            var event2 = new DummyEvent();
            var event3 = new DummyEvent();

            // Act
            await eventQueue.Enqueue(event1);
            await eventQueue.Enqueue(event2);
            await eventQueue.Enqueue(event3);

            // Assert
            eventDispatcherMock.Verify(ed => ed.Dispatch(It.IsAny<DummyEvent>()), Times.Exactly(3));
        }
    }
}

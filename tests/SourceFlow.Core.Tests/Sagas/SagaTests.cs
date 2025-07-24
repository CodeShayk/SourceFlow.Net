using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using SourceFlow.Aggregate;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Bus;
using SourceFlow.Saga;

namespace SourceFlow.Core.Tests.Sagas
{
    [TestFixture]
    public class SagaTests
    {
        public class TestSaga : Saga<IEntity>, IHandles<ICommand>
        {
            public TestSaga() : this(new Mock<ICommandPublisher>().Object, new Mock<IEventQueue>().Object, new Mock<IRepository>().Object, new Mock<ILogger>().Object)
            {
            }

            public TestSaga(ICommandPublisher publisher, IEventQueue queue, IRepository repo, ILogger logger)
            {
                commandPublisher = publisher;
                eventQueue = queue;
                repository = repo;
                this.logger = logger;
            }

            public Task Handle(ICommand command) => Task.CompletedTask;

            public Task TestPublish(ICommand command) => Publish(command);

            public Task TestRaise(IEvent @event) => Raise(@event);
        }

        [Test]
        public void CanHandle_ReturnsTrueForMatchingType()
        {
            var saga = new TestSaga(null, null, null, null);
            Assert.IsTrue(Saga<IEntity>.CanHandle(saga, typeof(ICommand)));
        }

        [Test]
        public void CanHandle_ReturnsFalseForNulls()
        {
            Assert.IsFalse(Saga<IEntity>.CanHandle(null, typeof(ICommand)));
            var saga = new TestSaga(null, null, null, null);
            Assert.IsFalse(Saga<IEntity>.CanHandle(saga, null));
        }

        [Test]
        public void Publish_NullCommand_ThrowsArgumentNullException()
        {
            var saga = new TestSaga(new Mock<ICommandPublisher>().Object, null, null, null);
            Assert.ThrowsAsync<ArgumentNullException>(async () => await saga.TestPublish(null));
        }

        [Test]
        public void Publish_NullPayload_ThrowsInvalidOperationException()
        {
            var publisher = new Mock<ICommandPublisher>().Object;
            var saga = new TestSaga(publisher, null, null, null);
            var commandMock = new Mock<ICommand>();
            commandMock.Setup(c => c.Payload).Returns((IPayload)null);
            Assert.ThrowsAsync<InvalidOperationException>(async () => await saga.TestPublish(commandMock.Object));
        }

        [Test]
        public async Task Publish_ValidCommand_DelegatesToPublisher()
        {
            var publisherMock = new Mock<ICommandPublisher>();
            publisherMock.Setup(p => p.Publish(It.IsAny<ICommand>())).Returns(Task.CompletedTask);
            var saga = new TestSaga(publisherMock.Object, null, null, null);
            var payloadMock = new Mock<IPayload>();
            payloadMock.Setup(p => p.Id).Returns(1);
            var commandMock = new Mock<ICommand>();
            commandMock.Setup(c => c.Payload).Returns(payloadMock.Object);
            await saga.TestPublish(commandMock.Object);
            publisherMock.Verify(p => p.Publish(commandMock.Object), Times.Once);
        }

        [Test]
        public void Raise_NullEvent_ThrowsArgumentNullException()
        {
            var saga = new TestSaga(null, new Mock<IEventQueue>().Object, null, null);
            Assert.ThrowsAsync<ArgumentNullException>(async () => await saga.TestRaise(null));
        }

        [Test]
        public void Raise_NullPayload_ThrowsInvalidOperationException()
        {
            var queue = new Mock<IEventQueue>().Object;
            var saga = new TestSaga(null, queue, null, null);
            var eventMock = new Mock<IEvent>();
            eventMock.Setup(e => e.Payload).Returns((IEntity)null);
            Assert.ThrowsAsync<InvalidOperationException>(async () => await saga.TestRaise(eventMock.Object));
        }

        [Test]
        public async Task Raise_ValidEvent_DelegatesToQueue()
        {
            var queueMock = new Mock<IEventQueue>();
            queueMock.Setup(q => q.Enqueue(It.IsAny<IEvent>())).Returns(Task.CompletedTask);
            var saga = new TestSaga(null, queueMock.Object, null, null);
            var payloadMock = new Mock<IEntity>();
            var eventMock = new Mock<IEvent>();
            eventMock.Setup(e => e.Payload).Returns(payloadMock.Object);
            await saga.TestRaise(eventMock.Object);
            queueMock.Verify(q => q.Enqueue(eventMock.Object), Times.Once);
        }
    }
}
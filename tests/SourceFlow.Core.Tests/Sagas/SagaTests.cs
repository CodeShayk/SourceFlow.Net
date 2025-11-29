using Microsoft.Extensions.Logging;
using Moq;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Events;
using SourceFlow.Saga;

namespace SourceFlow.Core.Tests.Sagas
{
    [TestFixture]
    public class SagaTests
    {
        public class TestSaga : Saga<IEntity>, IHandles<ICommand>
        {
            public TestSaga() : base(new Lazy<ICommandPublisher>(() => new Mock<ICommandPublisher>().Object), new Mock<IEventQueue>().Object, new Mock<IEntityStoreAdapter>().Object, new Mock<ILogger<ISaga>>().Object)
            {
            }

            public TestSaga(Lazy<ICommandPublisher> publisher, IEventQueue queue, IEntityStoreAdapter repo, ILogger<ISaga> logger):base(publisher, queue, repo, logger)
            {
                commandPublisher = publisher;
                eventQueue = queue;
                entityStore = repo;
                this.logger = logger;
            }

            public Task<IEntity> Handle(IEntity entity, ICommand command) => Task.FromResult(entity);

            public Task TestPublish(ICommand command) => Publish(command);

            public Task TestRaise(IEvent @event) => Raise(@event);
        }

        [Test]
        public void CanHandle_ReturnsTrueForMatchingType()
        {
            var saga = new TestSaga();
            Assert.IsTrue(Saga<IEntity>.CanHandle(saga, typeof(ICommand)));
        }

        [Test]
        public void CanHandle_ReturnsFalseForNulls()
        {
            Assert.IsFalse(Saga<IEntity>.CanHandle(null, typeof(ICommand)));
            var saga = new TestSaga();
            Assert.IsFalse(Saga<IEntity>.CanHandle(saga, null));
        }

        [Test]
        public void Publish_NullCommand_ThrowsArgumentNullException()
        {
            var saga = new TestSaga();
            Assert.ThrowsAsync<ArgumentNullException>(async () => await saga.TestPublish(null!));
        }

        [Test]
        public void Publish_NullPayload_ThrowsInvalidOperationException()
        {
            var saga = new TestSaga();
            var commandMock = new Mock<ICommand>();
            commandMock.Setup(c => c.Payload).Returns((IPayload?)null!);
            Assert.ThrowsAsync<InvalidOperationException>(async () => await saga.TestPublish(commandMock.Object));
        }

        [Test]
        public async Task Publish_ValidCommand_DelegatesToPublisher()
        {
            var publisherMock = new Mock<ICommandPublisher>();
            publisherMock.Setup(p => p.Publish(It.IsAny<ICommand>())).Returns(Task.CompletedTask);
            var saga = new TestSaga(new Lazy<ICommandPublisher>(() => publisherMock.Object), new Mock<IEventQueue>().Object, new Mock<IEntityStoreAdapter>().Object, new Mock<ILogger<ISaga>>().Object);
            var payloadMock = new Mock<IPayload>();

            var commandMock = new Mock<ICommand>();
            commandMock.Setup(c => c.Payload).Returns(payloadMock.Object);
            commandMock.Setup(p => p.Entity).Returns(new EntityRef { Id=1});
            await saga.TestPublish(commandMock.Object);
            publisherMock.Verify(p => p.Publish(commandMock.Object), Times.Once);
        }

        [Test]
        public void Raise_NullEvent_ThrowsArgumentNullException()
        {
            var saga = new TestSaga();
            Assert.ThrowsAsync<ArgumentNullException>(async () => await saga.TestRaise(null!));
        }

        [Test]
        public void Raise_NullPayload_ThrowsInvalidOperationException()
        {
            var saga = new TestSaga();
            var eventMock = new Mock<IEvent>();
            eventMock.Setup(e => e.Payload).Returns((IEntity?)null!);
            Assert.ThrowsAsync<InvalidOperationException>(async () => await saga.TestRaise(eventMock.Object));
        }

        [Test]
        public async Task Raise_ValidEvent_DelegatesToQueue()
        {
            var queueMock = new Mock<IEventQueue>();
            queueMock.Setup(q => q.Enqueue(It.IsAny<IEvent>())).Returns(Task.CompletedTask);
            var saga = new TestSaga(new Lazy<ICommandPublisher>(() => new Mock<ICommandPublisher>().Object), queueMock.Object, new Mock<IEntityStoreAdapter>().Object, new Mock<ILogger<ISaga>>().Object);
            var payloadMock = new Mock<IEntity>();
            var eventMock = new Mock<IEvent>();
            eventMock.Setup(e => e.Payload).Returns(payloadMock.Object);
            await saga.TestRaise(eventMock.Object);
            queueMock.Verify(q => q.Enqueue(eventMock.Object), Times.Once);
        }
    }
}
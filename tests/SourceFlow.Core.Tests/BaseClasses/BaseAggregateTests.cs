namespace SourceFlow.Tests
{
    using System;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;
    using Moq;
    using NUnit.Framework;

    namespace SourceFlow.Tests
    {
        public class BaseAggregateTests
        {
            private Mock<ICommandPublisher> _busPublisherMock;
            private Mock<ICommandReplayer> _eventReplayerMock;
            private Mock<ILogger> _loggerMock;
            private TestAggregate _aggregate;

            public class DummyEntity : IEntity
            {
                public int Id { get; set; }
            }

            public class DummyEvent : ICommand
            {
                public Guid EventId { get; set; } = Guid.NewGuid();
                public Source Entity { get; set; } = new Source(1, typeof(object));
                public bool IsReplay { get; set; }
                public DateTime OccurredOn { get; set; } = DateTime.UtcNow;
                public int SequenceNo { get; set; }
                public DateTime Timestamp => OccurredOn;
                public string EventType => nameof(DummyEvent);
                public int Version => 1;
            }

            // Concrete implementation for testing
            public class TestAggregate : BaseAggregate<DummyEntity>
            {
                public TestAggregate(ICommandPublisher busPublisher, ICommandReplayer commandReplayer, ILogger logger)
                {
                    this.commandPublisher = busPublisher;
                    this.commandReplayer = commandReplayer;
                    this.logger = logger;
                }

                public Task TestPublishAsync(ICommand @event) => PublishAsync(@event);
            }

            [SetUp]
            public void SetUp()
            {
                _busPublisherMock = new Mock<ICommandPublisher>();
                _eventReplayerMock = new Mock<ICommandReplayer>();
                _loggerMock = new Mock<ILogger>();
                _aggregate = new TestAggregate(_busPublisherMock.Object, _eventReplayerMock.Object, _loggerMock.Object);
            }

            [Test]
            public async Task ReplayEvents_DelegatesToEventReplayer()
            {
                _eventReplayerMock.Setup(r => r.Replay(It.IsAny<int>())).Returns(Task.CompletedTask);

                await _aggregate.ReplayEvents(42);

                _eventReplayerMock.Verify(r => r.Replay(42), Times.Once);
            }

            [Test]
            public void PublishAsync_ThrowsArgumentNullException_WhenEventIsNull()
            {
                Assert.ThrowsAsync<ArgumentNullException>(async () => await _aggregate.TestPublishAsync(null));
            }

            [Test]
            public async Task PublishAsync_CallsBusPublisher()
            {
                var dummyEvent = new DummyEvent();
                _busPublisherMock.Setup(b => b.Publish(It.IsAny<ICommand>())).Returns(Task.CompletedTask);

                await _aggregate.TestPublishAsync(dummyEvent);

                _busPublisherMock.Verify(b => b.Publish(dummyEvent), Times.Once);
            }
        }
    }
}
using Microsoft.Extensions.Logging;
using Moq;
using SourceFlow.Aggregate;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Events;
using SourceFlow.Projections;
using SourceFlow.Saga;

namespace SourceFlow.Tests.Ioc
{
    public class TestEntity : IEntity
    {
        public int Id { get; set; }
    }

    public class TestPayload : IPayload
    {
        // Empty implementation for test
    }

    public class TestCommand : ICommand, IName, IMetadata
    {
        public string Name { get; set; } = "TestCommand";
        public EntityRef Entity { get; set; } = new EntityRef { Id = 1, IsNew = false };
        public IPayload Payload { get; set; } = new TestPayload();
        public Metadata Metadata { get; set; } = new Metadata();
    }

    public class TestEvent : IEvent, IName, IMetadata
    {
        public string Name { get; set; } = "TestEvent";
        public IEntity Payload { get; set; } = new TestEntity();
        public Metadata Metadata { get; set; } = new Metadata();
    }

    internal class TestAggregate : Aggregate<TestEntity>, ITestAggregate, IHandles<TestCommand>
    {
        public TestAggregate(Lazy<ICommandPublisher> commandPublisher, ILogger<IAggregate> logger)
            : base(commandPublisher, logger) { }

        public Task Handle(IEntity entity, TestCommand command)
        {
            // Implementation not needed for test
            return Task.CompletedTask;
        }
    }

    internal class TestSaga : Saga<TestEntity>, ITestSaga, IHandles<TestCommand>
    {
        public TestSaga(Lazy<ICommandPublisher> commandPublisher, IEventQueue eventQueue,
            IEntityStoreAdapter repository, ILogger<ISaga> logger)
            : base(commandPublisher, eventQueue, repository, logger) { }

        public Task Handle(IEntity entity, TestCommand command)
        {
            // Implementation not needed for test
            return Task.CompletedTask;
        }
    }

    public interface ITestAggregate { }

    public interface ITestSaga { }

    public class TestProjection : View, IProjectOn<TestEvent>
    {
        public TestProjection() : base(new Mock<IViewModelStoreAdapter>().Object, new Mock<ILogger<IView>>().Object)
        {
        }

        public Task Apply(TestEvent @event)
        {
            
            // Implementation not needed for test
            return Task.CompletedTask;
        }
    }
}
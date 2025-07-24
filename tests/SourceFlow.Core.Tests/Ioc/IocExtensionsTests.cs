using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using SourceFlow;
using SourceFlow.Aggregate;
using SourceFlow.Impl;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Bus;
using SourceFlow.Saga;
using SourceFlow.Services;

namespace SourceFlow.Core.Tests.Ioc
{
    public class DummyService : IService
    {
        public DummyService()
        { }

        public Task<TAggregateRoot> CreateAggregate<TAggregateRoot>() where TAggregateRoot : class, IAggregate
        {
            var mock = new Mock<TAggregateRoot>();
            return Task.FromResult(mock.Object);
        }
    }

    public class DummyAggregate : Aggregate<DummyEntity>
    {
        public DummyAggregate()
        { }

        public DummyAggregate(ICommandPublisher publisher, ICommandReplayer replayer, ILogger logger)
        {
            commandPublisher = publisher;
            commandReplayer = replayer;
            this.logger = logger;
        }
    }

    public class DummySaga : ISaga<DummyEntity>
    {
        public Task Handle<TCommand>(TCommand command) where TCommand : ICommand => Task.CompletedTask;
    }

    public class DummyEntity : IEntity
    { public int Id { get; set; } }

    [TestFixture]
    public class IocExtensionsTests
    {
        [Test]
        public void UseSourceFlow_AddsExpectedServices()
        {
            var services = new ServiceCollection();
            services.UseSourceFlow();
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            });
            // Should register core types
            Assert.IsTrue(services.Count > 0);
        }

        [Test]
        public void UseSourceFlow_WithCustomConfig_AddsExpectedServices()
        {
            var services = new ServiceCollection();
            services.UseSourceFlow(cfg => { });
            Assert.IsTrue(services.Count > 0);
        }

        [Test]
        public void UseSourceFlow_ResolvesCoreServices()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            });

            services.UseSourceFlow();

            var provider = services.BuildServiceProvider();
            Assert.IsNotNull(provider.GetService<AggregateDispatcher>());
            Assert.IsNotNull(provider.GetService<ProjectionDispatcher>());
            Assert.IsNotNull(provider.GetService<SagaDispatcher>());
            Assert.IsNotNull(provider.GetService<ICommandBus>());
            Assert.IsNotNull(provider.GetService<IEventQueue>());
            Assert.IsNotNull(provider.GetService<ICommandPublisher>());
            Assert.IsNotNull(provider.GetService<ICommandReplayer>());
            Assert.IsNotNull(provider.GetService<IAggregateFactory>());
        }

        [Test]
        public void WithService_RegistersService()
        {
            var config = new IocExtensions.SourceFlowConfig { Services = new ServiceCollection() };
            config.WithService<DummyService>();
            Assert.IsTrue(config.Services.Count > 0);
        }

        [Test]
        public void WithService_ResolvesService()
        {
            var config = new IocExtensions.SourceFlowConfig { Services = new ServiceCollection() };
            config.Services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            });

            config.WithService<DummyService>();
            var provider = config.Services.BuildServiceProvider();
            Assert.IsNotNull(provider.GetService<DummyService>());
            Assert.IsNotNull(provider.GetService<IService>());
        }

        [Test]
        public void WithAggregate_RegistersAggregate()
        {
            var config = new IocExtensions.SourceFlowConfig { Services = new ServiceCollection() };
            config.Services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            });
            config.Services.AddSingleton<ICommandPublisher, CommandPublisher>();
            config.Services.AddSingleton<ICommandReplayer, CommandReplayer>();

            config.WithAggregate<DummyAggregate>(c => new DummyAggregate(c.GetService<ICommandPublisher>(), c.GetService<ICommandReplayer>(), c.GetService<ILogger>()));
            Assert.IsTrue(config.Services.Count > 0);
        }

        [Test]
        public void WithAggregate_ResolvesAggregate()
        {
            var config = new IocExtensions.SourceFlowConfig { Services = new ServiceCollection() };
            config.Services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            });

            config.Services.AddSingleton<ICommandBus, CommandBus>(c => new CommandBus(new Mock<ICommandStore>().Object, c.GetService<ILogger<ICommandBus>>()));
            config.Services.AddSingleton<ICommandPublisher, CommandPublisher>();
            config.Services.AddSingleton<ICommandReplayer, CommandReplayer>();

            config.WithAggregate<DummyAggregate>();

            var provider = config.Services.BuildServiceProvider();
            Assert.IsNotNull(provider.GetService<DummyAggregate>());
            Assert.IsNotNull(provider.GetService<IAggregate>());
        }

        [Test]
        public void WithSaga_RegistersSaga()
        {
            var config = new IocExtensions.SourceFlowConfig { Services = new ServiceCollection() };
            config.WithSaga<DummyEntity, DummySaga>();
            Assert.IsTrue(config.Services.Count > 0);
        }

        [Test]
        public void WithSaga_ResolvesSaga()
        {
            var config = new IocExtensions.SourceFlowConfig { Services = new ServiceCollection() };
            config.Services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            });

            config.WithSaga<DummyEntity, DummySaga>();
            config.Services.AddSingleton<SagaDispatcher>();

            var provider = config.Services.BuildServiceProvider();
            Assert.IsNotNull(provider.GetService<ISaga>());
        }

        [Test]
        public void WithServices_ThrowsIfFactoryReturnsNull()
        {
            var config = new IocExtensions.SourceFlowConfig { Services = new ServiceCollection() };
            Assert.Throws<InvalidOperationException>(() => config.WithServices(_ => null));
        }

        [Test]
        public void WithAggregates_ThrowsIfFactoryReturnsNull()
        {
            var config = new IocExtensions.SourceFlowConfig { Services = new ServiceCollection() };
            Assert.Throws<InvalidOperationException>(() => config.WithAggregates(_ => null));
        }

        [Test]
        public void WithSagas_ThrowsIfFactoryReturnsNull()
        {
            var config = new IocExtensions.SourceFlowConfig { Services = new ServiceCollection() };
            Assert.Throws<InvalidOperationException>(() => config.WithSagas(_ => null));
        }
    }
}
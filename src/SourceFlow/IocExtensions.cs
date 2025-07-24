using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SourceFlow.Aggregate;
using SourceFlow.Impl;
using SourceFlow.Messaging.Bus;
using SourceFlow.Projections;
using SourceFlow.Saga;
using SourceFlow.Services;

namespace SourceFlow
{
    /// <summary>
    /// Extension methods for setting up SourceFlow using ioc container.
    /// </summary>
    public static class IocExtensions
    {
        /// <summary>
        /// Configures the SourceFlow with aggregates, sagas and services with IoC Container.
        /// Only supports when aggregates, sagas and services can be initialized with default constructor.
        /// </summary>
        /// <param name="services"></param>
        public static void UseSourceFlow(this IServiceCollection services)
        {
            UseSourceFlow(services, config =>
            {
                config.WithAggregates();
                config.WithSagas();
                config.WithServices();
            });
        }

        /// <summary>
        /// Configures the SourceFlow with aggregates, sagas and services with IoC Container.
        /// Supports custom configuration for aggregates, sagas and services.
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        public static void UseSourceFlow(this IServiceCollection services, Action<ISourceFlowConfig> configuration)
        {
            services.AddAsImplementationsOfInterface<IRepository>(lifetime: ServiceLifetime.Singleton);
            services.AddAsImplementationsOfInterface<IViewProvider>(lifetime: ServiceLifetime.Singleton);
            services.AddAsImplementationsOfInterface<ICommandStore>(lifetime: ServiceLifetime.Singleton);
            services.AddAsImplementationsOfInterface<IProjection>(lifetime: ServiceLifetime.Singleton);

            services.AddSingleton<SagaDispatcher>(c => new SagaDispatcher(
                c.GetService<ILogger<ICommandDispatcher>>()));

            services.AddSingleton<ICommandDispatcher, SagaDispatcher>(c => new SagaDispatcher(
              c.GetService<ILogger<ICommandDispatcher>>()));

            services.AddSingleton<ICommandBus, CommandBus>(c =>
            {
                var commandBus = new CommandBus(
                c.GetService<ICommandStore>(),
                c.GetService<ILogger<ICommandBus>>());

                var dispatcher = c.GetService<SagaDispatcher>();
                commandBus.Dispatchers += dispatcher.Dispatch;

                return commandBus;
            });

            services.AddSingleton<AggregateDispatcher>(c => new AggregateDispatcher(
                        c.GetServices<IAggregate>(),
                        c.GetService<ILogger<IEventDispatcher>>())
            );

            services.AddSingleton<IEventDispatcher, AggregateDispatcher>(c => new AggregateDispatcher(
                       c.GetServices<IAggregate>(),
                       c.GetService<ILogger<IEventDispatcher>>())
           );

            services.AddSingleton<ProjectionDispatcher>(c => new ProjectionDispatcher(
                        c.GetServices<IProjection>(),
                        c.GetService<ILogger<IEventDispatcher>>())
            );

            services.AddSingleton<IEventDispatcher, ProjectionDispatcher>(c => new ProjectionDispatcher(
                       c.GetServices<IProjection>(),
                       c.GetService<ILogger<IEventDispatcher>>())
           );
            services.AddSingleton<IEventQueue, EventQueue>(c =>
            {
                var queue = new EventQueue(
                c.GetService<ILogger<IEventQueue>>());
                // need to register event handlers for the projection before aggregates
                var projectionDispatcher = c.GetService<ProjectionDispatcher>();
                queue.Dispatchers += projectionDispatcher.Dispatch;
                // need to register event handlers for the aggregates after projections
                var aggregateDispatcher = c.GetService<AggregateDispatcher>();
                queue.Dispatchers += aggregateDispatcher.Dispatch;

                return queue;
            });

            services.AddSingleton<IAggregateFactory, AggregateFactory>();
            services.AddSingleton<ICommandPublisher, CommandPublisher>(c => new CommandPublisher(c.GetService<ICommandBus>()));
            services.AddSingleton<ICommandReplayer, CommandReplayer>(c => new CommandReplayer(c.GetService<ICommandBus>()));

            configuration(new SourceFlowConfig { Services = services });

            //var serviceProvider = services.BuildServiceProvider();
            //var accountService = serviceProvider.GetRequiredService<IAccountService>();
            //var saga = serviceProvider.GetRequiredService<ISaga>();
            //var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
            //var dataView = serviceProvider.GetRequiredService<IDataView>();
        }

        /// <summary>
        /// Registers a service with the SourceFlow configuration.
        /// When factory is not provided, uses default constructor to create service instance.
        /// </summary>
        /// <typeparam name="TService">Service Type that implements IService.</typeparam>
        /// <param name="config"></param>
        /// <param name="factory">Factory to return service instance using service provider.</param>
        /// <returns></returns>
        public static ISourceFlowConfig WithService<TService>(this ISourceFlowConfig config, Func<IServiceProvider, TService> factory = null)
        where TService : class, IService, new()
        {
            ((SourceFlowConfig)config).Services.AddSingleton(c =>
            {
                var serviceInstance = factory != null ? factory(c) : new TService();

                typeof(TService)
                  .GetField("aggregateFactory", BindingFlags.Instance | BindingFlags.NonPublic)
                  ?.SetValue(serviceInstance, c.GetRequiredService<IAggregateFactory>());

                typeof(TService)
                  .GetField("logger", BindingFlags.Instance | BindingFlags.NonPublic)
                  ?.SetValue(serviceInstance, c.GetService<ILogger<TService>>());

                return serviceInstance;
            });

            var interfaces = typeof(TService).GetInterfaces();

            foreach (var intrface in interfaces)
            {
                ((SourceFlowConfig)config).Services.AddSingleton(intrface, c =>
                {
                    var serviceInstance = factory != null ? factory(c) : new TService();

                    typeof(TService)
                      .GetField("aggregateFactory", BindingFlags.Instance | BindingFlags.NonPublic)
                      ?.SetValue(serviceInstance, c.GetRequiredService<IAggregateFactory>());

                    typeof(TService)
                      .GetField("logger", BindingFlags.Instance | BindingFlags.NonPublic)
                      ?.SetValue(serviceInstance, c.GetService<ILogger<TService>>());

                    return serviceInstance;
                });
            }

            return config;
        }

        /// <summary>
        /// Registers an aggregate with the SourceFlow configuration.
        /// When no factory is provided, uses default constructor to create aggregate instance.
        /// </summary>
        /// <typeparam name="TAggregate">Aggregate implementation of IAggregate.</typeparam>
        /// <param name="config"></param>
        /// <param name="factory">Factory to return aggrgate instance using service provider.</param>
        /// <returns></returns>
        public static ISourceFlowConfig WithAggregate<TAggregate>(this ISourceFlowConfig config, Func<IServiceProvider, TAggregate> factory = null)
        where TAggregate : class, IAggregate, new()
        {
            ((SourceFlowConfig)config).Services.AddSingleton<IAggregate, TAggregate>(c =>
            {
                var aggrgateInstance = factory != null ? factory(c) : new TAggregate();

                typeof(TAggregate)
                  .GetField("commandPublisher", BindingFlags.Instance | BindingFlags.NonPublic)
                  ?.SetValue(aggrgateInstance, c.GetRequiredService<ICommandPublisher>());

                typeof(TAggregate)
                  .GetField("commandReplayer", BindingFlags.Instance | BindingFlags.NonPublic)
                  ?.SetValue(aggrgateInstance, c.GetRequiredService<ICommandReplayer>());

                typeof(TAggregate)
                  .GetField("logger", BindingFlags.Instance | BindingFlags.NonPublic)
                  ?.SetValue(aggrgateInstance, c.GetService<ILogger<TAggregate>>());

                return aggrgateInstance;
            });

            ((SourceFlowConfig)config).Services.AddSingleton<TAggregate>(c =>
            {
                var aggrgateInstance = factory != null ? factory(c) : new TAggregate();

                typeof(TAggregate)
                  .GetField("commandPublisher", BindingFlags.Instance | BindingFlags.NonPublic)
                  ?.SetValue(aggrgateInstance, c.GetRequiredService<ICommandPublisher>());

                typeof(TAggregate)
                  .GetField("commandReplayer", BindingFlags.Instance | BindingFlags.NonPublic)
                  ?.SetValue(aggrgateInstance, c.GetRequiredService<ICommandReplayer>());

                typeof(TAggregate)
                  .GetField("logger", BindingFlags.Instance | BindingFlags.NonPublic)
                  ?.SetValue(aggrgateInstance, c.GetService<ILogger<TAggregate>>());

                return aggrgateInstance;
            });

            return config;
        }

        /// <summary>
        /// Registers a saga with the SourceFlow configuration.
        /// When no factory is provided, uses default constructor to create saga instance.
        /// </summary>
        /// <typeparam name="TAggregate">Aggregate implementation supported by TSaga. Implementation of IAggregate.</typeparam>
        /// <typeparam name="TSaga">Saga that implementation for a given Aggregate. Implementation of ISaga<TAggregate>.</typeparam>
        /// <param name="config"></param>
        /// <param name="factory">Factory to return aggrgate instance using service provider.</param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        public static ISourceFlowConfig WithSaga<TAggregate, TSaga>(this ISourceFlowConfig config, Func<IServiceProvider, ISaga<TAggregate>> factory = null)
        where TAggregate : IEntity
        where TSaga : class, ISaga<TAggregate>, new()
        {
            ((SourceFlowConfig)config).Services.AddSingleton<ISaga, TSaga>(c =>
            {
                var saga = factory != null ? factory(c) : new TSaga();

                if (saga == null)
                    throw new InvalidOperationException($"Saga registration for {typeof(TAggregate).Name} returned null.");

                typeof(TSaga)
                    .GetField("commandPublisher", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(saga, c.GetRequiredService<ICommandPublisher>());

                typeof(TSaga)
                          .GetField("eventQueue", BindingFlags.Instance | BindingFlags.NonPublic)
                           ?.SetValue(saga, c.GetRequiredService<IEventQueue>());

                typeof(TSaga)
                    .GetField("logger", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(saga, c.GetService<ILogger<TSaga>>());

                typeof(TSaga)
                    .GetField("repository", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(saga, c.GetRequiredService<IRepository>());

                var dispatcher = c.GetRequiredService<SagaDispatcher>();
                dispatcher.Register(saga);

                return (TSaga)saga;
            });

            return config;
        }

        /// <summary>
        /// Registers all implementations of a given interface in the IoC container.
        /// </summary>
        /// <typeparam name="TInterface"></typeparam>
        /// <param name="services"></param>
        /// <param name="lifetime"></param>
        /// <returns></returns>
        private static IServiceCollection AddAsImplementationsOfInterface<TInterface>(this IServiceCollection services, ServiceLifetime lifetime = ServiceLifetime.Scoped)
        {
            var interfaceType = typeof(TInterface);

            var assemblies = AppDomain.CurrentDomain
                .GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location));

            var implementationTypes = assemblies
                .SelectMany(a =>
                {
                    try
                    { return a.GetTypes(); }
                    catch { return Array.Empty<Type>(); } // Prevent ReflectionTypeLoadException
                })
                .Where(t =>
                    interfaceType.IsAssignableFrom(t) &&
                    t.IsClass && !t.IsAbstract && !t.IsGenericType);

            foreach (var implType in implementationTypes)
            {
                services.TryAddEnumerable(ServiceDescriptor.Describe(interfaceType, implType, lifetime));

                var interfaces = implType.GetInterfaces().Where(t => !t.AssemblyQualifiedName.Equals(interfaceType.AssemblyQualifiedName));

                foreach (var intrface in interfaces)
                {
                    services.TryAddEnumerable(ServiceDescriptor.Describe(intrface, implType, lifetime));
                }
            }

            return services;
        }

        /// <summary>
        /// Gets all types that implement a given interface from all loaded assemblies.
        /// </summary>
        /// <param name="interfaceType"></param>
        /// <returns></returns>
        private static IEnumerable<Type> GetTypesFromAssemblies(Type interfaceType)
        {
            var assemblies = AppDomain.CurrentDomain
                .GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location));

            var implementationTypes = assemblies
                .SelectMany(a =>
                {
                    try
                    { return a.GetTypes(); }
                    catch { return Array.Empty<Type>(); } // Prevent ReflectionTypeLoadException
                })
                .Where(t =>
                    interfaceType.IsAssignableFrom(t) &&
                    t.IsClass && !t.IsAbstract && !t.IsGenericType);

            return implementationTypes;
        }

        /// <summary>
        /// Registers all services that implement the IService interface in the IoC container.
        /// When factory is not provided, uses default constructor to create service instances.
        /// </summary>
        /// <param name="config"></param>
        /// <param name="serviceFactory">Factory to return service instances by given type.</param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        public static ISourceFlowConfig WithServices(this ISourceFlowConfig config, Func<Type, IService> serviceFactory = null)
        {
            var interfaceType = typeof(IService);
            var implementationTypes = GetTypesFromAssemblies(interfaceType);

            foreach (var implType in implementationTypes)
            {
                var serviceInstance = serviceFactory != null
                            ? serviceFactory(implType)
                            : (IService)Activator.CreateInstance(implType);

                if (serviceInstance == null)
                    throw new InvalidOperationException($"Service registration for {implType.Name} returned null.");

                var loggerType = typeof(ILogger<>).MakeGenericType(implType);

                ((SourceFlowConfig)config).Services.AddSingleton(c =>
                {
                    implType
                        .GetField("aggregateFactory", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?.SetValue(serviceInstance, c.GetRequiredService<IAggregateFactory>());

                    implType
                        .GetField("logger", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?.SetValue(serviceInstance, (ILogger)c.GetService(loggerType));

                    return serviceInstance;
                });

                var interfaces = implType.GetInterfaces();

                foreach (var intrface in interfaces)
                    ((SourceFlowConfig)config).Services.AddSingleton(intrface, c =>
                    {
                        implType
                            .GetField("aggregateFactory", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?.SetValue(serviceInstance, c.GetRequiredService<IAggregateFactory>());

                        implType
                            .GetField("logger", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?.SetValue(serviceInstance, (ILogger)c.GetService(loggerType));

                        return serviceInstance;
                    });
            }

            return config;
        }

        /// <summary>
        /// Registers all aggregates that implement the IAggregate interface in the IoC container.
        /// When factory is not provided, uses default constructor to create aggrgate instances.
        /// </summary>
        /// <param name="config"></param>
        /// <param name="aggregateFactory">Factory to return aggregate instances by given type.</param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        public static ISourceFlowConfig WithAggregates(this ISourceFlowConfig config, Func<Type, IAggregate> aggregateFactory = null)
        {
            var interfaceType = typeof(IAggregate);
            var implementationTypes = GetTypesFromAssemblies(interfaceType);

            foreach (var implType in implementationTypes)
            {
                var aggrgateInstance = aggregateFactory != null
                           ? aggregateFactory(implType)
                           : (IAggregate)Activator.CreateInstance(implType);

                if (aggrgateInstance == null)
                    throw new InvalidOperationException($"Aggregate registration for {implType.Name} returned null.");

                var loggerType = typeof(ILogger<>).MakeGenericType(implType);

                ((SourceFlowConfig)config).Services.AddSingleton(implType, c =>
                {
                    implType
                        .GetField("commandPublisher", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?.SetValue(aggrgateInstance, c.GetRequiredService<ICommandPublisher>());

                    implType
                        .GetField("commandReplayer", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?.SetValue(aggrgateInstance, c.GetRequiredService<ICommandReplayer>());

                    implType
                        .GetField("logger", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?.SetValue(aggrgateInstance, (ILogger)c.GetService(loggerType));

                    return aggrgateInstance;
                });

                var interfaces = implType.GetInterfaces();

                foreach (var intrface in interfaces)
                    ((SourceFlowConfig)config).Services.AddSingleton(intrface, c =>
                    {
                        implType
                            .GetField("commandPublisher", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?.SetValue(aggrgateInstance, c.GetRequiredService<ICommandPublisher>());

                        implType
                            .GetField("commandReplayer", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?.SetValue(aggrgateInstance, c.GetRequiredService<ICommandReplayer>());

                        implType
                            .GetField("logger", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?.SetValue(aggrgateInstance, (ILogger)c.GetService(loggerType));

                        return aggrgateInstance;
                    });
            }

            return config;
        }

        /// <summary>
        /// Registers all sagas that implement the ISaga interface in the IoC container.
        /// When factory is not provided, uses default constructor to create saga instances.
        /// </summary>
        /// <param name="config"></param>
        /// <param name="sagaFactory">Factory to return saga instances by given type.</param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        public static ISourceFlowConfig WithSagas(this ISourceFlowConfig config, Func<Type, ISaga> sagaFactory = null)
        {
            var interfaceType = typeof(ISaga);
            var implementationTypes = GetTypesFromAssemblies(interfaceType);

            foreach (var implType in implementationTypes)
            {
                var sagaInstance = sagaFactory != null
                        ? sagaFactory(implType)
                        : (ISaga)Activator.CreateInstance(implType);

                if (sagaInstance == null)
                    throw new InvalidOperationException($"Saga registration for {implType.Name} returned null.");

                var loggerType = typeof(ILogger<>).MakeGenericType(implType);

                var interfaces = implType.GetInterfaces();

                var index = 1;

                foreach (var intrface in interfaces)
                    ((SourceFlowConfig)config).Services.AddSingleton(intrface, c =>
                    {
                        implType
                            .GetField("commandPublisher", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?.SetValue(sagaInstance, c.GetRequiredService<ICommandPublisher>());

                        implType
                            .GetField("eventQueue", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?.SetValue(sagaInstance, c.GetRequiredService<IEventQueue>());

                        implType
                            .GetField("logger", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?.SetValue(sagaInstance, (ILogger)c.GetService(loggerType));

                        implType
                            .GetField("repository", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?.SetValue(sagaInstance, c.GetRequiredService<IRepository>());

                        if (index == 1)
                        {
                            var dispatcher = c.GetRequiredService<SagaDispatcher>();
                            dispatcher.Register(sagaInstance);
                        }

                        index++;

                        return sagaInstance;
                    });
            }

            return config;
        }

        /// <summary>
        /// Interface for SourceFlow configuration.
        /// </summary>
        public interface ISourceFlowConfig
        {
        }

        /// <summary>
        /// Configuration class for SourceFlow.
        /// </summary>
        public class SourceFlowConfig : ISourceFlowConfig
        {
            /// <summary>
            /// Service collection for SourceFlow configuration.
            /// </summary>
            public IServiceCollection Services { get; set; }
        }
    }
}
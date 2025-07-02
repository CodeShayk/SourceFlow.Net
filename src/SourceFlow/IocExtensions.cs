using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
            configuration(new SourceFlowConfig { Services = services });

            services.AddAsImplementationsOfInterface<IAggregateFactory>(ServiceLifetime.Singleton);
            services.AddAsImplementationsOfInterface<IAggregateRepository>(ServiceLifetime.Singleton);
            services.AddAsImplementationsOfInterface<IEventStore>(ServiceLifetime.Singleton);

            services.AddSingleton<ICommandBus, CommandBus>(c => new CommandBus(
                c.GetService<IEventStore>(),
                c.GetService<IAggregateFactory>()));

            services.AddSingleton<IBusSubscriber, BusSubscriber>(c => new BusSubscriber(c.GetService<ICommandBus>()));
            services.AddSingleton<IBusPublisher, BusPublisher>(c => new BusPublisher(c.GetService<ICommandBus>()));
            services.AddSingleton<IEventReplayer, EventReplayer>(c => new EventReplayer(c.GetService<ICommandBus>()));
        }

        /// <summary>
        /// Registers a service with the SourceFlow configuration.
        /// </summary>
        /// <typeparam name="TService"></typeparam>
        /// <param name="config"></param>
        /// <param name="service"></param>
        /// <returns></returns>
        public static ISourceFlowConfig WithService<TService>(this ISourceFlowConfig config, Func<IServiceProvider, TService> service = null)
        where TService : class, IService, new()
        {
            var interfaces = typeof(TService).GetInterfaces();

            foreach (var intrface in interfaces)
            {
                ((SourceFlowConfig)config).Services.AddSingleton(intrface, c =>
                {
                    var serviceInstance = service != null ? service(c) : new TService();

                    typeof(TService)
                     .GetField("aggregateRepository", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(serviceInstance, c.GetRequiredService<IAggregateRepository>());

                    typeof(TService)
                      .GetField("aggregateFactory", BindingFlags.Instance | BindingFlags.NonPublic)
                      ?.SetValue(serviceInstance, c.GetRequiredService<IAggregateFactory>());

                    return serviceInstance;
                });
            }

            return config;
        }

        /// <summary>
        /// Registers an aggregate with the SourceFlow configuration.
        /// </summary>
        /// <typeparam name="TAggregate"></typeparam>
        /// <param name="config"></param>
        /// <param name="aggregate"></param>
        /// <returns></returns>
        public static ISourceFlowConfig WithAggregate<TAggregate>(this ISourceFlowConfig config, Func<IServiceProvider, TAggregate> aggregate = null)
        where TAggregate : class, IAggregateRoot, new()
        {
            ((SourceFlowConfig)config).Services.AddTransient<IAggregateRoot, TAggregate>(c =>
            {
                var aggrgateInstance = aggregate != null ? aggregate(c) : new TAggregate();

                typeof(TAggregate)
                  .GetField("busPublisher", BindingFlags.Instance | BindingFlags.NonPublic)
                  ?.SetValue(aggrgateInstance, c.GetRequiredService<IBusPublisher>());

                typeof(TAggregate)
                  .GetField("eventReplayer", BindingFlags.Instance | BindingFlags.NonPublic)
                  ?.SetValue(aggrgateInstance, c.GetRequiredService<IEventReplayer>());

                return aggrgateInstance;
            });

            return config;
        }

        /// <summary>
        /// Registers a saga with the SourceFlow configuration.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <typeparam name="TSaga"></typeparam>
        /// <param name="config"></param>
        /// <param name="sagaRegister"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        public static ISourceFlowConfig WithSaga<T, TSaga>(this ISourceFlowConfig config, Func<IServiceProvider, ISaga<T>> sagaRegister = null)
        where T : IAggregateRoot
        where TSaga : class, ISaga<T>, new()
        {
            ((SourceFlowConfig)config).Services.AddSingleton<ISagaHandler, TSaga>(c =>
            {
                var saga = sagaRegister != null ? sagaRegister(c) : new TSaga();

                if (saga == null)
                    throw new InvalidOperationException($"Saga registration for {typeof(T).Name} returned null.");

                var subscriber = c.GetRequiredService<IBusSubscriber>();
                subscriber.Subscribe(saga);

                typeof(TSaga)
                    .GetField("busSubscriber", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(saga, subscriber);

                typeof(TSaga)
                    .GetField("busPublisher", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(saga, c.GetRequiredService<IBusPublisher>());

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
                // Use TryAddEnumerable to allow multiple implementations (IEnumerable<TInterface>)
                services.TryAddEnumerable(ServiceDescriptor.Describe(interfaceType, implType, lifetime));
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
        /// </summary>
        /// <param name="config"></param>
        /// <param name="serviceFactory">Factory to return service instances for given type.</param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        public static ISourceFlowConfig WithServices(this ISourceFlowConfig config, Func<Type, IService> serviceFactory = null)
        {
            var interfaceType = typeof(IService);
            var implementationTypes = GetTypesFromAssemblies(interfaceType);

            foreach (var implType in implementationTypes)
            {
                var interfaces = implType.GetInterfaces();

                foreach (var intrface in interfaces)
                    ((SourceFlowConfig)config).Services.AddSingleton(intrface, c =>
                    {
                        var serviceInstance = serviceFactory != null
                            ? serviceFactory(implType)
                            : (IService)Activator.CreateInstance(implType);

                        if (serviceInstance == null)
                            throw new InvalidOperationException($"Service registration for {implType.Name} returned null.");

                        implType
                            .GetField("aggregateRepository", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?.SetValue(serviceInstance, c.GetRequiredService<IAggregateRepository>());

                        implType
                            .GetField("aggregateFactory", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?.SetValue(serviceInstance, c.GetRequiredService<IAggregateFactory>());

                        return serviceInstance;
                    });
            }

            return config;
        }

        /// <summary>
        /// Registers all aggregates that implement the IAggregateRoot interface in the IoC container.
        /// </summary>
        /// <param name="config"></param>
        /// <param name="aggregateFactory">Factory to return aggregate instances for given type.</param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        public static ISourceFlowConfig WithAggregates(this ISourceFlowConfig config, Func<Type, IAggregateRoot> aggregateFactory = null)
        {
            var interfaceType = typeof(IAggregateRoot);
            var implementationTypes = GetTypesFromAssemblies(interfaceType);

            foreach (var implType in implementationTypes)
            {
                var interfaces = implType.GetInterfaces();

                foreach (var intrface in interfaces)
                    ((SourceFlowConfig)config).Services.AddSingleton(intrface, c =>
                    {
                        var aggrgateInstance = aggregateFactory != null
                            ? aggregateFactory(implType)
                            : (IAggregateRoot)Activator.CreateInstance(implType);

                        if (aggrgateInstance == null)
                            throw new InvalidOperationException($"Aggregate registration for {implType.Name} returned null.");

                        implType
                            .GetField("busPublisher", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?.SetValue(aggrgateInstance, c.GetRequiredService<IBusPublisher>());

                        implType
                          .GetField("eventReplayer", BindingFlags.Instance | BindingFlags.NonPublic)
                          ?.SetValue(aggrgateInstance, c.GetRequiredService<IEventReplayer>());

                        return aggrgateInstance;
                    });
            }

            return config;
        }

        /// <summary>
        /// Registers all sagas that implement the ISagaHandler interface in the IoC container.
        /// </summary>
        /// <param name="config"></param>
        /// <param name="sagaFactory">Factory to return saga instances for given type.</param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        public static ISourceFlowConfig WithSagas(this ISourceFlowConfig config, Func<Type, ISagaHandler> sagaFactory = null)
        {
            var interfaceType = typeof(ISagaHandler);
            var implementationTypes = GetTypesFromAssemblies(interfaceType);

            foreach (var implType in implementationTypes)
            {
                var interfaces = implType.GetInterfaces();

                foreach (var intrface in interfaces)
                    ((SourceFlowConfig)config).Services.AddSingleton(intrface, c =>
                    {
                        var sagaInstance = sagaFactory != null
                            ? sagaFactory(implType)
                            : (ISagaHandler)Activator.CreateInstance(implType);

                        if (sagaInstance == null)
                            throw new InvalidOperationException($"Saga registration for {implType.Name} returned null.");

                        var subscriber = c.GetRequiredService<IBusSubscriber>();
                        subscriber.Subscribe(sagaInstance);

                        implType
                            .GetField("busSubscriber", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?.SetValue(sagaInstance, subscriber);

                        implType
                            .GetField("busPublisher", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?.SetValue(sagaInstance, c.GetRequiredService<IBusPublisher>());

                        return sagaInstance;
                    });
            }

            return config;
        }
    }
}
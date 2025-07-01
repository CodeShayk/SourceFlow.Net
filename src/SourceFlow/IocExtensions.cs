using System;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SourceFlow
{
    public static class IocExtensions
    {
        public static void UseSourceFlow(this IServiceCollection services, Action<SourceFlowConfig> configuration)
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
        }

        public static SourceFlowConfig WithService<TService>(this SourceFlowConfig config, Func<IServiceProvider, TService> service)
        where TService : class, ICommandService
        {
            if (service == null)
                throw new ArgumentNullException(nameof(service));

            var interfaces = typeof(TService).GetInterfaces();

            foreach (var intrface in interfaces)
            {
                config.Services.AddSingleton(intrface, c =>
                {
                    var serviceInstance = service(c);

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

        public static SourceFlowConfig WithAggregate<TAggregate>(this SourceFlowConfig config, Func<IServiceProvider, TAggregate> aggregate)
        where TAggregate : class, IAggregateRoot
        {
            if (aggregate == null)
                throw new ArgumentNullException(nameof(aggregate));

            config.Services.AddTransient<IAggregateRoot, TAggregate>(c =>
            {
                var aggrgateInstance = aggregate(c);

                typeof(TAggregate)
                  .GetField("busPublisher", BindingFlags.Instance | BindingFlags.NonPublic)
                  ?.SetValue(aggrgateInstance, c.GetRequiredService<IBusPublisher>());

                return aggrgateInstance;
            });

            return config;
        }

        public static SourceFlowConfig WithSaga<T, TSaga>(this SourceFlowConfig config, Func<IServiceProvider, ISaga<T>> sagaRegister)
        where T : IAggregateRoot
        where TSaga : class, ISaga<T>
        {
            if (sagaRegister == null)
                throw new ArgumentNullException(nameof(sagaRegister));

            config.Services.AddSingleton<ISagaHandler, TSaga>(c =>
            {
                var saga = sagaRegister(c);

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
    }

    public class SourceFlowConfig
    {
        public IServiceCollection Services { get; set; }

        //private Func<IServiceProvider, ISaga<T>> sagaRegister
    }
}
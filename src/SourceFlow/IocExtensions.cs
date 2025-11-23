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
using SourceFlow.Messaging.Bus.Impl;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Commands.Impl;
using SourceFlow.Messaging.Events;
using SourceFlow.Messaging.Events.Impl;
using SourceFlow.Projections;
using SourceFlow.Saga;

namespace SourceFlow
{
    /// <summary>
    /// Extension methods for setting up SourceFlow using IoC container.
    /// </summary>
    public static class IocExtensions
    {
        /// <summary>
        /// Configures the SourceFlow with aggregates, sagas and services with IoC Container.
        /// </summary>
        /// <param name="services">The service collection to register services with.</param>
        /// <param name="assemblies">Optional parameter to specify assemblies to scan for implementations.
        /// If not provided, uses the current SourceFlow assembly and calling assembly.</param>
        /// <param name="lifetime">The service lifetime to use for registered services (default: Singleton).</param>
        public static void UseSourceFlow(this IServiceCollection services, params Assembly[] assemblies)
        {
            UseSourceFlow(services, ServiceLifetime.Singleton, assemblies);
        }

        /// <summary>
        /// Configures the SourceFlow with aggregates, sagas and services with IoC Container.
        /// </summary>
        /// <param name="services">The service collection to register services with.</param>
        /// <param name="lifetime">The service lifetime to use for registered services.</param>
        /// <param name="assemblies">Optional parameter to specify assemblies to scan for implementations.
        /// If not provided, uses the current SourceFlow assembly and calling assembly.</param>
        public static void UseSourceFlow(this IServiceCollection services, ServiceLifetime lifetime, params Assembly[] assemblies)
        {
            // If no assemblies are specified, scan assemblies that contain SourceFlow types and calling assembly
            if (assemblies.Length == 0)
            {
                assemblies = new[] {
                    Assembly.GetExecutingAssembly(),
                    Assembly.GetCallingAssembly()
                };
            }

            // Register foundational services first - these have no dependencies on aggregates/sagas
            services.AddAsImplementationsOfInterface<IRepository>(assemblies, lifetime);
            services.AddAsImplementationsOfInterface<IViewProvider>(assemblies, lifetime);
            services.AddAsImplementationsOfInterface<ICommandStore>(assemblies, lifetime);
            services.AddAsImplementationsOfInterface<IProjection>(assemblies, lifetime);

            // Register factories
            services.Add(ServiceDescriptor.Describe(typeof(IAggregateFactory), typeof(AggregateFactory), lifetime));

            // Register Sagas and Aggregates with proper constructor injection
            RegisterSagasWithConstructorInjection(services, assemblies, lifetime);
            RegisterAggregatesWithConstructorInjection(services, assemblies, lifetime);

            // Register infrastructure services that depend on Sagas/Aggregates
            services.AddSingleton<ICommandSubscriber>(provider =>
                new CommandSubscriber(
                    provider.GetServices<ISaga>(),
                    provider.GetService<ILogger<ICommandSubscriber>>()));

            services.AddSingleton<ICommandDispatcher>(provider =>
                new CommandDispatcher(
                    provider.GetServices<ICommandSubscriber>(),
                    provider.GetService<ILogger<ICommandDispatcher>>()));

            services.AddSingleton<ICommandBus>(provider =>
                new CommandBus(
                    provider.GetService<ICommandDispatcher>(),
                    provider.GetService<ICommandStore>(),
                    provider.GetService<ILogger<ICommandBus>>()));

            services.AddSingleton<ICommandPublisher>(provider =>
                new CommandPublisher(provider.GetService<ICommandBus>()));

            // Register the concrete EventSubscriber implementations
            services.AddSingleton<Aggregate.EventSubscriber>(provider =>
                new Aggregate.EventSubscriber(
                    provider.GetServices<IAggregate>(),
                    provider.GetService<ILogger<Aggregate.EventSubscriber>>()));

            services.AddSingleton<Projections.EventSubscriber>(provider =>
                new Projections.EventSubscriber(
                    provider.GetServices<IProjection>(),
                    provider.GetService<ILogger<Projections.EventSubscriber>>()));

            // Register factory methods to get each implementation as IEventSubscriber
            // This allows the DI container to collect both when requesting IEnumerable<IEventSubscriber>
            services.AddSingleton<IEventSubscriber>(provider =>
                provider.GetRequiredService<Aggregate.EventSubscriber>());
            services.AddSingleton<IEventSubscriber>(provider =>
                provider.GetRequiredService<Projections.EventSubscriber>());

            services.AddSingleton<IEventDispatcher>(provider =>
                new EventDispatcher(
                    provider.GetServices<IEventSubscriber>(),
                    provider.GetService<ILogger<IEventDispatcher>>()));

            services.AddSingleton<IEventQueue>(provider =>
                new EventQueue(
                    provider.GetService<IEventDispatcher>(),
                    provider.GetService<ILogger<IEventQueue>>()));
        }

        private static void RegisterSagasWithConstructorInjection(IServiceCollection services, Assembly[] assemblies, ServiceLifetime lifetime)
        {
            var sagaTypes = GetImplementedTypes<ISaga>(assemblies);
            foreach (var sagaType in sagaTypes)
            {
                // Register as interface ISaga
                services.Add(ServiceDescriptor.Describe(typeof(ISaga), sagaType, lifetime));

                // Register as concrete type for direct access
                services.Add(ServiceDescriptor.Describe(sagaType, sagaType, lifetime));

                // Register as all other interfaces the saga implements
                var interfaces = sagaType.GetInterfaces()
                    .Where(i => i != typeof(ISaga) && i.IsPublic);
                foreach (var iface in interfaces)
                {
                    services.Add(ServiceDescriptor.Describe(iface, sagaType, lifetime));
                }
            }
        }

        private static void RegisterAggregatesWithConstructorInjection(IServiceCollection services, Assembly[] assemblies, ServiceLifetime lifetime)
        {
            var aggregateTypes = GetImplementedTypes<IAggregate>(assemblies);
            foreach (var aggregateType in aggregateTypes)
            {
                // Register as interface IAggregate
                services.Add(ServiceDescriptor.Describe(typeof(IAggregate), aggregateType, lifetime));

                // Register as concrete type for direct access
                services.Add(ServiceDescriptor.Describe(aggregateType, aggregateType, lifetime));

                // Register as all other interfaces the aggregate implements
                var interfaces = aggregateType.GetInterfaces()
                    .Where(i => i != typeof(IAggregate) && i.IsPublic);
                foreach (var iface in interfaces)
                {
                    services.Add(ServiceDescriptor.Describe(iface, aggregateType, lifetime));
                }
            }
        }

        /// <summary>
        /// Registers all implementations of a given interface in the IoC container.
        /// </summary>
        /// <typeparam name="TInterface">The interface to register implementations for.</typeparam>
        /// <param name="services">The service collection to register services with.</param>
        /// <param name="assemblies">The assemblies to scan for implementations.</param>
        /// <param name="lifetime">The service lifetime for registered implementations.</param>
        /// <returns>The service collection for chaining.</returns>
        private static IServiceCollection AddAsImplementationsOfInterface<TInterface>(this IServiceCollection services, Assembly[] assemblies, ServiceLifetime lifetime = ServiceLifetime.Singleton)
        {
            var interfaceType = typeof(TInterface);
            var implementationTypes = GetImplementedTypes(interfaceType, assemblies);

            foreach (var implType in implementationTypes)
            {
                services.Add(ServiceDescriptor.Describe(interfaceType, implType, lifetime));

                // Register implementation as all of its interfaces (except the main one)
                var interfaces = implType.GetInterfaces()
                    .Where(i => i != interfaceType && i.IsPublic)
                    .Distinct(); // Avoid duplicate registrations

                foreach (var iface in interfaces)
                {
                    services.Add(ServiceDescriptor.Describe(iface, implType, lifetime));
                }
            }

            return services;
        }

        private static IEnumerable<Type> GetImplementedTypes<TInterface>(Assembly[] assemblies)
        {
            return GetImplementedTypes(typeof(TInterface), assemblies);
        }

        private static IEnumerable<Type> GetImplementedTypes(Type interfaceType, Assembly[] assemblies)
        {
            var implementationTypes = assemblies
                .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
                .SelectMany(a =>
                {
                    try
                    {
                        return a.GetTypes()
                            .Where(t => interfaceType.IsAssignableFrom(t) &&
                                       t.IsClass &&
                                       !t.IsAbstract &&
                                       !t.IsGenericType &&
                                       !t.ContainsGenericParameters);
                    }
                    catch (ReflectionTypeLoadException)
                    {
                        // Handle cases where some types can't be loaded
                        return a.GetTypes()
                            .Where(t => t != null &&
                                       interfaceType.IsAssignableFrom(t) &&
                                       t.IsClass &&
                                       !t.IsAbstract &&
                                       !t.IsGenericType &&
                                       !t.ContainsGenericParameters);
                    }
                    catch
                    {
                        return Array.Empty<Type>();
                    }
                })
                .Distinct();

            return implementationTypes;
        }
    }
}
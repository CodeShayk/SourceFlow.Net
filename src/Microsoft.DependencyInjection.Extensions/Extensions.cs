using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Linq;

namespace Microsoft.DependencyInjection.Extensions
{
    public static class Extensions
    {
        public static IServiceCollection AddConventionalServicesFromAppDomain(this IServiceCollection services, Func<Type, bool> namingConvention = null)
        {
            if (namingConvention == null)
                namingConvention = (type => type.Name.EndsWith("Service") || type.Name.EndsWith("Repository"));

            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location)); // Skip dynamic/in-memory assemblies

            foreach (var assembly in assemblies)
            {
                RegisterFromAssembly(services, assembly, namingConvention);
            }

            return services;
        }

        private static void RegisterFromAssembly(IServiceCollection services, Assembly assembly, Func<Type, bool> namingConvention)
        {
            var types = assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract);

            foreach (var type in types)
            {
                var attr = type.GetCustomAttribute<InjectableAttribute>();
                bool matchesConvention = namingConvention(type);

                if (attr == null && !matchesConvention)
                    continue;

                var interfaces = type.GetInterfaces();
                var lifetime = attr?.Lifetime ?? ServiceLifetime.Scoped;

                if (interfaces.Length > 0)
                {
                    foreach (var iface in interfaces)
                    {
                        services.TryAddEnumerable(ServiceDescriptor.Describe(iface, type, lifetime));
                    }
                }
                else
                {
                    services.TryAddEnumerable(ServiceDescriptor.Describe(type, type, lifetime));
                }
            }
        }

        public static IServiceCollection AddAsImplementedInterfaces<TImplementation>(this IServiceCollection services, ServiceLifetime lifetime = ServiceLifetime.Scoped)
        {
            var implementationType = typeof(TImplementation);
            var interfaces = implementationType.GetInterfaces();

            foreach (var iface in interfaces)
            {
                services.Add(new ServiceDescriptor(iface, implementationType, lifetime));
            }

            return services;
        }

        public static IServiceCollection AddAsByTypeInjectedAttribute(this IServiceCollection services)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location)); // Skip dynamic/in-memory assemblies

            foreach (var assembly in assemblies)
            {
                RegisterFromAssembly(services, assembly, null);
            }

            return services;
        }

        public static IServiceCollection AddAsByTypeNameConvention(this IServiceCollection services, Func<Type, bool> namingConvention = null)
        {
            if (namingConvention == null)
                namingConvention = (type => type.Name.EndsWith("Service") || type.Name.EndsWith("Repository"));

            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location)); // Skip dynamic/in-memory assemblies

            foreach (var assembly in assemblies)
            {
                RegisterFromAssembly(services, assembly, namingConvention);
            }

            return services;
        }

        public static IServiceCollection AddAsTypesOfInterface<TInterface>(this IServiceCollection services, ServiceLifetime lifetime = ServiceLifetime.Scoped)
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

        public static IServiceCollection AddAsImplementedSubclassAndInterfacesOf(this IServiceCollection services, Type baseType, ServiceLifetime lifetime = ServiceLifetime.Scoped)
        {
            var assemblies = AppDomain.CurrentDomain
                .GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location));

            var types = assemblies
                .SelectMany(a =>
                {
                    try
                    { return a.GetTypes(); }
                    catch { return Array.Empty<Type>(); } // Skip problematic assemblies
                })
                .Where(t =>
                    t.IsClass &&
                    !t.IsAbstract &&
                    !t.IsGenericType &&
                    baseType.IsAssignableFrom(t));

            foreach (var impl in types)
            {
                // Register by base class
                services.TryAddEnumerable(ServiceDescriptor.Describe(baseType, impl, lifetime));

                // Register by each interface it implements
                var interfaces = impl.GetInterfaces();
                foreach (var iface in interfaces)
                {
                    services.TryAddEnumerable(ServiceDescriptor.Describe(iface, impl, lifetime));
                }

                // Optionally register concrete type itself
                // services.TryAddEnumerable(ServiceDescriptor.Describe(impl, impl, lifetime));
            }

            return services;
        }

        public static IServiceCollection AddAsImplementedSubclassAndInterfacesOf<TBase>(this IServiceCollection services, ServiceLifetime lifetime = ServiceLifetime.Scoped)
        {
            return AddAsImplementedSubclassAndInterfacesOf(services, typeof(TBase), lifetime);
        }
    }
}
using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SourceFlow.Net.SQL.Configuration;
using SourceFlow.Net.SQL.Stores;

namespace SourceFlow.Net.SQL.Extensions
{
    /// <summary>
    /// Extension methods for registering SQL-based persistence stores.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Registers SQL Server implementations of ICommandStore, IEntityStore, and IViewModelStore.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="configuration">SQL store configuration</param>
        /// <returns>The service collection for chaining</returns>
        public static IServiceCollection AddSourceFlowSqlStores(
            this IServiceCollection services,
            SqlStoreConfiguration configuration)
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));

            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            // Validate configuration
            configuration.Validate();

            // Register configuration as singleton
            services.TryAddSingleton(configuration);

            // Register SQL stores
            services.TryAddSingleton<ICommandStore, SqlCommandStore>();
            services.TryAddSingleton<IEntityStore, SqlEntityStore>();
            services.TryAddSingleton<IViewModelStore, SqlViewModelStore>();

            return services;
        }

        /// <summary>
        /// Registers SQL Server implementations using a configuration builder.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="configureOptions">Configuration builder action</param>
        /// <returns>The service collection for chaining</returns>
        public static IServiceCollection AddSourceFlowSqlStores(
            this IServiceCollection services,
            Action<SqlStoreConfiguration> configureOptions)
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));

            if (configureOptions == null)
                throw new ArgumentNullException(nameof(configureOptions));

            var configuration = new SqlStoreConfiguration();
            configureOptions(configuration);

            return services.AddSourceFlowSqlStores(configuration);
        }

        /// <summary>
        /// Registers SQL Server implementations using a simple connection string (same for all stores).
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="connectionString">SQL Server connection string</param>
        /// <param name="schema">Schema name (default: dbo)</param>
        /// <returns>The service collection for chaining</returns>
        public static IServiceCollection AddSourceFlowSqlStores(
            this IServiceCollection services,
            string connectionString,
            string schema = "dbo")
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));

            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string is required.", nameof(connectionString));

            var configuration = new SqlStoreConfiguration
            {
                CommandStoreConnectionString = connectionString,
                EntityStoreConnectionString = connectionString,
                ViewModelStoreConnectionString = connectionString,
                CommandStoreSchema = schema,
                EntityStoreSchema = schema,
                ViewModelStoreSchema = schema
            };

            return services.AddSourceFlowSqlStores(configuration);
        }

        /// <summary>
        /// Ensures all database schemas are created for SQL stores.
        /// This should be called during application startup or as part of migrations.
        /// </summary>
        /// <param name="serviceProvider">The service provider</param>
        /// <returns>Task representing the async operation</returns>
        public static async System.Threading.Tasks.Task EnsureSourceFlowSqlSchemasCreated(
            this IServiceProvider serviceProvider)
        {
            if (serviceProvider == null)
                throw new ArgumentNullException(nameof(serviceProvider));

            var commandStore = serviceProvider.GetService<ICommandStore>() as SqlCommandStore;
            var entityStore = serviceProvider.GetService<IEntityStore>() as SqlEntityStore;
            var viewModelStore = serviceProvider.GetService<IViewModelStore>() as SqlViewModelStore;

            if (commandStore != null)
                await commandStore.EnsureSchemaCreated();

            if (entityStore != null)
                await entityStore.EnsureSchemaCreated();

            if (viewModelStore != null)
                await viewModelStore.EnsureSchemaCreated();
        }
    }
}

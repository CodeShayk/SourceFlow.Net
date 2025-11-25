using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using SourceFlow.Stores.EntityFramework.Options;
using SourceFlow.Stores.EntityFramework.Stores;

namespace SourceFlow.Stores.EntityFramework.Extensions
{
    /// <summary>
    /// Extension methods for registering Entity Framework-based persistence stores.
    ///
    /// <para>
    /// <strong>SQL Server Methods:</strong> AddSourceFlowEfStores overloads use SQL Server by default.
    /// </para>
    /// <para>
    /// <strong>Database-Agnostic Methods:</strong> Use AddSourceFlowEfStoresWithCustomProvider(s) for other databases
    /// (PostgreSQL, MySQL, SQLite, etc.).
    /// </para>
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// [SQL Server] Registers Entity Framework implementations with a single connection string.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="connectionString">SQL Server connection string to use for all stores</param>
        /// <returns>The service collection for chaining</returns>
        /// <remarks>
        /// This method uses SQL Server as the database provider. For other databases, use
        /// <see cref="AddSourceFlowEfStoresWithCustomProvider"/>.
        /// </remarks>
        public static IServiceCollection AddSourceFlowEfStores(this IServiceCollection services, string connectionString)
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));
            if (string.IsNullOrEmpty(connectionString))
                throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));

            // Configure options with default connection string
            var options = new SourceFlowEfOptions { DefaultConnectionString = connectionString };
            services.AddSingleton(options);

            // Configure naming conventions
            ConfigureNamingConventions(options);

            services.AddDbContext<CommandDbContext>(optionsBuilder =>
                optionsBuilder.UseSqlServer(connectionString));
            services.AddDbContext<EntityDbContext>(optionsBuilder =>
                optionsBuilder.UseSqlServer(connectionString));
            services.AddDbContext<ViewModelDbContext>(optionsBuilder =>
                optionsBuilder.UseSqlServer(connectionString));

            // Register EF stores
            services.TryAddScoped<ICommandStore, EfCommandStore>();
            services.TryAddScoped<IEntityStore, EfEntityStore>();
            services.TryAddScoped<IViewModelStore, EfViewModelStore>();

            return services;
        }

        /// <summary>
        /// [SQL Server] Registers Entity Framework implementations with separate connection strings for each store.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="commandConnectionString">SQL Server connection string for command store</param>
        /// <param name="entityConnectionString">SQL Server connection string for entity store</param>
        /// <param name="viewModelConnectionString">SQL Server connection string for view model store</param>
        /// <returns>The service collection for chaining</returns>
        /// <remarks>
        /// This method uses SQL Server as the database provider. For other databases, use
        /// <see cref="AddSourceFlowEfStoresWithCustomProviders"/>.
        /// </remarks>
        public static IServiceCollection AddSourceFlowEfStores(
            this IServiceCollection services,
            string commandConnectionString,
            string entityConnectionString,
            string viewModelConnectionString)
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));

            // Configure options with individual connection strings
            var options = new SourceFlowEfOptions
            {
                CommandConnectionString = commandConnectionString,
                EntityConnectionString = entityConnectionString,
                ViewModelConnectionString = viewModelConnectionString
            };
            services.AddSingleton(options);

            // Configure naming conventions
            ConfigureNamingConventions(options);

            services.AddDbContext<CommandDbContext>(optionsBuilder =>
                optionsBuilder.UseSqlServer(commandConnectionString));
            services.AddDbContext<EntityDbContext>(optionsBuilder =>
                optionsBuilder.UseSqlServer(entityConnectionString));
            services.AddDbContext<ViewModelDbContext>(optionsBuilder =>
                optionsBuilder.UseSqlServer(viewModelConnectionString));

            // Register EF stores
            services.TryAddScoped<ICommandStore, EfCommandStore>();
            services.TryAddScoped<IEntityStore, EfEntityStore>();
            services.TryAddScoped<IViewModelStore, EfViewModelStore>();

            return services;
        }

        /// <summary>
        /// [SQL Server] Registers Entity Framework implementations using configuration from IConfiguration.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="configuration">Configuration to read SQL Server connection strings from</param>
        /// <returns>The service collection for chaining</returns>
        /// <remarks>
        /// <para>Looks for settings in the format: SourceFlow:CommandConnectionString, SourceFlow:EntityConnectionString, etc.</para>
        /// <para>This method uses SQL Server as the database provider. For other databases, use
        /// <see cref="AddSourceFlowEfStoresWithCustomProvider"/>.</para>
        /// </remarks>
        public static IServiceCollection AddSourceFlowEfStores(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            // Read configuration
            var options = new SourceFlowEfOptions
            {
                CommandConnectionString = configuration.GetConnectionString("SourceFlow.Command"),
                EntityConnectionString = configuration.GetConnectionString("SourceFlow.Entity"),
                ViewModelConnectionString = configuration.GetConnectionString("SourceFlow.ViewModel"),
                DefaultConnectionString = configuration.GetConnectionString("SourceFlow.Default")
                    ?? configuration.GetSection("SourceFlow")?.GetValue<string>("DefaultConnectionString")
            };

            // If individual connection strings are not provided, fallback to default
            if (string.IsNullOrEmpty(options.CommandConnectionString))
                options.CommandConnectionString = options.DefaultConnectionString;
            if (string.IsNullOrEmpty(options.EntityConnectionString))
                options.EntityConnectionString = options.DefaultConnectionString;
            if (string.IsNullOrEmpty(options.ViewModelConnectionString))
                options.ViewModelConnectionString = options.DefaultConnectionString;

            services.AddSingleton(options);

            // Configure naming conventions
            ConfigureNamingConventions(options);

            // Register contexts with appropriate connection strings
            services.AddDbContext<CommandDbContext>(optionsBuilder =>
            {
                var connectionString = options.GetConnectionString(StoreType.Command);
                optionsBuilder.UseSqlServer(connectionString);
            });

            services.AddDbContext<EntityDbContext>(optionsBuilder =>
            {
                var connectionString = options.GetConnectionString(StoreType.Entity);
                optionsBuilder.UseSqlServer(connectionString);
            });

            services.AddDbContext<ViewModelDbContext>(optionsBuilder =>
            {
                var connectionString = options.GetConnectionString(StoreType.ViewModel);
                optionsBuilder.UseSqlServer(connectionString);
            });

            // Register EF stores
            services.TryAddScoped<ICommandStore, EfCommandStore>();
            services.TryAddScoped<IEntityStore, EfEntityStore>();
            services.TryAddScoped<IViewModelStore, EfViewModelStore>();

            return services;
        }

        /// <summary>
        /// [SQL Server] Registers Entity Framework implementations with options configuration.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="optionsAction">Action to configure the options including connection strings and naming conventions</param>
        /// <returns>The service collection for chaining</returns>
        /// <remarks>
        /// <para>This method allows configuring connection strings and table naming conventions.</para>
        /// <para>This method uses SQL Server as the database provider. For other databases, use
        /// <see cref="AddSourceFlowEfStoresWithCustomProvider"/>.</para>
        /// </remarks>
        public static IServiceCollection AddSourceFlowEfStores(
            this IServiceCollection services,
            Action<SourceFlowEfOptions> optionsAction)
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));
            if (optionsAction == null)
                throw new ArgumentNullException(nameof(optionsAction));

            var options = new SourceFlowEfOptions();
            optionsAction(options);
            services.AddSingleton(options);

            // Configure naming conventions
            ConfigureNamingConventions(options);

            // Register contexts with appropriate connection strings based on options
            services.AddDbContext<CommandDbContext>(optionsBuilder =>
            {
                var connectionString = options.GetConnectionString(StoreType.Command);
                optionsBuilder.UseSqlServer(connectionString);
            });

            services.AddDbContext<EntityDbContext>(optionsBuilder =>
            {
                var connectionString = options.GetConnectionString(StoreType.Entity);
                optionsBuilder.UseSqlServer(connectionString);
            });

            services.AddDbContext<ViewModelDbContext>(optionsBuilder =>
            {
                var connectionString = options.GetConnectionString(StoreType.ViewModel);
                optionsBuilder.UseSqlServer(connectionString);
            });

            // Register EF stores
            services.TryAddScoped<ICommandStore, EfCommandStore>();
            services.TryAddScoped<IEntityStore, EfEntityStore>();
            services.TryAddScoped<IViewModelStore, EfViewModelStore>();

            return services;
        }

        /// <summary>
        /// [Database-Agnostic] Registers Entity Framework implementations using a custom database provider.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="configureContext">Action to configure the DbContext with the desired provider (SQLite, PostgreSQL, MySQL, etc.)</param>
        /// <returns>The service collection for chaining</returns>
        /// <remarks>
        /// <para>This method allows you to use any Entity Framework Core database provider.</para>
        /// <example>
        /// PostgreSQL:
        /// <code>
        /// services.AddSourceFlowEfStoresWithCustomProvider(options =>
        ///     options.UseNpgsql(connectionString));
        /// </code>
        /// SQLite:
        /// <code>
        /// services.AddSourceFlowEfStoresWithCustomProvider(options =>
        ///     options.UseSqlite(connectionString));
        /// </code>
        /// MySQL:
        /// <code>
        /// services.AddSourceFlowEfStoresWithCustomProvider(options =>
        ///     options.UseMySql(connectionString, serverVersion));
        /// </code>
        /// </example>
        /// </remarks>
        public static IServiceCollection AddSourceFlowEfStoresWithCustomProvider(
            this IServiceCollection services,
            Action<DbContextOptionsBuilder> configureContext)
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));
            if (configureContext == null)
                throw new ArgumentNullException(nameof(configureContext));

            // Create and configure default options
            var options = new SourceFlowEfOptions();
            services.AddSingleton(options);

            // Configure naming conventions with default settings
            ConfigureNamingConventions(options);

            services.AddDbContext<CommandDbContext>(configureContext);
            services.AddDbContext<EntityDbContext>(configureContext);
            services.AddDbContext<ViewModelDbContext>(configureContext);

            // Register EF stores
            services.TryAddScoped<ICommandStore, EfCommandStore>();
            services.TryAddScoped<IEntityStore, EfEntityStore>();
            services.TryAddScoped<IViewModelStore, EfViewModelStore>();

            return services;
        }

        /// <summary>
        /// [Database-Agnostic] Registers Entity Framework implementations with separate database provider configurations.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="commandContextConfig">Action to configure the CommandDbContext (can use any EF Core provider)</param>
        /// <param name="entityContextConfig">Action to configure the EntityDbContext (can use any EF Core provider)</param>
        /// <param name="viewModelContextConfig">Action to configure the ViewModelDbContext (can use any EF Core provider)</param>
        /// <returns>The service collection for chaining</returns>
        /// <remarks>
        /// <para>This method allows each store to use a different database provider or configuration.</para>
        /// <example>
        /// Mix different databases:
        /// <code>
        /// services.AddSourceFlowEfStoresWithCustomProviders(
        ///     commandConfig: opt => opt.UseNpgsql(postgresConnectionString),
        ///     entityConfig: opt => opt.UseSqlite(sqliteConnectionString),
        ///     viewModelConfig: opt => opt.UseSqlServer(sqlServerConnectionString));
        /// </code>
        /// </example>
        /// </remarks>
        public static IServiceCollection AddSourceFlowEfStoresWithCustomProviders(
            this IServiceCollection services,
            Action<DbContextOptionsBuilder> commandContextConfig,
            Action<DbContextOptionsBuilder> entityContextConfig,
            Action<DbContextOptionsBuilder> viewModelContextConfig)
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));
            if (commandContextConfig == null)
                throw new ArgumentNullException(nameof(commandContextConfig));
            if (entityContextConfig == null)
                throw new ArgumentNullException(nameof(entityContextConfig));
            if (viewModelContextConfig == null)
                throw new ArgumentNullException(nameof(viewModelContextConfig));

            // Create and configure default options
            var options = new SourceFlowEfOptions();
            services.AddSingleton(options);

            // Configure naming conventions with default settings
            ConfigureNamingConventions(options);

            services.AddDbContext<CommandDbContext>(commandContextConfig);
            services.AddDbContext<EntityDbContext>(entityContextConfig);
            services.AddDbContext<ViewModelDbContext>(viewModelContextConfig);

            // Register EF stores
            services.TryAddScoped<ICommandStore, EfCommandStore>();
            services.TryAddScoped<IEntityStore, EfEntityStore>();
            services.TryAddScoped<IViewModelStore, EfViewModelStore>();

            return services;
        }

        /// <summary>
        /// Configures naming conventions for all DbContexts based on the options.
        /// </summary>
        /// <param name="options">The SourceFlow options containing naming convention settings</param>
        private static void ConfigureNamingConventions(SourceFlowEfOptions options)
        {
            if (options == null)
                return;

            // Configure naming convention for each context type
            CommandDbContext.ConfigureNamingConvention(options.CommandTableNaming);
            EntityDbContext.ConfigureNamingConvention(options.EntityTableNaming);
            ViewModelDbContext.ConfigureNamingConvention(options.ViewModelTableNaming);
        }
    }
}
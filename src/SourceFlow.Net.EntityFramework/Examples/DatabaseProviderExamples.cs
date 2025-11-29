//using Microsoft.EntityFrameworkCore;
//using Microsoft.Extensions.DependencyInjection;
//using Microsoft.Data.Sqlite;
//using SourceFlow.Net.EntityFramework.Extensions;

//namespace SourceFlow.Net.EntityFramework.Examples
//{
//    /// <summary>
//    /// Examples showing how to use the Entity Framework stores with different database providers
//    /// </summary>
//    public static class DatabaseProviderExamples
//    {
//        /// <summary>
//        /// Example: Using SQLite for all stores
//        /// </summary>
//        public static void UseSqliteExample()
//        {
//            var services = new ServiceCollection();

//            // Create a connection string for SQLite
//            var connectionString = "DataSource=:memory:";

//            services.AddSourceFlowEfStoresWithCustomProvider(optionsBuilder =>
//            {
//                // Configure for SQLite
//                optionsBuilder.UseSqlite(connectionString);
//            });
//        }

//        /// <summary>
//        /// Example: Using different database providers for each store
//        /// </summary>
//        public static void UseDifferentProvidersExample()
//        {
//            var services = new ServiceCollection();

//            // Command store using SQLite
//            var commandConnectionString = "DataSource=:memory:command.db";
            
//            // Entity store using a different SQLite database
//            var entityConnectionString = "DataSource=:memory:entity.db";
            
//            // View model store using another SQLite database
//            var viewModelConnectionString = "DataSource=:memory:viewmodel.db";

//            services.AddSourceFlowEfStoresWithCustomProviders(
//                commandContextConfig: optionsBuilder => optionsBuilder.UseSqlite(commandConnectionString),
//                entityContextConfig: optionsBuilder => optionsBuilder.UseSqlite(entityConnectionString),
//                viewModelContextConfig: optionsBuilder => optionsBuilder.UseSqlite(viewModelConnectionString)
//            );
//        }

//        /// <summary>
//        /// Example: Using PostgreSQL
//        /// </summary>
//        public static void UsePostgreSqlExample()
//        {
//            var services = new ServiceCollection();

//            var connectionString = "Host=localhost;Database=SourceFlow;Username=postgres;Password=password";

//            services.AddSourceFlowEfStoresWithCustomProvider(optionsBuilder =>
//            {
//                // This would require Microsoft.EntityFrameworkCore.PostgreSQL package
//                // optionsBuilder.UseNpgsql(connectionString);
//            });
//        }

//        /// <summary>
//        /// Example: Using MySQL
//        /// </summary>
//        public static void UseMySqlExample()
//        {
//            var services = new ServiceCollection();

//            var connectionString = "Server=localhost;Database=SourceFlow;Uid=user;Pwd=password;";

//            services.AddSourceFlowEfStoresWithCustomProvider(optionsBuilder =>
//            {
//                // This would require Pomelo.EntityFrameworkCore.MySql or Oracle's MySQL provider
//                // optionsBuilder.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));
//            });
//        }

//        /// <summary>
//        /// Example: Using SQL Server (default behavior)
//        /// </summary>
//        public static void UseSqlServerExample()
//        {
//            var services = new ServiceCollection();

//            var connectionString = "Server=localhost;Database=SourceFlow;Trusted_Connection=true;";

//            // This is the original method that uses SQL Server
//            services.AddSourceFlowEfStores(connectionString);
//        }
        
//        /// <summary>
//        /// Example: Using SQL Server with different connection strings per store
//        /// </summary>
//        public static void UseSqlServerSeparateConnectionsExample()
//        {
//            var services = new ServiceCollection();

//            // Different connection strings for each store
//            var commandConnectionString = "Server=localhost;Database=SourceFlow.Commands;Trusted_Connection=true;";
//            var entityConnectionString = "Server=localhost;Database=SourceFlow.Entities;Trusted_Connection=true;";
//            var viewModelConnectionString = "Server=localhost;Database=SourceFlow.ViewModels;Trusted_Connection=true;";

//            // This is the original method that uses SQL Server
//            services.AddSourceFlowEfStores(
//                commandConnectionString,
//                entityConnectionString,
//                viewModelConnectionString
//            );
//        }
//    }
//}
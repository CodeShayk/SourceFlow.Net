//using System;
//using System.Collections.Generic;
//using Microsoft.Extensions.Configuration;
//using Microsoft.Extensions.DependencyInjection;
//using SourceFlow.Net.EntityFramework.Extensions;

//namespace SourceFlow.Net.EntityFramework.Examples
//{
//    /// <summary>
//    /// Examples showing different ways to configure Entity Framework stores with connection strings
//    /// </summary>
//    public static class UsageExamples
//    {
//        /// <summary>
//        /// Example: Using a single connection string for all stores
//        /// </summary>
//        public static void SingleConnectionStringExample()
//        {
//            var services = new ServiceCollection();

//            // All stores use the same connection string
//            services.AddSourceFlowEfStores("Server=localhost;Database=SourceFlow;Trusted_Connection=true;");
//        }

//        /// <summary>
//        /// Example: Using separate connection strings for each store
//        /// </summary>
//        public static void SeparateConnectionStringsExample()
//        {
//            var services = new ServiceCollection();

//            // Each store gets its own connection string
//            services.AddSourceFlowEfStores(
//                commandConnectionString: "Server=localhost;Database=SourceFlow.Commands;Trusted_Connection=true;",
//                entityConnectionString: "Server=localhost;Database=SourceFlow.Entities;Trusted_Connection=true;",
//                viewModelConnectionString: "Server=localhost;Database=SourceFlow.ViewModels;Trusted_Connection=true;"
//            );
//        }

//        /// <summary>
//        /// Example: Using configuration from appsettings.json
//        /// </summary>
//        public static void ConfigurationExample(IConfiguration configuration)
//        {
//            var services = new ServiceCollection();

//            // Reads connection strings from configuration:
//            // ConnectionStrings:SourceFlow.Command
//            // ConnectionStrings:SourceFlow.Entity  
//            // ConnectionStrings:SourceFlow.ViewModel
//            services.AddSourceFlowEfStores(configuration);
//        }

//        /// <summary>
//        /// Example: Using options action for programmatic configuration
//        /// </summary>
//        public static void OptionsExample()
//        {
//            var services = new ServiceCollection();

//            services.AddSourceFlowEfStores(options =>
//            {
//                options.CommandConnectionString = GetCommandConnectionString();
//                options.EntityConnectionString = GetEntityConnectionString();
//                options.ViewModelConnectionString = GetViewModelConnectionString();
//            });
//        }

//        /// <summary>
//        /// Placeholder methods to represent getting connection strings from various sources
//        /// </summary>
//        private static string GetCommandConnectionString() => "Server=localhost;Database=SourceFlow.Commands;Trusted_Connection=true;";
//        private static string GetEntityConnectionString() => "Server=localhost;Database=SourceFlow.Entities;Trusted_Connection=true;";
//        private static string GetViewModelConnectionString() => "Server=localhost;Database=SourceFlow.ViewModels;Trusted_Connection=true;";
//    }
//}
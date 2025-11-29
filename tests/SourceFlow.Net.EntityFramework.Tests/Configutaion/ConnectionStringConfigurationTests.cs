#nullable enable

using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SourceFlow.Stores.EntityFramework.Extensions;

namespace SourceFlow.Stores.EntityFramework.Tests.Configutaion
{
    [TestFixture]
    public class ConnectionStringConfigurationTests
    {
        [Test]
        public void AddSourceFlowEfStores_SingleConnectionString_RegistersCorrectly()
        {
            // Arrange
            var services = new ServiceCollection();
            var connectionString = "DataSource=:memory:";

            // Act
            services.AddSourceFlowEfStores(connectionString);

            // Assert - Services should be registered without throwing exceptions
            var serviceProvider = services.BuildServiceProvider();
            Assert.DoesNotThrow(() => serviceProvider.GetRequiredService<ICommandStore>());
            Assert.DoesNotThrow(() => serviceProvider.GetRequiredService<IEntityStore>());
            Assert.DoesNotThrow(() => serviceProvider.GetRequiredService<IViewModelStore>());
            Assert.DoesNotThrow(() => serviceProvider.GetRequiredService<CommandDbContext>());
            Assert.DoesNotThrow(() => serviceProvider.GetRequiredService<EntityDbContext>());
            Assert.DoesNotThrow(() => serviceProvider.GetRequiredService<ViewModelDbContext>());
        }

        [Test]
        public void AddSourceFlowEfStores_SeparateConnectionStrings_RegistersCorrectly()
        {
            // Arrange
            var services = new ServiceCollection();
            var cmdConnectionString = "DataSource=command.db";
            var entityConnectionString = "DataSource=entity.db";
            var viewModelConnectionString = "DataSource=viewModel.db";

            // Act
            services.AddSourceFlowEfStores(
                cmdConnectionString,
                entityConnectionString,
                viewModelConnectionString);

            // Assert - Services should be registered without throwing exceptions
            var serviceProvider = services.BuildServiceProvider();
            Assert.DoesNotThrow(() => serviceProvider.GetRequiredService<ICommandStore>());
            Assert.DoesNotThrow(() => serviceProvider.GetRequiredService<IEntityStore>());
            Assert.DoesNotThrow(() => serviceProvider.GetRequiredService<IViewModelStore>());
        }

        [Test]
        public void AddSourceFlowEfStores_WithConfiguration_RegistersCorrectly()
        {
            // Arrange
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    {"ConnectionStrings:SourceFlow.Command", "DataSource=command.db"},
                    {"ConnectionStrings:SourceFlow.Entity", "DataSource=entity.db"},
                    {"ConnectionStrings:SourceFlow.ViewModel", "DataSource=viewModel.db"}
                })
                .Build();

            var services = new ServiceCollection();

            // Act
            services.AddSourceFlowEfStores(config);

            // Assert - Services should be registered without throwing exceptions
            var serviceProvider = services.BuildServiceProvider();
            Assert.DoesNotThrow(() => serviceProvider.GetRequiredService<ICommandStore>());
            Assert.DoesNotThrow(() => serviceProvider.GetRequiredService<IEntityStore>());
            Assert.DoesNotThrow(() => serviceProvider.GetRequiredService<IViewModelStore>());
        }

        [Test]
        public void AddSourceFlowEfStores_WithOptionsAction_RegistersCorrectly()
        {
            // Arrange
            var services = new ServiceCollection();

            // Act
            services.AddSourceFlowEfStores(options =>
            {
                options.CommandConnectionString = "DataSource=command.db";
                options.EntityConnectionString = "DataSource=entity.db";
                options.ViewModelConnectionString = "DataSource=viewModel.db";
            });

            // Assert - Services should be registered without throwing exceptions
            var serviceProvider = services.BuildServiceProvider();
            Assert.DoesNotThrow(() => serviceProvider.GetRequiredService<ICommandStore>());
            Assert.DoesNotThrow(() => serviceProvider.GetRequiredService<IEntityStore>());
            Assert.DoesNotThrow(() => serviceProvider.GetRequiredService<IViewModelStore>());
        }
    }
}

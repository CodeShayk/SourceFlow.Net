using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Net.SQL.Configuration;
using SourceFlow.Net.SQL.Extensions;
using SourceFlow.Net.SQL.Stores;

namespace SourceFlow.Net.SQL.Tests.Extensions
{
    [TestFixture]
    public class ServiceCollectionExtensionsTests
    {
        [Test]
        public void AddSourceFlowSqlStores_WithConfiguration_RegistersAllStores()
        {
            // Arrange
            var services = new ServiceCollection();
            var configuration = new SqlStoreConfiguration
            {
                CommandStoreConnectionString = "Server=localhost;Database=Commands;",
                EntityStoreConnectionString = "Server=localhost;Database=Entities;",
                ViewModelStoreConnectionString = "Server=localhost;Database=ViewModels;"
            };

            // Act
            services.AddSourceFlowSqlStores(configuration);
            var serviceProvider = services.BuildServiceProvider();

            // Assert
            Assert.That(serviceProvider.GetService<ICommandStore>(), Is.InstanceOf<SqlCommandStore>());
            Assert.That(serviceProvider.GetService<IEntityStore>(), Is.InstanceOf<SqlEntityStore>());
            Assert.That(serviceProvider.GetService<IViewModelStore>(), Is.InstanceOf<SqlViewModelStore>());
            Assert.That(serviceProvider.GetService<SqlStoreConfiguration>(), Is.SameAs(configuration));
        }

        [Test]
        public void AddSourceFlowSqlStores_WithConfigureAction_RegistersAllStores()
        {
            // Arrange
            var services = new ServiceCollection();

            // Act
            services.AddSourceFlowSqlStores(config =>
            {
                config.CommandStoreConnectionString = "Server=localhost;Database=Commands;";
                config.EntityStoreConnectionString = "Server=localhost;Database=Entities;";
                config.ViewModelStoreConnectionString = "Server=localhost;Database=ViewModels;";
                config.CommandTimeout = 60;
            });

            var serviceProvider = services.BuildServiceProvider();

            // Assert
            Assert.That(serviceProvider.GetService<ICommandStore>(), Is.InstanceOf<SqlCommandStore>());
            Assert.That(serviceProvider.GetService<IEntityStore>(), Is.InstanceOf<SqlEntityStore>());
            Assert.That(serviceProvider.GetService<IViewModelStore>(), Is.InstanceOf<SqlViewModelStore>());

            var configuration = serviceProvider.GetService<SqlStoreConfiguration>();
            Assert.That(configuration, Is.Not.Null);
            Assert.That(configuration!.CommandTimeout, Is.EqualTo(60));
        }

        [Test]
        public void AddSourceFlowSqlStores_WithConnectionString_RegistersAllStores()
        {
            // Arrange
            var services = new ServiceCollection();
            var connectionString = "Server=localhost;Database=SourceFlow;";

            // Act
            services.AddSourceFlowSqlStores(connectionString);
            var serviceProvider = services.BuildServiceProvider();

            // Assert
            Assert.That(serviceProvider.GetService<ICommandStore>(), Is.InstanceOf<SqlCommandStore>());
            Assert.That(serviceProvider.GetService<IEntityStore>(), Is.InstanceOf<SqlEntityStore>());
            Assert.That(serviceProvider.GetService<IViewModelStore>(), Is.InstanceOf<SqlViewModelStore>());

            var configuration = serviceProvider.GetService<SqlStoreConfiguration>();
            Assert.That(configuration, Is.Not.Null);
            Assert.That(configuration!.CommandStoreConnectionString, Is.EqualTo(connectionString));
            Assert.That(configuration.EntityStoreConnectionString, Is.EqualTo(connectionString));
            Assert.That(configuration.ViewModelStoreConnectionString, Is.EqualTo(connectionString));
        }

        [Test]
        public void AddSourceFlowSqlStores_WithConnectionStringAndSchema_UsesCustomSchema()
        {
            // Arrange
            var services = new ServiceCollection();
            var connectionString = "Server=localhost;Database=SourceFlow;";
            var schema = "custom";

            // Act
            services.AddSourceFlowSqlStores(connectionString, schema);
            var serviceProvider = services.BuildServiceProvider();

            // Assert
            var configuration = serviceProvider.GetService<SqlStoreConfiguration>();
            Assert.That(configuration, Is.Not.Null);
            Assert.That(configuration!.CommandStoreSchema, Is.EqualTo(schema));
            Assert.That(configuration.EntityStoreSchema, Is.EqualTo(schema));
            Assert.That(configuration.ViewModelStoreSchema, Is.EqualTo(schema));
        }

        [Test]
        public void AddSourceFlowSqlStores_WithNullServices_ThrowsArgumentNullException()
        {
            // Arrange
            IServiceCollection services = null!;
            var configuration = new SqlStoreConfiguration
            {
                CommandStoreConnectionString = "Server=localhost;Database=Commands;",
                EntityStoreConnectionString = "Server=localhost;Database=Entities;",
                ViewModelStoreConnectionString = "Server=localhost;Database=ViewModels;"
            };

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => services.AddSourceFlowSqlStores(configuration));
        }

        [Test]
        public void AddSourceFlowSqlStores_WithNullConfiguration_ThrowsArgumentNullException()
        {
            // Arrange
            var services = new ServiceCollection();
            SqlStoreConfiguration configuration = null!;

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => services.AddSourceFlowSqlStores(configuration));
        }

        [Test]
        public void AddSourceFlowSqlStores_WithInvalidConfiguration_ThrowsArgumentException()
        {
            // Arrange
            var services = new ServiceCollection();
            var configuration = new SqlStoreConfiguration
            {
                CommandStoreConnectionString = "Server=localhost;Database=Commands;",
                EntityStoreConnectionString = "",  // Invalid
                ViewModelStoreConnectionString = "Server=localhost;Database=ViewModels;"
            };

            // Act & Assert
            Assert.Throws<ArgumentException>(() => services.AddSourceFlowSqlStores(configuration));
        }

        [Test]
        public void AddSourceFlowSqlStores_CalledMultipleTimes_DoesNotDuplicateRegistrations()
        {
            // Arrange
            var services = new ServiceCollection();
            var configuration = new SqlStoreConfiguration
            {
                CommandStoreConnectionString = "Server=localhost;Database=Commands;",
                EntityStoreConnectionString = "Server=localhost;Database=Entities;",
                ViewModelStoreConnectionString = "Server=localhost;Database=ViewModels;"
            };

            // Act
            services.AddSourceFlowSqlStores(configuration);
            services.AddSourceFlowSqlStores(configuration);
            var serviceProvider = services.BuildServiceProvider();

            // Assert
            var commandStores = serviceProvider.GetServices<ICommandStore>().ToList();
            var entityStores = serviceProvider.GetServices<IEntityStore>().ToList();
            var viewModelStores = serviceProvider.GetServices<IViewModelStore>().ToList();

            // TryAddSingleton ensures only one registration
            Assert.That(commandStores.Count, Is.EqualTo(1));
            Assert.That(entityStores.Count, Is.EqualTo(1));
            Assert.That(viewModelStores.Count, Is.EqualTo(1));
        }
    }
}

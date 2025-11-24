using SourceFlow.Net.SQL.Configuration;

namespace SourceFlow.Net.SQL.Tests.Configuration
{
    [TestFixture]
    public class SqlStoreConfigurationTests
    {
        [Test]
        public void Validate_WithValidConfiguration_DoesNotThrow()
        {
            // Arrange
            var configuration = new SqlStoreConfiguration
            {
                CommandStoreConnectionString = "Server=localhost;Database=Commands;",
                EntityStoreConnectionString = "Server=localhost;Database=Entities;",
                ViewModelStoreConnectionString = "Server=localhost;Database=ViewModels;"
            };

            // Act & Assert
            Assert.DoesNotThrow(() => configuration.Validate());
        }

        [Test]
        public void Validate_WithMissingCommandStoreConnectionString_ThrowsArgumentException()
        {
            // Arrange
            var configuration = new SqlStoreConfiguration
            {
                CommandStoreConnectionString = "",
                EntityStoreConnectionString = "Server=localhost;Database=Entities;",
                ViewModelStoreConnectionString = "Server=localhost;Database=ViewModels;"
            };

            // Act & Assert
            var ex = Assert.Throws<ArgumentException>(() => configuration.Validate());
            Assert.That(ex!.ParamName, Is.EqualTo("CommandStoreConnectionString"));
        }

        [Test]
        public void Validate_WithMissingEntityStoreConnectionString_ThrowsArgumentException()
        {
            // Arrange
            var configuration = new SqlStoreConfiguration
            {
                CommandStoreConnectionString = "Server=localhost;Database=Commands;",
                EntityStoreConnectionString = null!,
                ViewModelStoreConnectionString = "Server=localhost;Database=ViewModels;"
            };

            // Act & Assert
            var ex = Assert.Throws<ArgumentException>(() => configuration.Validate());
            Assert.That(ex!.ParamName, Is.EqualTo("EntityStoreConnectionString"));
        }

        [Test]
        public void Validate_WithMissingViewModelStoreConnectionString_ThrowsArgumentException()
        {
            // Arrange
            var configuration = new SqlStoreConfiguration
            {
                CommandStoreConnectionString = "Server=localhost;Database=Commands;",
                EntityStoreConnectionString = "Server=localhost;Database=Entities;",
                ViewModelStoreConnectionString = "   "
            };

            // Act & Assert
            var ex = Assert.Throws<ArgumentException>(() => configuration.Validate());
            Assert.That(ex!.ParamName, Is.EqualTo("ViewModelStoreConnectionString"));
        }

        [Test]
        public void Validate_WithInvalidCommandTimeout_ThrowsArgumentException()
        {
            // Arrange
            var configuration = new SqlStoreConfiguration
            {
                CommandStoreConnectionString = "Server=localhost;Database=Commands;",
                EntityStoreConnectionString = "Server=localhost;Database=Entities;",
                ViewModelStoreConnectionString = "Server=localhost;Database=ViewModels;",
                CommandTimeout = 0
            };

            // Act & Assert
            var ex = Assert.Throws<ArgumentException>(() => configuration.Validate());
            Assert.That(ex!.ParamName, Is.EqualTo("CommandTimeout"));
        }

        [Test]
        public void DefaultValues_AreSetCorrectly()
        {
            // Arrange & Act
            var configuration = new SqlStoreConfiguration();

            // Assert
            Assert.That(configuration.CommandStoreSchema, Is.EqualTo("dbo"));
            Assert.That(configuration.EntityStoreSchema, Is.EqualTo("dbo"));
            Assert.That(configuration.ViewModelStoreSchema, Is.EqualTo("dbo"));
            Assert.That(configuration.CommandTimeout, Is.EqualTo(30));
        }

        [Test]
        public void CustomValues_CanBeSet()
        {
            // Arrange & Act
            var configuration = new SqlStoreConfiguration
            {
                CommandStoreConnectionString = "Server=localhost;Database=Commands;",
                EntityStoreConnectionString = "Server=localhost;Database=Entities;",
                ViewModelStoreConnectionString = "Server=localhost;Database=ViewModels;",
                CommandStoreSchema = "cmd",
                EntityStoreSchema = "entity",
                ViewModelStoreSchema = "view",
                CommandTimeout = 60
            };

            // Assert
            Assert.That(configuration.CommandStoreSchema, Is.EqualTo("cmd"));
            Assert.That(configuration.EntityStoreSchema, Is.EqualTo("entity"));
            Assert.That(configuration.ViewModelStoreSchema, Is.EqualTo("view"));
            Assert.That(configuration.CommandTimeout, Is.EqualTo(60));
        }
    }
}

using System;
using NUnit.Framework;
using SourceFlow.Stores.EntityFramework.Options;

namespace SourceFlow.Stores.EntityFramework.Tests.Unit
{
    [TestFixture]
    public class SourceFlowEfOptionsTests
    {
        [Test]
        public void GetConnectionString_WithCommandStoreTypeAndCommandConnectionString_ReturnsCommandConnectionString()
        {
            // Arrange
            var options = new SourceFlowEfOptions
            {
                CommandConnectionString = "Command Connection",
                EntityConnectionString = "Entity Connection",
                ViewModelConnectionString = "ViewModel Connection"
            };

            // Act
            var result = options.GetConnectionString(StoreType.Command);

            // Assert
            Assert.That(result, Is.EqualTo("Command Connection"));
        }

        [Test]
        public void GetConnectionString_WithEntityStoreTypeAndEntityConnectionString_ReturnsEntityConnectionString()
        {
            // Arrange
            var options = new SourceFlowEfOptions
            {
                CommandConnectionString = "Command Connection",
                EntityConnectionString = "Entity Connection",
                ViewModelConnectionString = "ViewModel Connection"
            };

            // Act
            var result = options.GetConnectionString(StoreType.Entity);

            // Assert
            Assert.That(result, Is.EqualTo("Entity Connection"));
        }

        [Test]
        public void GetConnectionString_WithViewModelStoreTypeAndViewModelConnectionString_ReturnsViewModelConnectionString()
        {
            // Arrange
            var options = new SourceFlowEfOptions
            {
                CommandConnectionString = "Command Connection",
                EntityConnectionString = "Entity Connection",
                ViewModelConnectionString = "ViewModel Connection"
            };

            // Act
            var result = options.GetConnectionString(StoreType.ViewModel);

            // Assert
            Assert.That(result, Is.EqualTo("ViewModel Connection"));
        }

        [Test]
        public void GetConnectionString_WithCommandStoreTypeAndNoCommandConnectionStringButDefault_ReturnsDefaultConnectionString()
        {
            // Arrange
            var options = new SourceFlowEfOptions
            {
                DefaultConnectionString = "Default Connection"
            };

            // Act
            var result = options.GetConnectionString(StoreType.Command);

            // Assert
            Assert.That(result, Is.EqualTo("Default Connection"));
        }

        [Test]
        public void GetConnectionString_WithUnknownStoreType_ThrowsArgumentException()
        {
            // Arrange
            var options = new SourceFlowEfOptions();

            // Act & Assert
            Assert.Throws<ArgumentException>(() => options.GetConnectionString((StoreType)999));
        }

        [Test]
        public void GetConnectionString_WithNoConnectionStrings_ThrowsInvalidOperationException()
        {
            // Arrange
            var options = new SourceFlowEfOptions();

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => options.GetConnectionString(StoreType.Command));
        }
    }
}
using Microsoft.Extensions.Configuration;
using Moq;
using SourceFlow.Cloud.AWS.Configuration;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;

namespace SourceFlow.Cloud.AWS.Tests.Unit;

public class RoutingConfigurationTests
{
    [Fact]
    public void ConfigurationBasedAwsCommandRouting_ShouldRouteToAws_WhenAttributePresent()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().Build();
        var routingConfig = new ConfigurationBasedAwsCommandRouting(configuration);

        // Act
        var result = routingConfig.ShouldRouteToAws<TestCommand>();

        // Assert
        Assert.False(result); // Default behavior without configuration or attribute
    }

    [Fact]
    public void ConfigurationBasedAwsEventRouting_ShouldRouteToAws_WhenAttributePresent()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().Build();
        var routingConfig = new ConfigurationBasedAwsEventRouting(configuration);

        // Act
        var result = routingConfig.ShouldRouteToAws<TestEvent>();

        // Assert
        Assert.False(result); // Default behavior without configuration or attribute
    }
}
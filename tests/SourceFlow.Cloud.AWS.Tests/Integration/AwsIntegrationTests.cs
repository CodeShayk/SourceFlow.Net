using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Cloud.AWS.Configuration;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;

namespace SourceFlow.Cloud.AWS.Tests.Integration;

[Collection("AWS Integration Tests")]
[Trait("Category", "Integration")]
[Trait("Category", "RequiresLocalStack")]
public class AwsIntegrationTests
{
    [Fact]
    public void AwsOptions_CanBeConfigured()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.UseSourceFlowAws(
            options =>
            {
                options.Region = Amazon.RegionEndpoint.USEast1;
                options.EnableCommandRouting = true;
                options.EnableEventRouting = true;
            },
            bus => bus
                .Send.Command<TestCommand>(q => q.Queue("test-queue.fifo"))
                .Listen.To.CommandQueue("test-queue.fifo"));

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<AwsOptions>();

        // Assert
        Assert.Equal(Amazon.RegionEndpoint.USEast1, options.Region);
        Assert.True(options.EnableCommandRouting);
        Assert.True(options.EnableEventRouting);
    }
}

using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Cloud.AWS.Configuration;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using SourceFlow.Cloud.Core.Configuration;

namespace SourceFlow.Cloud.AWS.Tests.Unit;

public class IocExtensionsTests
{
    [Fact]
    public void UseSourceFlowAws_RegistersAllRequiredServices()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.UseSourceFlowAws(
            options => { options.Region = Amazon.RegionEndpoint.USEast1; },
            bus => bus
                .Send.Command<TestCommand>(q => q.Queue("test-queue.fifo"))
                .Listen.To.CommandQueue("test-queue.fifo"));

        var provider = services.BuildServiceProvider();

        // Assert
        var awsOptions = provider.GetRequiredService<AwsOptions>();
        var commandRouting = provider.GetRequiredService<ICommandRoutingConfiguration>();
        var eventRouting = provider.GetRequiredService<IEventRoutingConfiguration>();
        var bootstrapConfig = provider.GetRequiredService<IBusBootstrapConfiguration>();

        Assert.NotNull(awsOptions);
        Assert.NotNull(commandRouting);
        Assert.NotNull(eventRouting);
        Assert.NotNull(bootstrapConfig);
    }

    [Fact]
    public void UseSourceFlowAws_RegistersBusConfigurationAsSingletonAcrossInterfaces()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.UseSourceFlowAws(
            options => { options.Region = Amazon.RegionEndpoint.USEast1; },
            bus => bus
                .Send.Command<TestCommand>(q => q.Queue("test-queue.fifo"))
                .Listen.To.CommandQueue("test-queue.fifo"));

        var provider = services.BuildServiceProvider();

        // Assert - all routing interfaces resolve to the same BusConfiguration instance
        var busConfig = provider.GetRequiredService<BusConfiguration>();
        var commandRouting = provider.GetRequiredService<ICommandRoutingConfiguration>();
        var eventRouting = provider.GetRequiredService<IEventRoutingConfiguration>();
        var bootstrapConfig = provider.GetRequiredService<IBusBootstrapConfiguration>();

        Assert.Same(busConfig, commandRouting);
        Assert.Same(busConfig, eventRouting);
        Assert.Same(busConfig, bootstrapConfig);
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Cloud.Azure.Tests.TestHelpers;
using SourceFlow.Cloud.Core.Configuration;

namespace SourceFlow.Cloud.Azure.Tests.Unit;

public class AzureIocExtensionsTests
{
    [Fact]
    public void UseSourceFlowAzure_RegistersBusConfigurationAsSingleton()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SourceFlow:Azure:ServiceBus:ConnectionString"] = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=testkey="
            })
            .Build());

        // Act
        services.UseSourceFlowAzure(
            options =>
            {
                options.EnableCommandRouting = true;
                options.EnableEventRouting = true;
            },
            bus => bus
                .Send.Command<TestCommand>(q => q.Queue("test-queue"))
                .Raise.Event<TestEvent>(t => t.Topic("test-topic"))
                .Listen.To.CommandQueue("test-queue")
                .Subscribe.To.Topic("test-topic"));

        var provider = services.BuildServiceProvider();

        // Assert - all routing interfaces resolve to the same singleton
        var commandRouting = provider.GetRequiredService<ICommandRoutingConfiguration>();
        var eventRouting = provider.GetRequiredService<IEventRoutingConfiguration>();
        var bootstrapConfig = provider.GetRequiredService<IBusBootstrapConfiguration>();

        Assert.NotNull(commandRouting);
        Assert.NotNull(eventRouting);
        Assert.NotNull(bootstrapConfig);
        Assert.Same(commandRouting, eventRouting);
        Assert.Same(commandRouting, bootstrapConfig);
    }

    [Fact]
    public void UseSourceFlowAzure_RegistersOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SourceFlow:Azure:ServiceBus:ConnectionString"] = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=testkey="
            })
            .Build());

        // Act
        services.UseSourceFlowAzure(
            options =>
            {
                options.EnableCommandRouting = true;
                options.EnableEventRouting = true;
                options.EnableCommandListener = false;
                options.EnableEventListener = false;
            },
            bus => bus.Listen.To.CommandQueue("test-queue"));

        var provider = services.BuildServiceProvider();

        // Assert
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AzureOptions>>();
        Assert.False(options.Value.EnableCommandListener);
        Assert.False(options.Value.EnableEventListener);
    }
}

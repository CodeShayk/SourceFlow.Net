using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Cloud.AWS.Configuration;

namespace SourceFlow.Cloud.AWS.Tests.Unit;

public class IocExtensionsTests
{
    [Fact]
    public void UseSourceFlowAws_RegistersAllRequiredServices()
    {
        // Arrange
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(configuration);

        // Act
        services.UseSourceFlowAws(options =>
        {
            options.Region = Amazon.RegionEndpoint.USEast1;
        });

        var provider = services.BuildServiceProvider();

        // Assert
        var awsOptions = provider.GetRequiredService<AwsOptions>();
        var commandRouting = provider.GetRequiredService<IAwsCommandRoutingConfiguration>();
        var eventRouting = provider.GetRequiredService<IAwsEventRoutingConfiguration>();
        
        Assert.NotNull(awsOptions);
        Assert.NotNull(commandRouting);
        Assert.NotNull(eventRouting);
    }
}
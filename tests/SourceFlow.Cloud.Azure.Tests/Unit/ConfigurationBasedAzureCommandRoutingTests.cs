using Xunit;
using Microsoft.Extensions.Configuration;
using SourceFlow.Cloud.Azure.Configuration;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging;

namespace SourceFlow.Cloud.Azure.Tests.Unit;

public class ConfigurationBasedAzureCommandRoutingTests
{
    [Fact]
    public void ShouldRouteToAzure_WithConfigRouteTrue_ReturnsTrue()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                {"SourceFlow:Azure:Commands:Routes:TestCommand:RouteToAzure", "true"}
            })
            .Build();

        var routing = new ConfigurationBasedAzureCommandRouting(config);

        // Act
        var result = routing.ShouldRouteToAzure<TestCommand>();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ShouldRouteToAzure_WithConfigRouteFalse_ReturnsFalse()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                {"SourceFlow:Azure:Commands:Routes:TestCommand:RouteToAzure", "false"}
            })
            .Build();

        var routing = new ConfigurationBasedAzureCommandRouting(config);

        // Act
        var result = routing.ShouldRouteToAzure<TestCommand>();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void GetQueueName_WithConfigQueueName_ReturnsConfigQueueName()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                {"SourceFlow:Azure:Commands:Routes:TestCommand:QueueName", "test-queue"}
            })
            .Build();

        var routing = new ConfigurationBasedAzureCommandRouting(config);

        // Act
        var result = routing.GetQueueName<TestCommand>();

        // Assert
        Assert.Equal("test-queue", result);
    }

    [Fact]
    public void GetListeningQueues_WithConfigQueues_ReturnsConfigQueues()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                {"SourceFlow:Azure:Commands:ListeningQueues:0", "queue1"},
                {"SourceFlow:Azure:Commands:ListeningQueues:1", "queue2"}
            })
            .Build();

        var routing = new ConfigurationBasedAzureCommandRouting(config);

        // Act
        var result = routing.GetListeningQueues().ToList();

        // Assert
        Assert.Contains("queue1", result);
        Assert.Contains("queue2", result);
    }

    private class TestCommand : ICommand
    {
        public IPayload Payload { get; set; } = null!;
        public EntityRef Entity { get; set; } = null!;
        public string Name { get; set; } = null!;
        public Metadata Metadata { get; set; } = null!;
    }
}
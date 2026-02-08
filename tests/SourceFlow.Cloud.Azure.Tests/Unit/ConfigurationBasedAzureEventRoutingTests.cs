using Xunit;
using Microsoft.Extensions.Configuration;
using SourceFlow.Cloud.Azure.Configuration;
using SourceFlow.Messaging.Events;
using SourceFlow.Messaging;

namespace SourceFlow.Cloud.Azure.Tests.Unit;

public class ConfigurationBasedAzureEventRoutingTests
{
    [Fact]
    public void ShouldRouteToAzure_WithConfigRouteTrue_ReturnsTrue()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                {"SourceFlow:Azure:Events:Routes:TestEvent:RouteToAzure", "true"}
            })
            .Build();

        var routing = new ConfigurationBasedAzureEventRouting(config);

        // Act
        var result = routing.ShouldRouteToAzure<TestEvent>();

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
                {"SourceFlow:Azure:Events:Routes:TestEvent:RouteToAzure", "false"}
            })
            .Build();

        var routing = new ConfigurationBasedAzureEventRouting(config);

        // Act
        var result = routing.ShouldRouteToAzure<TestEvent>();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void GetTopicName_WithConfigTopicName_ReturnsConfigTopicName()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                {"SourceFlow:Azure:Events:Routes:TestEvent:TopicName", "test-topic"}
            })
            .Build();

        var routing = new ConfigurationBasedAzureEventRouting(config);

        // Act
        var result = routing.GetTopicName<TestEvent>();

        // Assert
        Assert.Equal("test-topic", result);
    }

    [Fact]
    public void GetListeningSubscriptions_WithConfigSubscriptions_ReturnsConfigSubscriptions()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                {"SourceFlow:Azure:Events:ListeningSubscriptions:0:TopicName", "topic1"},
                {"SourceFlow:Azure:Events:ListeningSubscriptions:0:SubscriptionName", "sub1"},
                {"SourceFlow:Azure:Events:ListeningSubscriptions:1:TopicName", "topic2"},
                {"SourceFlow:Azure:Events:ListeningSubscriptions:1:SubscriptionName", "sub2"}
            })
            .Build();

        var routing = new ConfigurationBasedAzureEventRouting(config);

        // Act
        var result = routing.GetListeningSubscriptions().ToList();

        // Assert
        Assert.Contains(("topic1", "sub1"), result);
        Assert.Contains(("topic2", "sub2"), result);
    }

    private class TestEvent : IEvent
    {
        public string Name { get; set; } = null!;
        public IEntity Payload { get; set; } = null!;
        public Metadata Metadata { get; set; } = null!;
    }
}
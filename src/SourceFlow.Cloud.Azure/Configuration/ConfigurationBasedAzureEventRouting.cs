using Microsoft.Extensions.Configuration;
using System.Reflection;
using SourceFlow.Cloud.Azure.Attributes;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Cloud.Azure.Configuration;

public class ConfigurationBasedAzureEventRouting : IAzureEventRoutingConfiguration
{
    private readonly IConfiguration _configuration;

    public ConfigurationBasedAzureEventRouting(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public bool ShouldRouteToAzure<TEvent>() where TEvent : IEvent
    {
        // 1. Check attribute first (highest priority)
        var attribute = typeof(TEvent).GetCustomAttribute<AzureEventRoutingAttribute>();
        if (attribute != null)
            return attribute.RouteToAzure;

        // 2. Check configuration
        var eventType = typeof(TEvent).FullName;
        var routeSetting = _configuration[$"SourceFlow:Azure:Events:Routes:{eventType}:RouteToAzure"];

        // If we can't find the specific full name, try with just the type name
        if (string.IsNullOrEmpty(routeSetting))
        {
            var simpleTypeName = typeof(TEvent).Name;
            routeSetting = _configuration[$"SourceFlow:Azure:Events:Routes:{simpleTypeName}:RouteToAzure"];
        }

        if (bool.TryParse(routeSetting, out var routeToAzure))
        {
            return routeToAzure;
        }

        // 3. Use default (false)
        return false;
    }

    public string GetTopicName<TEvent>() where TEvent : IEvent
    {
        // 1. Check attribute first (highest priority)
        var attribute = typeof(TEvent).GetCustomAttribute<AzureEventRoutingAttribute>();
        if (attribute != null && !string.IsNullOrEmpty(attribute.TopicName))
        {
            return attribute.TopicName;
        }

        // 2. Check configuration
        var eventType = typeof(TEvent).FullName;
        var topicName = _configuration[$"SourceFlow:Azure:Events:Routes:{eventType}:TopicName"];

        // If we can't find the specific full name, try with just the type name
        if (string.IsNullOrEmpty(topicName))
        {
            var simpleTypeName = typeof(TEvent).Name;
            topicName = _configuration[$"SourceFlow:Azure:Events:Routes:{simpleTypeName}:TopicName"];
        }

        if (!string.IsNullOrEmpty(topicName))
        {
            return topicName;
        }

        // 3. Throw exception if no topic configured (safer than silent default)
        throw new InvalidOperationException($"No topic name configured for event type: {typeof(TEvent).Name}");
    }

    public IEnumerable<(string TopicName, string SubscriptionName)> GetListeningSubscriptions()
    {
        var subscriptionsSection = _configuration.GetSection("SourceFlow:Azure:Events:ListeningSubscriptions");
        if (subscriptionsSection.Exists())
        {
            foreach (var subSection in subscriptionsSection.GetChildren())
            {
                var topicName = subSection["TopicName"];
                var subscriptionName = subSection["SubscriptionName"];

                if (!string.IsNullOrEmpty(topicName) && !string.IsNullOrEmpty(subscriptionName))
                {
                    yield return (topicName, subscriptionName);
                }
            }
        }
    }
}
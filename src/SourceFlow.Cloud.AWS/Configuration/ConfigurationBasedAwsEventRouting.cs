using Microsoft.Extensions.Configuration;
using SourceFlow.Cloud.AWS.Attributes;
using SourceFlow.Messaging.Events;
using System.Reflection;

namespace SourceFlow.Cloud.AWS.Configuration;

public class ConfigurationBasedAwsEventRouting : IAwsEventRoutingConfiguration
{
    private readonly IConfiguration _configuration;
    private readonly Dictionary<Type, AwsEventRoute> _routes;

    public ConfigurationBasedAwsEventRouting(IConfiguration configuration)
    {
        _configuration = configuration;
        _routes = LoadRoutesFromConfiguration();
    }

    public bool ShouldRouteToAws<TEvent>() where TEvent : IEvent
    {
        // 1. Check attribute first
        var attribute = typeof(TEvent).GetCustomAttribute<AwsEventRoutingAttribute>();
        if (attribute != null)
            return attribute.RouteToAws;

        // 2. Check configuration
        if (_routes.TryGetValue(typeof(TEvent), out var route))
            return route.RouteToAws;

        // 3. Use default routing
        var defaultRouting = _configuration["SourceFlow:Aws:Events:DefaultRouting"];
        return defaultRouting?.Equals("Aws", StringComparison.OrdinalIgnoreCase) ?? false;
    }

    public string GetTopicArn<TEvent>() where TEvent : IEvent
    {
        var attribute = typeof(TEvent).GetCustomAttribute<AwsEventRoutingAttribute>();
        if (attribute != null && !string.IsNullOrEmpty(attribute.TopicArn))
            return attribute.TopicArn;

        if (_routes.TryGetValue(typeof(TEvent), out var route))
            return route.TopicArn;

        throw new InvalidOperationException($"No topic ARN configured for event type: {typeof(TEvent).Name}");
    }

    public IEnumerable<string> GetListeningQueues()
    {
        var listeningQueues = _configuration.GetSection("SourceFlow:Aws:Events:ListeningQueues");
        return listeningQueues.GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrEmpty(v));
    }

    private Dictionary<Type, AwsEventRoute> LoadRoutesFromConfiguration()
    {
        var routes = new Dictionary<Type, AwsEventRoute>();
        var routesSection = _configuration.GetSection("SourceFlow:Aws:Events:Routes");

        foreach (var routeSection in routesSection.GetChildren())
        {
            var eventTypeString = routeSection["EventType"];
            var topicArn = routeSection["TopicArn"];
            var routeToAws = bool.Parse(routeSection["RouteToAws"] ?? "true");

            var eventType = Type.GetType(eventTypeString);
            if (eventType != null && typeof(IEvent).IsAssignableFrom(eventType))
            {
                routes[eventType] = new AwsEventRoute
                {
                    TopicArn = topicArn,
                    RouteToAws = routeToAws
                };
            }
        }

        return routes;
    }
}

internal class AwsEventRoute
{
    public string TopicArn { get; set; }
    public bool RouteToAws { get; set; }
}
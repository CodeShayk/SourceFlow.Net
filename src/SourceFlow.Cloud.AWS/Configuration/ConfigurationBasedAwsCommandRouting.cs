using Microsoft.Extensions.Configuration;
using SourceFlow.Cloud.AWS.Attributes;
using SourceFlow.Messaging.Commands;
using System.Reflection;

namespace SourceFlow.Cloud.AWS.Configuration;

public class ConfigurationBasedAwsCommandRouting : IAwsCommandRoutingConfiguration
{
    private readonly IConfiguration _configuration;
    private readonly Dictionary<Type, AwsCommandRoute> _routes;

    public ConfigurationBasedAwsCommandRouting(IConfiguration configuration)
    {
        _configuration = configuration;
        _routes = LoadRoutesFromConfiguration();
    }

    public bool ShouldRouteToAws<TCommand>() where TCommand : ICommand
    {
        // 1. Check attribute first
        var attribute = typeof(TCommand).GetCustomAttribute<AwsCommandRoutingAttribute>();
        if (attribute != null)
            return attribute.RouteToAws;

        // 2. Check configuration
        if (_routes.TryGetValue(typeof(TCommand), out var route))
            return route.RouteToAws;

        // 3. Use default routing
        var defaultRouting = _configuration["SourceFlow:Aws:Commands:DefaultRouting"];
        return defaultRouting?.Equals("Aws", StringComparison.OrdinalIgnoreCase) ?? false;
    }

    public string GetQueueUrl<TCommand>() where TCommand : ICommand
    {
        var attribute = typeof(TCommand).GetCustomAttribute<AwsCommandRoutingAttribute>();
        if (attribute != null && !string.IsNullOrEmpty(attribute.QueueUrl))
            return attribute.QueueUrl;

        if (_routes.TryGetValue(typeof(TCommand), out var route))
            return route.QueueUrl;

        throw new InvalidOperationException($"No queue URL configured for command type: {typeof(TCommand).Name}");
    }

    public IEnumerable<string> GetListeningQueues()
    {
        var listeningQueues = _configuration.GetSection("SourceFlow:Aws:Commands:ListeningQueues");
        return listeningQueues.GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrEmpty(v));
    }

    private Dictionary<Type, AwsCommandRoute> LoadRoutesFromConfiguration()
    {
        var routes = new Dictionary<Type, AwsCommandRoute>();
        var routesSection = _configuration.GetSection("SourceFlow:Aws:Commands:Routes");

        foreach (var routeSection in routesSection.GetChildren())
        {
            var commandTypeString = routeSection["CommandType"];
            var queueUrl = routeSection["QueueUrl"];
            var routeToAws = bool.Parse(routeSection["RouteToAws"] ?? "true");

            var commandType = Type.GetType(commandTypeString);
            if (commandType != null && typeof(ICommand).IsAssignableFrom(commandType))
            {
                routes[commandType] = new AwsCommandRoute
                {
                    QueueUrl = queueUrl,
                    RouteToAws = routeToAws
                };
            }
        }

        return routes;
    }
}

internal class AwsCommandRoute
{
    public string QueueUrl { get; set; }
    public bool RouteToAws { get; set; }
}
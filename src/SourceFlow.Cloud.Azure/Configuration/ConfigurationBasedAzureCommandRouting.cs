using Microsoft.Extensions.Configuration;
using System.Reflection;
using SourceFlow.Cloud.Azure.Attributes;
using SourceFlow.Messaging.Commands;

namespace SourceFlow.Cloud.Azure.Configuration;

public class ConfigurationBasedAzureCommandRouting : IAzureCommandRoutingConfiguration
{
    private readonly IConfiguration _configuration;

    public ConfigurationBasedAzureCommandRouting(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public bool ShouldRouteToAzure<TCommand>() where TCommand : ICommand
    {
        // 1. Check attribute first (highest priority)
        var attribute = typeof(TCommand).GetCustomAttribute<AzureCommandRoutingAttribute>();
        if (attribute != null)
            return attribute.RouteToAzure;

        // 2. Check configuration
        var commandType = typeof(TCommand).FullName;
        var routeSetting = _configuration[$"SourceFlow:Azure:Commands:Routes:{commandType}:RouteToAzure"];

        // If we can't find the specific full name, try with just the type name
        if (string.IsNullOrEmpty(routeSetting))
        {
            var simpleTypeName = typeof(TCommand).Name;
            routeSetting = _configuration[$"SourceFlow:Azure:Commands:Routes:{simpleTypeName}:RouteToAzure"];
        }

        if (bool.TryParse(routeSetting, out var routeToAzure))
        {
            return routeToAzure;
        }

        // 3. Use default (false)
        return false;
    }

    public string GetQueueName<TCommand>() where TCommand : ICommand
    {
        // 1. Check attribute first (highest priority)
        var attribute = typeof(TCommand).GetCustomAttribute<AzureCommandRoutingAttribute>();
        if (attribute != null && !string.IsNullOrEmpty(attribute.QueueName))
        {
            return attribute.QueueName;
        }

        // 2. Check configuration
        var commandType = typeof(TCommand).FullName;
        var queueName = _configuration[$"SourceFlow:Azure:Commands:Routes:{commandType}:QueueName"];

        // If we can't find the specific full name, try with just the type name
        if (string.IsNullOrEmpty(queueName))
        {
            var simpleTypeName = typeof(TCommand).Name;
            queueName = _configuration[$"SourceFlow:Azure:Commands:Routes:{simpleTypeName}:QueueName"];
        }

        if (!string.IsNullOrEmpty(queueName))
        {
            return queueName;
        }

        // 3. Throw exception if no queue configured (safer than silent default)
        throw new InvalidOperationException($"No queue name configured for command type: {typeof(TCommand).Name}");
    }

    public IEnumerable<string> GetListeningQueues()
    {
        var queuesConfig = _configuration.GetSection("SourceFlow:Azure:Commands:ListeningQueues");
        if (queuesConfig.Exists())
        {
            foreach (var queue in queuesConfig.GetChildren())
            {
                yield return queue.Value ?? string.Empty;
            }
        }
    }
}
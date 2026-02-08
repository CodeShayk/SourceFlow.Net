using Amazon.SQS;
using Amazon.SimpleNotificationService;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SourceFlow.Cloud.AWS.Configuration;
using SourceFlow.Cloud.AWS.Infrastructure;
using SourceFlow.Cloud.AWS.Messaging.Commands;
using SourceFlow.Cloud.AWS.Messaging.Events;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Cloud.AWS;

public static class IocExtensions
{
    public static void UseSourceFlowAws(
        this IServiceCollection services,
        Action<AwsOptions> configureOptions)
    {
        // 1. Configure options
        var options = new AwsOptions();
        configureOptions(options);
        services.AddSingleton(options);

        // 2. Register AWS clients
        services.AddAWSService<IAmazonSQS>();
        services.AddAWSService<IAmazonSimpleNotificationService>();

        // 3. Register routing configurations
        services.AddSingleton<IAwsCommandRoutingConfiguration, ConfigurationBasedAwsCommandRouting>();
        services.AddSingleton<IAwsEventRoutingConfiguration, ConfigurationBasedAwsEventRouting>();

        // 4. Register AWS dispatchers
        services.AddScoped<ICommandDispatcher, AwsSqsCommandDispatcher>();
        services.AddSingleton<IEventDispatcher, AwsSnsEventDispatcher>();

        // 5. Register AWS listeners as hosted services
        services.AddHostedService<AwsSqsCommandListener>();
        services.AddHostedService<AwsSnsEventListener>();

        // 6. Register health check
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthCheck, AwsHealthCheck>(
            provider => new AwsHealthCheck(
                provider.GetRequiredService<IAmazonSQS>(),
                provider.GetRequiredService<IAmazonSimpleNotificationService>(),
                provider.GetRequiredService<IAwsCommandRoutingConfiguration>(),
                provider.GetRequiredService<IAwsEventRoutingConfiguration>())));
    }
}
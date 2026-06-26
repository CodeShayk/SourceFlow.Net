using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using SourceFlow.Cloud.Configuration;
using SourceFlow.Cloud.GCP;
using SourceFlow.Cloud.GCP.Configuration;
using SourceFlow.Cloud.GCP.Tests.TestHelpers;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Cloud.GCP.Tests.Unit;

[Trait("Category", TestCategories.Unit)]
public class GcpIocExtensionsTests
{
    private static IServiceCollection Configure()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.UseSourceFlowGcp(
            options => { options.ProjectId = "test-project"; },
            bus => bus
                .Send.Command<TestCommand>(q => q.Queue("orders"))
                .Raise.Event<TestEvent>(t => t.Topic("order-events"))
                .Listen.To.CommandQueue("orders")
                .Subscribe.To.Topic("order-events"));
        return services;
    }

    [Fact]
    public void Registers_Dispatchers_And_RoutingConfiguration()
    {
        var services = Configure();

        Assert.Contains(services, d => d.ServiceType == typeof(ICommandDispatcher));
        Assert.Contains(services, d => d.ServiceType == typeof(IEventDispatcher));
        Assert.Contains(services, d => d.ServiceType == typeof(ICommandRoutingConfiguration));
        Assert.Contains(services, d => d.ServiceType == typeof(IEventRoutingConfiguration));
        Assert.Contains(services, d => d.ServiceType == typeof(IBusBootstrapConfiguration));
        Assert.Contains(services, d => d.ServiceType == typeof(GcpOptions));
    }

    [Fact]
    public void Registers_InMemory_Idempotency_AsSingleton()
    {
        var services = Configure();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IIdempotencyService));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void Registers_Bootstrapper_And_Listeners_As_HostedServices()
    {
        var services = Configure();

        var hosted = services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ToList();

        Assert.Contains(typeof(SourceFlow.Cloud.GCP.Infrastructure.GcpBusBootstrapper), hosted);
        Assert.Contains(typeof(SourceFlow.Cloud.GCP.Messaging.Commands.PubSubCommandListener), hosted);
        Assert.Contains(typeof(SourceFlow.Cloud.GCP.Messaging.Events.PubSubEventListener), hosted);
    }

    [Fact]
    public void Registers_HealthCheck()
    {
        var services = Configure();
        Assert.Contains(services, d => d.ServiceType == typeof(IHealthCheck));
    }

    [Fact]
    public void Listeners_NotRegistered_When_Disabled()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.UseSourceFlowGcp(
            options => { options.ProjectId = "test-project"; options.EnableCommandListener = false; options.EnableEventListener = false; },
            bus => bus.Send.Command<TestCommand>(q => q.Queue("orders")));

        var hosted = services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ToList();

        Assert.DoesNotContain(typeof(SourceFlow.Cloud.GCP.Messaging.Commands.PubSubCommandListener), hosted);
        Assert.DoesNotContain(typeof(SourceFlow.Cloud.GCP.Messaging.Events.PubSubEventListener), hosted);
    }
}

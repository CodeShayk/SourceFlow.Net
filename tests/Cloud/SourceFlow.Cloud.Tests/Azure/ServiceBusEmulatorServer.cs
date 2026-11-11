using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using SourceFlow.Cloud.Tests.Azure;

[assembly: AssemblyFixture(typeof(ServiceBusEmulatorServer))]

namespace SourceFlow.Cloud.Tests.Azure;

/// <summary>
/// One Service Bus emulator per test run: the one in <c>SERVICEBUS_EMULATOR_CONNECTION</c> and
/// <c>SERVICEBUS_EMULATOR_ADMIN_CONNECTION</c> (CI starts the pinned compose service), or Testcontainers containers of the
/// same pinned images (the emulator and its SQL Edge store). Entities are created at runtime through the administration
/// endpoint, so the configuration declares the namespace only. It starts on first use, so runs without Azure tests need no
/// Docker.
/// </summary>
public sealed class ServiceBusEmulatorServer : IAsyncLifetime
{
    /// <summary>The pinned emulator image (.github/emulators/servicebus).</summary>
    public const string Image = "mcr.microsoft.com/azure-messaging/servicebus-emulator:2.0.1";

    /// <summary>The pinned SQL Edge image the emulator stores its state in.</summary>
    public const string SqlImage = "mcr.microsoft.com/azure-sql-edge:2.0.0";

    /// <summary>The shared access key every emulator accepts.</summary>
    public const string Credentials = "SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    private const string Config = """{ "UserConfig": { "Namespaces": [ { "Name": "sbemulatorns", "Queues": [], "Topics": [] } ], "Logging": { "Type": "Console" } } }""";
    private const string SqlPassword = "SourceFlow_Emulator_1";

    private readonly Lazy<Task> _started;
    private INetwork? _network;
    private IContainer? _sql;
    private IContainer? _emulator;

    public ServiceBusEmulatorServer()
    {
        _started = new Lazy<Task>(StartAsync);
    }

    /// <summary>The AMQP host.</summary>
    public string Host { get; private set; } = null!;

    /// <summary>The AMQP port.</summary>
    public int AmqpPort { get; private set; }

    /// <summary>The connection string of the administration (HTTP) endpoint.</summary>
    public string AdministrationConnectionString { get; private set; } = null!;

    /// <summary>The messaging connection string to <paramref name="host"/>:<paramref name="port"/> (the emulator, or a proxy in front of it).</summary>
    public static string ConnectionString(string host, int port)
    {
        return $"Endpoint=sb://{host}:{port};{Credentials}";
    }

    public ValueTask InitializeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public Task EnsureStartedAsync()
    {
        return _started.Value;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IAsyncDisposable? resource in new IAsyncDisposable?[] { _emulator, _sql, _network })
        {
            if (resource is not null)
            {
                await resource.DisposeAsync();
            }
        }
    }

    private async Task StartAsync()
    {
        if (Environment.GetEnvironmentVariable("SERVICEBUS_EMULATOR_CONNECTION") is { Length: > 0 } external)
        {
            var endpoint = new Uri(external.Split(';')[0]["Endpoint=".Length..]);
            Host = endpoint.Host;
            AmqpPort = endpoint.Port > 0 ? endpoint.Port : 5672;
            AdministrationConnectionString = Environment.GetEnvironmentVariable("SERVICEBUS_EMULATOR_ADMIN_CONNECTION") ?? ConnectionString(Host, 5300);
            return;
        }

        _network = new NetworkBuilder().Build();
        await _network.CreateAsync();
        _sql = new ContainerBuilder(SqlImage)
            .WithNetwork(_network)
            .WithNetworkAliases("sqledge")
            .WithEnvironment("ACCEPT_EULA", "Y")
            .WithEnvironment("MSSQL_SA_PASSWORD", SqlPassword)
            .Build();
        await _sql.StartAsync();
        _emulator = new ContainerBuilder(Image)
            .WithNetwork(_network)
            .WithPortBinding(5672, assignRandomHostPort: true)
            .WithPortBinding(5300, assignRandomHostPort: true)
            .WithResourceMapping(Encoding.UTF8.GetBytes(Config), "/ServiceBus_Emulator/ConfigFiles/Config.json")
            .WithEnvironment("SQL_SERVER", "sqledge")
            .WithEnvironment("MSSQL_SA_PASSWORD", SqlPassword)
            .WithEnvironment("ACCEPT_EULA", "Y")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Emulator Service is Successfully Up"))
            .Build();
        await _emulator.StartAsync();
        Host = _emulator.Hostname;
        AmqpPort = _emulator.GetMappedPublicPort(5672);
        AdministrationConnectionString = ConnectionString(Host, _emulator.GetMappedPublicPort(5300));
    }
}

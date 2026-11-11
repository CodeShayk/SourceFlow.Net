using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using SourceFlow.Cloud.Tests.Gcp;

[assembly: AssemblyFixture(typeof(PubSubEmulatorServer))]

namespace SourceFlow.Cloud.Tests.Gcp;

/// <summary>
/// One Pub/Sub emulator per test run: the one in <c>PUBSUB_EMULATOR_HOST</c> (CI starts the pinned compose service), or a
/// Testcontainers container of the same pinned image. It starts on first use, so runs without GCP tests need no Docker.
/// </summary>
public sealed class PubSubEmulatorServer : IAsyncLifetime
{
    /// <summary>The pinned image (.github/emulators/pubsub).</summary>
    public const string Image = "gcr.io/google.com/cloudsdktool/google-cloud-cli:590.0.1-emulators";

    /// <summary>The project the tests use (the emulator accepts any).</summary>
    public const string ProjectId = "sourceflow-emulator";

    private readonly Lazy<Task> _started;
    private IContainer? _container;

    public PubSubEmulatorServer()
    {
        _started = new Lazy<Task>(StartAsync);
    }

    /// <summary>The emulator's <c>host:port</c>.</summary>
    public string Host { get; private set; } = null!;

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
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private async Task StartAsync()
    {
        if (Environment.GetEnvironmentVariable("PUBSUB_EMULATOR_HOST") is { Length: > 0 } external)
        {
            Host = external;
            return;
        }

        _container = new ContainerBuilder(Image)
            .WithPortBinding(8085, assignRandomHostPort: true)
            .WithCommand("gcloud", "beta", "emulators", "pubsub", "start", "--host-port=0.0.0.0:8085", "--project=" + ProjectId)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server started, listening on"))
            .Build();
        await _container.StartAsync();
        Host = $"{_container.Hostname}:{_container.GetMappedPublicPort(8085)}";
    }
}

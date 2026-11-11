using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using SourceFlow.Cloud.Tests.Aws;

[assembly: AssemblyFixture(typeof(LocalStackServer))]

namespace SourceFlow.Cloud.Tests.Aws;

/// <summary>
/// One LocalStack per test run: the endpoint in <c>AWS_ENDPOINT_URL</c> (CI starts the pinned compose service), or a
/// Testcontainers container of the same pinned image. It starts on first use, so runs without AWS tests need no Docker.
/// </summary>
public sealed class LocalStackServer : IAsyncLifetime
{
    /// <summary>The pinned image (.github/emulators/localstack).</summary>
    public const string Image = "localstack/localstack:3.8";

    /// <summary>The region the tests use.</summary>
    public const string Region = "eu-west-1";

    private readonly Lazy<Task> _started;
    private IContainer? _container;

    public LocalStackServer()
    {
        _started = new Lazy<Task>(StartAsync);
    }

    /// <summary>The LocalStack endpoint.</summary>
    public Uri Endpoint { get; private set; } = null!;

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
        if (Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL") is { Length: > 0 } external)
        {
            Endpoint = new Uri(external);
            return;
        }

        _container = new ContainerBuilder(Image)
            .WithPortBinding(4566, assignRandomHostPort: true)
            .WithEnvironment("SERVICES", "sqs,sns,kms")
            .WithEnvironment("EAGER_SERVICE_LOADING", "1")
            .WithEnvironment("SKIP_SSL_CERT_DOWNLOAD", "1")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(4566).ForPath("/_localstack/health")))
            .Build();
        await _container.StartAsync();
        Endpoint = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(4566)}");
    }
}

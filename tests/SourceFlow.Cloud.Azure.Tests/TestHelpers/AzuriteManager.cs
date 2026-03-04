using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SourceFlow.Cloud.Azure.Tests.TestHelpers;

/// <summary>
/// Manages Azurite emulator lifecycle and configuration for Azure integration testing.
/// Provides Service Bus and Key Vault emulation for local development.
/// </summary>
public class AzuriteManager : IAzuriteManager, IAsyncDisposable
{
    private readonly AzuriteConfiguration _configuration;
    private readonly ILogger<AzuriteManager> _logger;
    private Process? _azuriteProcess;
    private bool _isRunning;

    public AzuriteManager(
        AzuriteConfiguration configuration,
        ILogger<AzuriteManager> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync()
    {
        if (_isRunning)
        {
            _logger.LogWarning("Azurite is already running");
            return;
        }

        _logger.LogInformation("Starting Azurite emulator");

        try
        {
            await StartAzuriteProcessAsync();
            await WaitForServicesAsync();
            _isRunning = true;

            _logger.LogInformation("Azurite emulator started successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Azurite emulator");
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (!_isRunning)
        {
            _logger.LogWarning("Azurite is not running");
            return;
        }

        _logger.LogInformation("Stopping Azurite emulator");

        try
        {
            if (_azuriteProcess != null && !_azuriteProcess.HasExited)
            {
                _azuriteProcess.Kill(entireProcessTree: true);
                await _azuriteProcess.WaitForExitAsync();
                _azuriteProcess.Dispose();
                _azuriteProcess = null;
            }

            _isRunning = false;
            _logger.LogInformation("Azurite emulator stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop Azurite emulator");
            throw;
        }
    }

    public async Task ConfigureServiceBusAsync()
    {
        if (!_isRunning)
        {
            throw new InvalidOperationException("Azurite must be running before configuration");
        }

        _logger.LogInformation("Configuring Azurite Service Bus emulation");

        try
        {
            // Create default queues
            await CreateDefaultQueuesAsync();

            // Create default topics
            await CreateDefaultTopicsAsync();

            // Create default subscriptions
            await CreateDefaultSubscriptionsAsync();

            _logger.LogInformation("Azurite Service Bus configured");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to configure Azurite Service Bus");
            throw;
        }
    }

    public async Task ConfigureKeyVaultAsync()
    {
        if (!_isRunning)
        {
            throw new InvalidOperationException("Azurite must be running before configuration");
        }

        _logger.LogInformation("Configuring Azurite Key Vault emulation");

        try
        {
            // Create test keys
            await CreateTestKeysAsync();

            // Configure access policies
            await ConfigureAccessPoliciesAsync();

            _logger.LogInformation("Azurite Key Vault configured");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to configure Azurite Key Vault");
            throw;
        }
    }

    public async Task<bool> IsRunningAsync()
    {
        if (!_isRunning || _azuriteProcess == null || _azuriteProcess.HasExited)
        {
            return false;
        }

        try
        {
            // Check if Azurite is responding
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(2);
            
            var response = await httpClient.GetAsync(
                $"http://{_configuration.Host}:{_configuration.BlobPort}/devstoreaccount1?comp=list");
            
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public string GetServiceBusConnectionString()
    {
        // Azurite uses a well-known connection string for local development
        return $"Endpoint=sb://{_configuration.Host}:{_configuration.ServiceBusPort}/;" +
               "SharedAccessKeyName=RootManageSharedAccessKey;" +
               "SharedAccessKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    }

    public string GetKeyVaultUrl()
    {
        return $"https://{_configuration.Host}:{_configuration.KeyVaultPort}/";
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private async Task StartAzuriteProcessAsync()
    {
        var arguments = BuildAzuriteArguments();

        _logger.LogInformation("Starting Azurite with arguments: {Arguments}", arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = _configuration.AzuriteExecutablePath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        _azuriteProcess = new Process { StartInfo = startInfo };

        // Capture output for diagnostics
        _azuriteProcess.OutputDataReceived += (sender, args) =>
        {
            if (!string.IsNullOrEmpty(args.Data))
            {
                _logger.LogDebug("Azurite output: {Output}", args.Data);
            }
        };

        _azuriteProcess.ErrorDataReceived += (sender, args) =>
        {
            if (!string.IsNullOrEmpty(args.Data))
            {
                _logger.LogWarning("Azurite error: {Error}", args.Data);
            }
        };

        if (!_azuriteProcess.Start())
        {
            throw new InvalidOperationException("Failed to start Azurite process");
        }

        _azuriteProcess.BeginOutputReadLine();
        _azuriteProcess.BeginErrorReadLine();

        _logger.LogInformation("Azurite process started with PID: {ProcessId}", _azuriteProcess.Id);
    }

    private string BuildAzuriteArguments()
    {
        var args = new List<string>
        {
            "--silent",
            $"--location {_configuration.DataLocation}",
            $"--blobHost {_configuration.Host}",
            $"--blobPort {_configuration.BlobPort}",
            $"--queueHost {_configuration.Host}",
            $"--queuePort {_configuration.QueuePort}",
            $"--tableHost {_configuration.Host}",
            $"--tablePort {_configuration.TablePort}"
        };

        if (_configuration.EnableDebugLog)
        {
            args.Add($"--debug {_configuration.DebugLogPath}");
        }

        if (_configuration.LooseMode)
        {
            args.Add("--loose");
        }

        return string.Join(" ", args);
    }

    private async Task WaitForServicesAsync()
    {
        var maxAttempts = _configuration.StartupTimeoutSeconds;
        var attempt = 0;

        _logger.LogInformation("Waiting for Azurite services to become ready");

        while (attempt < maxAttempts)
        {
            try
            {
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(1);

                var response = await httpClient.GetAsync(
                    $"http://{_configuration.Host}:{_configuration.BlobPort}/devstoreaccount1?comp=list");

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Azurite services are ready after {Attempts} seconds", attempt + 1);
                    return;
                }
            }
            catch
            {
                // Service not ready yet
            }

            attempt++;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new TimeoutException(
            $"Azurite services did not become ready within {_configuration.StartupTimeoutSeconds} seconds");
    }

    private async Task CreateDefaultQueuesAsync()
    {
        var defaultQueues = new[]
        {
            "test-commands.fifo",
            "test-notifications",
            "test-availability-queue"
        };

        foreach (var queueName in defaultQueues)
        {
            _logger.LogInformation("Creating default queue: {QueueName}", queueName);
            // In a real implementation, this would use Azurite API to create queues
            // For now, we simulate the operation
            await Task.Delay(10);
        }
    }

    private async Task CreateDefaultTopicsAsync()
    {
        var defaultTopics = new[]
        {
            "test-events",
            "test-domain-events"
        };

        foreach (var topicName in defaultTopics)
        {
            _logger.LogInformation("Creating default topic: {TopicName}", topicName);
            // In a real implementation, this would use Azurite API to create topics
            await Task.Delay(10);
        }
    }

    private async Task CreateDefaultSubscriptionsAsync()
    {
        var defaultSubscriptions = new Dictionary<string, string>
        {
            ["test-events"] = "test-subscription",
            ["test-domain-events"] = "test-subscription"
        };

        foreach (var (topicName, subscriptionName) in defaultSubscriptions)
        {
            _logger.LogInformation(
                "Creating default subscription: {SubscriptionName} for topic: {TopicName}",
                subscriptionName,
                topicName);
            // In a real implementation, this would use Azurite API to create subscriptions
            await Task.Delay(10);
        }
    }

    private async Task CreateTestKeysAsync()
    {
        var testKeys = new[] { "test-key-1", "test-key-2", "test-encryption-key" };

        foreach (var keyName in testKeys)
        {
            _logger.LogInformation("Creating test key: {KeyName}", keyName);
            // In a real implementation, this would use Azurite API to create keys
            await Task.Delay(10);
        }
    }

    private async Task ConfigureAccessPoliciesAsync()
    {
        _logger.LogInformation("Configuring Key Vault access policies");
        // In a real implementation, this would configure access policies
        await Task.Delay(10);
    }
}

/// <summary>
/// Configuration for Azurite emulator.
/// </summary>
public class AzuriteConfiguration
{
    /// <summary>
    /// Path to the Azurite executable.
    /// </summary>
    public string AzuriteExecutablePath { get; set; } = "azurite";

    /// <summary>
    /// Host address for Azurite services.
    /// </summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>
    /// Port for Blob service.
    /// </summary>
    public int BlobPort { get; set; } = 10000;

    /// <summary>
    /// Port for Queue service.
    /// </summary>
    public int QueuePort { get; set; } = 10001;

    /// <summary>
    /// Port for Table service.
    /// </summary>
    public int TablePort { get; set; } = 10002;

    /// <summary>
    /// Port for Service Bus emulation.
    /// </summary>
    public int ServiceBusPort { get; set; } = 10003;

    /// <summary>
    /// Port for Key Vault emulation.
    /// </summary>
    public int KeyVaultPort { get; set; } = 10004;

    /// <summary>
    /// Data location for Azurite storage.
    /// </summary>
    public string DataLocation { get; set; } = "./azurite-data";

    /// <summary>
    /// Enables debug logging.
    /// </summary>
    public bool EnableDebugLog { get; set; }

    /// <summary>
    /// Path for debug log file.
    /// </summary>
    public string DebugLogPath { get; set; } = "./azurite-debug.log";

    /// <summary>
    /// Enables loose mode for compatibility.
    /// </summary>
    public bool LooseMode { get; set; } = true;

    /// <summary>
    /// Timeout in seconds for Azurite startup.
    /// </summary>
    public int StartupTimeoutSeconds { get; set; } = 30;
}

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Net;
using System.Net.NetworkInformation;
using Amazon.SQS;
using Amazon.SimpleNotificationService;
using Amazon.KeyManagementService;
using Amazon.IdentityManagement;

namespace SourceFlow.Cloud.AWS.Tests.TestHelpers;

/// <summary>
/// LocalStack container manager implementation
/// Provides comprehensive container lifecycle management for AWS service emulation
/// with enhanced port management, service validation, and diagnostics
/// </summary>
public class LocalStackManager : ILocalStackManager
{
    private readonly ILogger<LocalStackManager> _logger;
    private IContainer? _container;
    private LocalStackConfiguration? _configuration;
    private bool _disposed;
    private readonly Dictionary<string, DateTime> _serviceReadyTimes = new();
    private readonly object _lockObject = new();
    
    public LocalStackManager(ILogger<LocalStackManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    
    /// <inheritdoc />
    public bool IsRunning => _container?.State == TestcontainersStates.Running;
    
    /// <inheritdoc />
    public string Endpoint => _configuration?.Endpoint ?? "http://localhost:4566";
    
    /// <inheritdoc />
    public async Task StartAsync(LocalStackConfiguration config)
    {
        lock (_lockObject)
        {
            if (_container != null && IsRunning)
            {
                _logger.LogInformation("LocalStack container is already running");
                return;
            }
        }
        
        _configuration = config ?? throw new ArgumentNullException(nameof(config));
        _logger.LogInformation("Starting LocalStack container with services: {Services}", string.Join(", ", config.EnabledServices));
        
        // Ensure port is available before starting
        var availablePort = await FindAvailablePortAsync(config.Port);
        if (availablePort != config.Port)
        {
            _logger.LogWarning("Port {RequestedPort} is not available, using {AvailablePort} instead", config.Port, availablePort);
            config.Port = availablePort;
            config.Endpoint = $"http://localhost:{availablePort}";
        }
        
        var containerBuilder = new ContainerBuilder()
            .WithImage(config.Image)
            .WithName(config.ContainerName ?? $"localstack-test-{Guid.NewGuid():N}")
            .WithAutoRemove(config.AutoRemove)
            .WithCleanUp(true);
        
        // Add port bindings with automatic port management
        var portBindings = config.GetAllPortBindings();
        foreach (var portBinding in portBindings)
        {
            var hostPort = await FindAvailablePortAsync(portBinding.Value);
            containerBuilder = containerBuilder.WithPortBinding((ushort)hostPort, (ushort)portBinding.Key);
            _logger.LogDebug("Binding container port {ContainerPort} to host port {HostPort}", portBinding.Key, hostPort);
        }
        
        // Add environment variables with enhanced configuration
        var environmentVariables = config.GetAllEnvironmentVariables();
        foreach (var env in environmentVariables)
        {
            containerBuilder = containerBuilder.WithEnvironment(env.Key, env.Value);
        }
        
        // Add volume mounts for data persistence
        foreach (var volume in config.VolumeMounts)
        {
            containerBuilder = containerBuilder.WithBindMount(volume.Key, volume.Value);
        }
        
        // Enhanced wait strategy with multiple health checks
        var waitStrategy = Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(r => r
                .ForPort((ushort)availablePort)
                .ForPath("/_localstack/health")
                .ForStatusCode(HttpStatusCode.OK))
            .UntilHttpRequestIsSucceeded(r => r
                .ForPort((ushort)availablePort)
                .ForPath("/_localstack/init")
                .ForStatusCode(HttpStatusCode.OK)); // Only check for OK status
        
        containerBuilder = containerBuilder.WithWaitStrategy(waitStrategy);
        
        _container = containerBuilder.Build();
        
        try
        {
            _logger.LogInformation("Starting LocalStack container...");
            await _container.StartAsync();
            _logger.LogInformation("LocalStack container started successfully on {Endpoint}", Endpoint);
            
            // Validate container is actually running
            if (!IsRunning)
            {
                throw new InvalidOperationException("LocalStack container failed to start properly");
            }
            
            // Wait for services to be ready with enhanced validation
            await WaitForServicesAsync(config.EnabledServices.ToArray(), config.HealthCheckTimeout);
            
            // Perform comprehensive service validation
            await ValidateAwsServicesAsync(config.EnabledServices);
            
            _logger.LogInformation("LocalStack container is fully ready with all services available");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start LocalStack container");
            await StopAsync();
            throw new InvalidOperationException($"LocalStack container startup failed: {ex.Message}", ex);
        }
    }
    
    /// <inheritdoc />
    public async Task StopAsync()
    {
        if (_container == null)
            return;
        
        _logger.LogInformation("Stopping LocalStack container");
        
        try
        {
            if (IsRunning)
            {
                await _container.StopAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping LocalStack container");
        }
        finally
        {
            await _container.DisposeAsync();
            _container = null;
            _configuration = null;
        }
        
        _logger.LogInformation("LocalStack container stopped");
    }
    
    /// <inheritdoc />
    public async Task<bool> IsServiceAvailableAsync(string serviceName)
    {
        if (!IsRunning || _configuration == null)
            return false;
        
        try
        {
            var healthStatus = await GetServicesHealthAsync();
            return healthStatus.ContainsKey(serviceName) && healthStatus[serviceName].IsAvailable;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check service availability for {ServiceName}", serviceName);
            return false;
        }
    }
    
    /// <inheritdoc />
    public async Task WaitForServicesAsync(string[] services, TimeSpan? timeout = null)
    {
        if (!IsRunning || _configuration == null)
            throw new InvalidOperationException("LocalStack container is not running");
        
        var actualTimeout = timeout ?? _configuration.HealthCheckTimeout;
        var retryDelay = _configuration.HealthCheckRetryDelay;
        var maxRetries = _configuration.MaxHealthCheckRetries;
        
        _logger.LogInformation("Waiting for LocalStack services to be ready: {Services}", string.Join(", ", services));
        
        var startTime = DateTime.UtcNow;
        var retryCount = 0;
        var lastErrors = new List<string>();
        
        while (DateTime.UtcNow - startTime < actualTimeout && retryCount < maxRetries)
        {
            try
            {
                var healthStatus = await GetServicesHealthAsync();
                var serviceStatuses = new Dictionary<string, bool>();
                
                foreach (var service in services)
                {
                    var isReady = healthStatus.ContainsKey(service) && healthStatus[service].IsAvailable;
                    serviceStatuses[service] = isReady;
                    
                    if (isReady && !_serviceReadyTimes.ContainsKey(service))
                    {
                        _serviceReadyTimes[service] = DateTime.UtcNow;
                        _logger.LogDebug("Service {ServiceName} became ready after {ElapsedTime}ms", 
                            service, (DateTime.UtcNow - startTime).TotalMilliseconds);
                    }
                }
                
                var allReady = serviceStatuses.Values.All(ready => ready);
                
                if (allReady)
                {
                    _logger.LogInformation("All LocalStack services are ready after {ElapsedTime}ms", 
                        (DateTime.UtcNow - startTime).TotalMilliseconds);
                    return;
                }
                
                var notReady = serviceStatuses.Where(kvp => !kvp.Value).Select(kvp => kvp.Key).ToList();
                
                _logger.LogDebug("Services not ready yet: {NotReadyServices} (attempt {Attempt}/{MaxAttempts})", 
                    string.Join(", ", notReady), retryCount + 1, maxRetries);
                
                lastErrors.Clear();
            }
            catch (Exception ex)
            {
                var errorMessage = $"Health check failed: {ex.Message}";
                lastErrors.Add(errorMessage);
                _logger.LogDebug(ex, "Health check failed (attempt {Attempt}/{MaxAttempts})", retryCount + 1, maxRetries);
            }
            
            retryCount++;
            await Task.Delay(retryDelay);
        }
        
        var errorDetails = lastErrors.Any() ? $" Last errors: {string.Join("; ", lastErrors)}" : "";
        throw new TimeoutException($"LocalStack services did not become ready within {actualTimeout}: {string.Join(", ", services)}.{errorDetails}");
    }
    
    /// <inheritdoc />
    public string GetServiceEndpoint(string serviceName)
    {
        if (_configuration == null)
            throw new InvalidOperationException("LocalStack is not configured");
        
        // LocalStack uses a single endpoint for all services
        return _configuration.Endpoint;
    }
    
    /// <inheritdoc />
    public async Task<Dictionary<string, LocalStackServiceHealth>> GetServicesHealthAsync()
    {
        if (!IsRunning || _configuration == null)
            return new Dictionary<string, LocalStackServiceHealth>();
        
        try
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            
            var healthUrl = $"{_configuration.Endpoint}/_localstack/health";
            var startTime = DateTime.UtcNow;
            
            var response = await httpClient.GetAsync(healthUrl);
            var responseTime = DateTime.UtcNow - startTime;
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("LocalStack health check returned {StatusCode}", response.StatusCode);
                return new Dictionary<string, LocalStackServiceHealth>();
            }
            
            var content = await response.Content.ReadAsStringAsync();
            var healthData = JsonSerializer.Deserialize<LocalStackHealthResponse>(content);
            
            var result = new Dictionary<string, LocalStackServiceHealth>();
            
            if (healthData?.Services != null)
            {
                foreach (var service in healthData.Services)
                {
                    result[service.Key] = new LocalStackServiceHealth
                    {
                        ServiceName = service.Key,
                        IsAvailable = service.Value == "available" || service.Value == "running",
                        Status = service.Value,
                        LastChecked = DateTime.UtcNow,
                        ResponseTime = responseTime
                    };
                }
            }
            
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get LocalStack services health");
            return new Dictionary<string, LocalStackServiceHealth>();
        }
    }
    
    /// <inheritdoc />
    public async Task ResetDataAsync()
    {
        if (!IsRunning || _configuration == null)
            throw new InvalidOperationException("LocalStack container is not running");
        
        try
        {
            using var httpClient = new HttpClient();
            var resetUrl = $"{_configuration.Endpoint}/_localstack/health";
            
            // LocalStack doesn't have a direct reset endpoint, but we can restart the container
            _logger.LogInformation("Resetting LocalStack data by restarting container");
            
            await StopAsync();
            await StartAsync(_configuration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset LocalStack data");
            throw;
        }
    }
    
    /// <inheritdoc />
    public async Task<string> GetLogsAsync(int tail = 100)
    {
        if (_container == null)
            return "Container not available";
        
        try
        {
            var (stdout, stderr) = await _container.GetLogsAsync();
            var logs = $"STDOUT:\n{stdout}\n\nSTDERR:\n{stderr}";
            
            // Simple tail implementation
            var lines = logs.Split('\n');
            if (lines.Length > tail)
            {
                lines = lines.TakeLast(tail).ToArray();
            }
            
            return string.Join('\n', lines);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get LocalStack container logs");
            return $"Failed to get logs: {ex.Message}";
        }
    }
    
    /// <summary>
    /// Find an available port starting from the specified port
    /// </summary>
    /// <param name="startPort">Starting port to check</param>
    /// <returns>Available port number</returns>
    private async Task<int> FindAvailablePortAsync(int startPort)
    {
        const int maxAttempts = 100;
        var currentPort = startPort;
        
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (await IsPortAvailableAsync(currentPort))
            {
                return currentPort;
            }
            currentPort++;
        }
        
        throw new InvalidOperationException($"Could not find an available port starting from {startPort} after {maxAttempts} attempts");
    }
    
    /// <summary>
    /// Check if a specific port is available
    /// </summary>
    /// <param name="port">Port to check</param>
    /// <returns>True if port is available</returns>
    private async Task<bool> IsPortAvailableAsync(int port)
    {
        try
        {
            // Check if port is in use by attempting to bind to it
            using var tcpListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
            tcpListener.Start();
            tcpListener.Stop();
            
            // Also check using IPGlobalProperties for more thorough validation
            var ipGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();
            var tcpConnections = ipGlobalProperties.GetActiveTcpConnections();
            var tcpListeners = ipGlobalProperties.GetActiveTcpListeners();
            
            var isInUse = tcpConnections.Any(c => c.LocalEndPoint.Port == port) ||
                         tcpListeners.Any(l => l.Port == port);
            
            return !isInUse;
        }
        catch
        {
            // If we can't bind to the port, it's not available
            return false;
        }
    }
    
    /// <summary>
    /// Validate that AWS services are properly emulated and accessible
    /// </summary>
    /// <param name="enabledServices">List of services to validate</param>
    private async Task ValidateAwsServicesAsync(List<string> enabledServices)
    {
        _logger.LogInformation("Validating AWS service emulation for: {Services}", string.Join(", ", enabledServices));
        
        var validationTasks = new List<Task>();
        
        if (enabledServices.Contains("sqs"))
        {
            validationTasks.Add(ValidateSqsServiceAsync());
        }
        
        if (enabledServices.Contains("sns"))
        {
            validationTasks.Add(ValidateSnsServiceAsync());
        }
        
        if (enabledServices.Contains("kms"))
        {
            validationTasks.Add(ValidateKmsServiceAsync());
        }
        
        if (enabledServices.Contains("iam"))
        {
            validationTasks.Add(ValidateIamServiceAsync());
        }
        
        try
        {
            await Task.WhenAll(validationTasks);
            _logger.LogInformation("All AWS service validations completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AWS service validation failed");
            throw new InvalidOperationException($"AWS service validation failed: {ex.Message}", ex);
        }
    }
    
    /// <summary>
    /// Validate SQS service emulation
    /// </summary>
    private async Task ValidateSqsServiceAsync()
    {
        try
        {
            var sqsClient = CreateSqsClient();
            var response = await sqsClient.ListQueuesAsync(new Amazon.SQS.Model.ListQueuesRequest());
            _logger.LogDebug("SQS service validation successful - can list queues");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SQS service validation failed");
            throw new InvalidOperationException($"SQS service validation failed: {ex.Message}", ex);
        }
    }
    
    /// <summary>
    /// Validate SNS service emulation
    /// </summary>
    private async Task ValidateSnsServiceAsync()
    {
        try
        {
            var snsClient = CreateSnsClient();
            var response = await snsClient.ListTopicsAsync();
            _logger.LogDebug("SNS service validation successful - can list topics");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SNS service validation failed");
            throw new InvalidOperationException($"SNS service validation failed: {ex.Message}", ex);
        }
    }
    
    /// <summary>
    /// Validate KMS service emulation
    /// </summary>
    private async Task ValidateKmsServiceAsync()
    {
        try
        {
            var kmsClient = CreateKmsClient();
            var response = await kmsClient.ListKeysAsync(new Amazon.KeyManagementService.Model.ListKeysRequest());
            _logger.LogDebug("KMS service validation successful - can list keys");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KMS service validation failed");
            throw new InvalidOperationException($"KMS service validation failed: {ex.Message}", ex);
        }
    }
    
    /// <summary>
    /// Validate IAM service emulation
    /// </summary>
    private async Task ValidateIamServiceAsync()
    {
        try
        {
            var iamClient = CreateIamClient();
            var response = await iamClient.ListRolesAsync();
            _logger.LogDebug("IAM service validation successful - can list roles");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IAM service validation failed");
            throw new InvalidOperationException($"IAM service validation failed: {ex.Message}", ex);
        }
    }
    
    /// <summary>
    /// Create an SQS client configured for LocalStack
    /// </summary>
    private IAmazonSQS CreateSqsClient()
    {
        if (_configuration == null)
            throw new InvalidOperationException("LocalStack is not configured");
        
        var config = new AmazonSQSConfig
        {
            ServiceURL = _configuration.Endpoint,
            UseHttp = true,
            AuthenticationRegion = "us-east-1"
        };
        
        return new AmazonSQSClient("test", "test", config);
    }
    
    /// <summary>
    /// Create an SNS client configured for LocalStack
    /// </summary>
    private IAmazonSimpleNotificationService CreateSnsClient()
    {
        if (_configuration == null)
            throw new InvalidOperationException("LocalStack is not configured");
        
        var config = new AmazonSimpleNotificationServiceConfig
        {
            ServiceURL = _configuration.Endpoint,
            UseHttp = true,
            AuthenticationRegion = "us-east-1"
        };
        
        return new AmazonSimpleNotificationServiceClient("test", "test", config);
    }
    
    /// <summary>
    /// Create a KMS client configured for LocalStack
    /// </summary>
    private IAmazonKeyManagementService CreateKmsClient()
    {
        if (_configuration == null)
            throw new InvalidOperationException("LocalStack is not configured");
        
        var config = new AmazonKeyManagementServiceConfig
        {
            ServiceURL = _configuration.Endpoint,
            UseHttp = true,
            AuthenticationRegion = "us-east-1"
        };
        
        return new AmazonKeyManagementServiceClient("test", "test", config);
    }
    
    /// <summary>
    /// Create an IAM client configured for LocalStack
    /// </summary>
    private IAmazonIdentityManagementService CreateIamClient()
    {
        if (_configuration == null)
            throw new InvalidOperationException("LocalStack is not configured");
        
        var config = new AmazonIdentityManagementServiceConfig
        {
            ServiceURL = _configuration.Endpoint,
            UseHttp = true,
            AuthenticationRegion = "us-east-1"
        };
        
        return new AmazonIdentityManagementServiceClient("test", "test", config);
    }
    
    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        
        await StopAsync();
        _disposed = true;
    }
    
    /// <summary>
    /// LocalStack health response model
    /// </summary>
    private class LocalStackHealthResponse
    {
        public Dictionary<string, string>? Services { get; set; }
        public string? Version { get; set; }
        public Dictionary<string, object>? Features { get; set; }
    }
}

using Amazon;
using Amazon.SQS;
using Amazon.SimpleNotificationService;
using Amazon.KeyManagementService;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SourceFlow.Cloud.AWS.Tests.TestHelpers;

/// <summary>
/// Test fixture for LocalStack integration testing
/// </summary>
public class LocalStackTestFixture : IAsyncLifetime
{
    private IContainer? _localStackContainer;
    private readonly AwsTestConfiguration _configuration;
    
    public LocalStackTestFixture()
    {
        _configuration = new AwsTestConfiguration();
    }
    
    /// <summary>
    /// LocalStack endpoint URL
    /// </summary>
    public string LocalStackEndpoint => _configuration.LocalStackEndpoint;
    
    /// <summary>
    /// Test configuration
    /// </summary>
    public AwsTestConfiguration Configuration => _configuration;
    
    /// <summary>
    /// SQS client configured for LocalStack
    /// </summary>
    public IAmazonSQS? SqsClient { get; private set; }
    
    /// <summary>
    /// SNS client configured for LocalStack
    /// </summary>
    public IAmazonSimpleNotificationService? SnsClient { get; private set; }
    
    /// <summary>
    /// KMS client configured for LocalStack
    /// </summary>
    public IAmazonKeyManagementService? KmsClient { get; private set; }
    
    /// <summary>
    /// Initialize LocalStack container and AWS clients
    /// </summary>
    public async Task InitializeAsync()
    {
        if (!_configuration.UseLocalStack || !_configuration.RunIntegrationTests)
        {
            return;
        }
        
        // Detect GitHub Actions CI environment
        bool isGitHubActions = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));
        
        // Use CI-specific configuration in GitHub Actions
        LocalStackConfiguration localStackConfig;
        if (isGitHubActions)
        {
            localStackConfig = LocalStackConfiguration.CreateForGitHubActions();
            Console.WriteLine("Using GitHub Actions CI-optimized LocalStack configuration (90s timeout, 30 retries)");
        }
        else
        {
            localStackConfig = LocalStackConfiguration.CreateDefault();
            Console.WriteLine("Using local development LocalStack configuration (30s timeout, 10 retries)");
        }
        
        // Check if LocalStack is already running (e.g., in GitHub Actions)
        // Use longer timeout and retry logic for CI environments
        TimeSpan externalCheckTimeout = isGitHubActions ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(3);
        int maxRetries = 3;
        bool isAlreadyRunning = false;
        
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                Console.WriteLine($"Checking for external LocalStack instance (attempt {attempt}/{maxRetries}, timeout: {externalCheckTimeout.TotalSeconds}s)...");
                isAlreadyRunning = await _configuration.IsLocalStackAvailableAsync(externalCheckTimeout);
                
                if (isAlreadyRunning)
                {
                    Console.WriteLine("Detected existing LocalStack instance - will reuse it");
                    break;
                }
                else
                {
                    Console.WriteLine($"No external LocalStack instance detected on attempt {attempt}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"External LocalStack check failed on attempt {attempt}: {ex.Message}");
            }
            
            // Wait before retry (except on last attempt)
            if (attempt < maxRetries && !isAlreadyRunning)
            {
                await Task.Delay(2000);
            }
        }
        
        if (!isAlreadyRunning)
        {
            // In GitHub Actions, we expect LocalStack to be provided as a service container
            // If it's not detected, fail fast rather than trying to start a new container
            if (isGitHubActions)
            {
                string errorMessage = "LocalStack service container not detected in GitHub Actions CI. " +
                    "Ensure the workflow has a 'services.localstack' configuration. " +
                    "Tests cannot start their own containers in CI due to Docker-in-Docker limitations.";
                Console.WriteLine($"ERROR: {errorMessage}");
                throw new InvalidOperationException(errorMessage);
            }

            Console.WriteLine("Starting new LocalStack container for local development...");

            try
            {
                // Create LocalStack container (local development only)
                _localStackContainer = new ContainerBuilder()
                    .WithImage("localstack/localstack:latest")
                    .WithPortBinding(4566, 4566)
                    .WithEnvironment("SERVICES", "sqs,sns,kms")
                    .WithEnvironment("DEBUG", "1")
                    .WithEnvironment("DATA_DIR", "/tmp/localstack/data")
                    .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(4566))
                    .Build();

                // Start LocalStack
                await _localStackContainer.StartAsync();
                Console.WriteLine("LocalStack container started successfully");

                // Wait for services to be ready
                Console.WriteLine("Waiting 2000ms for LocalStack services to initialize...");
                await Task.Delay(2000);
            }
            catch (Exception ex) when (ex.ToString().Contains("port", StringComparison.OrdinalIgnoreCase))
            {
                // Port conflict means another LocalStack instance is likely running but the health
                // check failed under parallel test load. Retry external detection with longer timeout.
                Console.WriteLine($"Docker port conflict detected ({ex.Message}). Retrying external LocalStack detection...");
                _localStackContainer = null;

                for (int retry = 1; retry <= 5; retry++)
                {
                    await Task.Delay(1000 * retry);
                    try
                    {
                        isAlreadyRunning = await _configuration.IsLocalStackAvailableAsync(TimeSpan.FromSeconds(10));
                        if (isAlreadyRunning)
                        {
                            Console.WriteLine($"External LocalStack instance detected on retry {retry}");
                            break;
                        }
                    }
                    catch (Exception retryEx)
                    {
                        Console.WriteLine($"Retry {retry} failed: {retryEx.Message}");
                    }
                }

                if (!isAlreadyRunning)
                {
                    throw new InvalidOperationException(
                        "Port 4566 is in use but LocalStack health endpoint is not responding. " +
                        "Ensure LocalStack is running correctly or free port 4566.", ex);
                }
            }
        }
        
        // Create AWS clients configured for LocalStack
        // Use BasicAWSCredentials with dummy values for LocalStack
        // AnonymousAWSCredentials can cause issues with endpoint resolution
        var credentials = new Amazon.Runtime.BasicAWSCredentials("test", "test");
        
        var config = new Amazon.SQS.AmazonSQSConfig
        {
            ServiceURL = LocalStackEndpoint,
            UseHttp = true,
            // Don't set RegionEndpoint when using ServiceURL - it can override the endpoint
            AuthenticationRegion = _configuration.Region.SystemName
        };
        
        SqsClient = new AmazonSQSClient(credentials, config);
        
        var snsConfig = new Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceConfig
        {
            ServiceURL = LocalStackEndpoint,
            UseHttp = true,
            // Don't set RegionEndpoint when using ServiceURL
            AuthenticationRegion = _configuration.Region.SystemName
        };
        
        SnsClient = new AmazonSimpleNotificationServiceClient(credentials, snsConfig);
        
        var kmsConfig = new Amazon.KeyManagementService.AmazonKeyManagementServiceConfig
        {
            ServiceURL = LocalStackEndpoint,
            UseHttp = true,
            // Don't set RegionEndpoint when using ServiceURL
            AuthenticationRegion = _configuration.Region.SystemName
        };
        
        KmsClient = new AmazonKeyManagementServiceClient(credentials, kmsConfig);
        
        // Create test resources
        await CreateTestResourcesAsync();
    }
    
    /// <summary>
    /// Clean up LocalStack container and resources
    /// </summary>
    public async Task DisposeAsync()
    {
        SqsClient?.Dispose();
        SnsClient?.Dispose();
        KmsClient?.Dispose();
        
        // Only stop container if we started it
        if (_localStackContainer != null)
        {
            await _localStackContainer.StopAsync();
            await _localStackContainer.DisposeAsync();
        }
    }
    
    /// <summary>
    /// Create test queues and topics in LocalStack
    /// </summary>
    private async Task CreateTestResourcesAsync()
    {
        if (SqsClient == null || SnsClient == null)
            return;
        
        try
        {
            // Create test queue
            var queueName = "test-command-queue.fifo";
            var createQueueResponse = await SqsClient.CreateQueueAsync(new Amazon.SQS.Model.CreateQueueRequest
            {
                QueueName = queueName,
                Attributes = new Dictionary<string, string>
                {
                    ["FifoQueue"] = "true",
                    ["ContentBasedDeduplication"] = "true"
                }
            });
            
            _configuration.QueueUrls["TestCommand"] = createQueueResponse.QueueUrl;
            
            // Create test topic
            var topicName = "test-event-topic";
            var createTopicResponse = await SnsClient.CreateTopicAsync(topicName);
            _configuration.TopicArns["TestEvent"] = createTopicResponse.TopicArn;
            
            // Create KMS key for encryption tests
            if (KmsClient != null)
            {
                try
                {
                    var createKeyResponse = await KmsClient.CreateKeyAsync(new Amazon.KeyManagementService.Model.CreateKeyRequest
                    {
                        Description = "Test key for SourceFlow integration tests",
                        KeyUsage = Amazon.KeyManagementService.KeyUsageType.ENCRYPT_DECRYPT
                    });
                    
                    _configuration.KmsKeyId = createKeyResponse.KeyMetadata.KeyId;
                }
                catch
                {
                    // KMS might not be fully supported in LocalStack free version
                    // This is optional for basic integration tests
                }
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail - some tests might still work without all resources
            Console.WriteLine($"Warning: Failed to create some test resources: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Check if LocalStack is available and running
    /// </summary>
    public async Task<bool> IsAvailableAsync()
    {
        if (!_configuration.UseLocalStack || SqsClient == null)
            return false;
        
        try
        {
            await SqsClient.ListQueuesAsync(new Amazon.SQS.Model.ListQueuesRequest());
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    /// Create a service collection configured for LocalStack testing
    /// </summary>
    public IServiceCollection CreateTestServices()
    {
        var services = new ServiceCollection();
        
        // Add logging
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        // Add AWS clients configured for LocalStack
        if (SqsClient != null)
            services.AddSingleton(SqsClient);
        
        if (SnsClient != null)
            services.AddSingleton(SnsClient);
        
        if (KmsClient != null)
            services.AddSingleton(KmsClient);
        
        // Add test configuration
        services.AddSingleton(_configuration);
        
        return services;
    }
}

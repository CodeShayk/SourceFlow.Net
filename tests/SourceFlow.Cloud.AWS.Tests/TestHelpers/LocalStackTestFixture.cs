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
        
        // Create LocalStack container
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
        
        // Wait a bit for services to be ready
        await Task.Delay(2000);
        
        // Create AWS clients configured for LocalStack
        var config = new Amazon.SQS.AmazonSQSConfig
        {
            ServiceURL = LocalStackEndpoint,
            UseHttp = true,
            RegionEndpoint = _configuration.Region
        };
        
        SqsClient = new AmazonSQSClient(_configuration.AccessKey, _configuration.SecretKey, config);
        
        var snsConfig = new Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceConfig
        {
            ServiceURL = LocalStackEndpoint,
            UseHttp = true,
            RegionEndpoint = _configuration.Region
        };
        
        SnsClient = new AmazonSimpleNotificationServiceClient(_configuration.AccessKey, _configuration.SecretKey, snsConfig);
        
        var kmsConfig = new Amazon.KeyManagementService.AmazonKeyManagementServiceConfig
        {
            ServiceURL = LocalStackEndpoint,
            UseHttp = true,
            RegionEndpoint = _configuration.Region
        };
        
        KmsClient = new AmazonKeyManagementServiceClient(_configuration.AccessKey, _configuration.SecretKey, kmsConfig);
        
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

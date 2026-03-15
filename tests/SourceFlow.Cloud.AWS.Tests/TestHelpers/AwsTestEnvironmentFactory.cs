using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SourceFlow.Cloud.AWS.Tests.TestHelpers;

/// <summary>
/// Factory for creating configured AWS test environments
/// Provides convenient methods for setting up test environments with different configurations
/// </summary>
public static class AwsTestEnvironmentFactory
{
    /// <summary>
    /// Create a default AWS test environment using LocalStack
    /// </summary>
    /// <param name="testPrefix">Unique prefix for test resources</param>
    /// <returns>Configured AWS test environment</returns>
    public static async Task<IAwsTestEnvironment> CreateLocalStackEnvironmentAsync(string? testPrefix = null)
    {
        var configuration = new AwsTestConfiguration
        {
            UseLocalStack = true,
            RunIntegrationTests = true,
            RunPerformanceTests = false,
            RunSecurityTests = true,
            LocalStack = LocalStackConfiguration.CreateDefault()
        };
        
        return await CreateEnvironmentAsync(configuration, testPrefix);
    }
    
    /// <summary>
    /// Create an AWS test environment for performance testing
    /// </summary>
    /// <param name="testPrefix">Unique prefix for test resources</param>
    /// <returns>Configured AWS test environment optimized for performance testing</returns>
    public static async Task<IAwsTestEnvironment> CreatePerformanceTestEnvironmentAsync(string? testPrefix = null)
    {
        var configuration = new AwsTestConfiguration
        {
            UseLocalStack = true,
            RunIntegrationTests = true,
            RunPerformanceTests = true,
            RunSecurityTests = false,
            LocalStack = LocalStackConfiguration.CreateForPerformanceTesting(),
            Performance = new PerformanceTestConfiguration
            {
                DefaultConcurrentSenders = 20,
                DefaultMessagesPerSender = 500,
                DefaultMessageSize = 2048,
                TestTimeout = TimeSpan.FromMinutes(10)
            }
        };
        
        return await CreateEnvironmentAsync(configuration, testPrefix);
    }
    
    /// <summary>
    /// Create an AWS test environment for security testing
    /// </summary>
    /// <param name="testPrefix">Unique prefix for test resources</param>
    /// <returns>Configured AWS test environment optimized for security testing</returns>
    public static async Task<IAwsTestEnvironment> CreateSecurityTestEnvironmentAsync(string? testPrefix = null)
    {
        var configuration = new AwsTestConfiguration
        {
            UseLocalStack = true,
            RunIntegrationTests = true,
            RunPerformanceTests = false,
            RunSecurityTests = true,
            LocalStack = LocalStackConfiguration.CreateForSecurityTesting(),
            Security = new SecurityTestConfiguration
            {
                TestEncryptionInTransit = true,
                TestIamPermissions = true,
                TestSensitiveDataMasking = true
            }
        };
        
        return await CreateEnvironmentAsync(configuration, testPrefix);
    }
    
    /// <summary>
    /// Create an AWS test environment using real AWS services
    /// </summary>
    /// <param name="testPrefix">Unique prefix for test resources</param>
    /// <returns>Configured AWS test environment using real AWS services</returns>
    public static async Task<IAwsTestEnvironment> CreateRealAwsEnvironmentAsync(string? testPrefix = null)
    {
        var configuration = new AwsTestConfiguration
        {
            UseLocalStack = false,
            RunIntegrationTests = true,
            RunPerformanceTests = true,
            RunSecurityTests = true
        };
        
        return await CreateEnvironmentAsync(configuration, testPrefix);
    }
    
    /// <summary>
    /// Create an AWS test environment with custom configuration
    /// </summary>
    /// <param name="configuration">Custom AWS test configuration</param>
    /// <param name="testPrefix">Unique prefix for test resources</param>
    /// <returns>Configured AWS test environment</returns>
    public static async Task<IAwsTestEnvironment> CreateEnvironmentAsync(AwsTestConfiguration configuration, string? testPrefix = null)
    {
        var actualTestPrefix = testPrefix ?? $"test-{Guid.NewGuid():N}";
        
        // Create service collection
        var services = new ServiceCollection();
        
        // Add logging
        services.AddLogging(builder => 
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });
        
        // Add configuration
        services.AddSingleton(configuration);
        
        // Add LocalStack manager if using LocalStack
        ILocalStackManager? localStackManager = null;
        if (configuration.UseLocalStack)
        {
            services.AddSingleton<ILocalStackManager, LocalStackManager>();
            var serviceProvider = services.BuildServiceProvider();
            localStackManager = serviceProvider.GetRequiredService<ILocalStackManager>();
            
            // Start LocalStack
            await localStackManager.StartAsync(configuration.LocalStack);
        }
        
        // Build service provider (for logging only - AwsResourceManager is created after AwsTestEnvironment
        // to break the circular dependency: AwsTestEnvironment → AwsResourceManager → IAwsTestEnvironment)
        var finalServiceProvider = services.BuildServiceProvider();

        var logger = finalServiceProvider.GetRequiredService<ILogger<AwsTestEnvironment>>();
        var resourceManagerLogger = finalServiceProvider.GetRequiredService<ILogger<AwsResourceManager>>();

        // Phase 1: create environment without resource manager, initialize AWS clients
        var testEnvironment = new AwsTestEnvironment(configuration, localStackManager, null, logger);
        await testEnvironment.InitializeAsync();

        // Phase 2: create resource manager (environment now has AWS clients), wire back
        var resourceManager = new AwsResourceManager(testEnvironment, resourceManagerLogger);
        testEnvironment.SetResourceManager(resourceManager);
        
        return testEnvironment;
    }
    
    /// <summary>
    /// Create a service collection configured for AWS testing
    /// </summary>
    /// <param name="testEnvironment">AWS test environment</param>
    /// <returns>Service collection with AWS test services</returns>
    public static IServiceCollection CreateTestServiceCollection(IAwsTestEnvironment testEnvironment)
    {
        var services = testEnvironment.CreateTestServices();
        
        // Add the test environment itself
        services.AddSingleton(testEnvironment);
        
        // Add test utilities
        services.AddTransient<AwsTestScenarioRunner>();
        services.AddTransient<AwsPerformanceTestRunner>();
        services.AddTransient<AwsSecurityTestRunner>();
        
        return services;
    }
    
    /// <summary>
    /// Create a test environment builder for fluent configuration
    /// </summary>
    /// <returns>AWS test environment builder</returns>
    public static AwsTestEnvironmentBuilder CreateBuilder()
    {
        return new AwsTestEnvironmentBuilder();
    }
}

/// <summary>
/// Builder for creating AWS test environments with fluent configuration
/// </summary>
public class AwsTestEnvironmentBuilder
{
    private readonly AwsTestConfiguration _configuration;
    private string? _testPrefix;
    
    public AwsTestEnvironmentBuilder()
    {
        _configuration = new AwsTestConfiguration();
    }
    
    /// <summary>
    /// Use LocalStack for AWS service emulation
    /// </summary>
    public AwsTestEnvironmentBuilder UseLocalStack(bool useLocalStack = true)
    {
        _configuration.UseLocalStack = useLocalStack;
        return this;
    }
    
    /// <summary>
    /// Configure LocalStack settings
    /// </summary>
    public AwsTestEnvironmentBuilder ConfigureLocalStack(Action<LocalStackConfiguration> configure)
    {
        configure(_configuration.LocalStack);
        return this;
    }
    
    /// <summary>
    /// Enable integration tests
    /// </summary>
    public AwsTestEnvironmentBuilder EnableIntegrationTests(bool enable = true)
    {
        _configuration.RunIntegrationTests = enable;
        return this;
    }
    
    /// <summary>
    /// Enable performance tests
    /// </summary>
    public AwsTestEnvironmentBuilder EnablePerformanceTests(bool enable = true)
    {
        _configuration.RunPerformanceTests = enable;
        return this;
    }
    
    /// <summary>
    /// Enable security tests
    /// </summary>
    public AwsTestEnvironmentBuilder EnableSecurityTests(bool enable = true)
    {
        _configuration.RunSecurityTests = enable;
        return this;
    }
    
    /// <summary>
    /// Configure AWS services
    /// </summary>
    public AwsTestEnvironmentBuilder ConfigureServices(Action<AwsServiceConfiguration> configure)
    {
        configure(_configuration.Services);
        return this;
    }
    
    /// <summary>
    /// Configure performance testing
    /// </summary>
    public AwsTestEnvironmentBuilder ConfigurePerformance(Action<PerformanceTestConfiguration> configure)
    {
        configure(_configuration.Performance);
        return this;
    }
    
    /// <summary>
    /// Configure security testing
    /// </summary>
    public AwsTestEnvironmentBuilder ConfigureSecurity(Action<SecurityTestConfiguration> configure)
    {
        configure(_configuration.Security);
        return this;
    }
    
    /// <summary>
    /// Set test prefix for resource naming
    /// </summary>
    public AwsTestEnvironmentBuilder WithTestPrefix(string testPrefix)
    {
        _testPrefix = testPrefix;
        return this;
    }
    
    /// <summary>
    /// Build the AWS test environment
    /// </summary>
    public async Task<IAwsTestEnvironment> BuildAsync()
    {
        return await AwsTestEnvironmentFactory.CreateEnvironmentAsync(_configuration, _testPrefix);
    }
}

/// <summary>
/// Test scenario runner for AWS integration tests
/// </summary>
public class AwsTestScenarioRunner
{
    private readonly IAwsTestEnvironment _testEnvironment;
    private readonly ILogger<AwsTestScenarioRunner> _logger;
    
    public AwsTestScenarioRunner(IAwsTestEnvironment testEnvironment, ILogger<AwsTestScenarioRunner> logger)
    {
        _testEnvironment = testEnvironment ?? throw new ArgumentNullException(nameof(testEnvironment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    
    /// <summary>
    /// Run a basic SQS integration test scenario
    /// </summary>
    public async Task<bool> RunSqsBasicScenarioAsync()
    {
        try
        {
            _logger.LogInformation("Running basic SQS integration test scenario");
            
            // Create test queue
            var queueUrl = await _testEnvironment.CreateStandardQueueAsync("basic-test-queue");
            
            // Send test message
            await _testEnvironment.SqsClient.SendMessageAsync(new Amazon.SQS.Model.SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = "Test message from SourceFlow AWS integration test"
            });
            
            // Receive test message
            var response = await _testEnvironment.SqsClient.ReceiveMessageAsync(new Amazon.SQS.Model.ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 5
            });
            
            var success = response.Messages.Count > 0;
            
            // Cleanup
            await _testEnvironment.DeleteQueueAsync(queueUrl);
            
            _logger.LogInformation("Basic SQS scenario completed: {Success}", success);
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Basic SQS scenario failed");
            return false;
        }
    }
    
    /// <summary>
    /// Run a basic SNS integration test scenario
    /// </summary>
    public async Task<bool> RunSnsBasicScenarioAsync()
    {
        try
        {
            _logger.LogInformation("Running basic SNS integration test scenario");
            
            // Create test topic
            var topicArn = await _testEnvironment.CreateTopicAsync("basic-test-topic");
            
            // Publish test message
            await _testEnvironment.SnsClient.PublishAsync(new Amazon.SimpleNotificationService.Model.PublishRequest
            {
                TopicArn = topicArn,
                Message = "Test message from SourceFlow AWS integration test"
            });
            
            // Cleanup
            await _testEnvironment.DeleteTopicAsync(topicArn);
            
            _logger.LogInformation("Basic SNS scenario completed successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Basic SNS scenario failed");
            return false;
        }
    }
}

/// <summary>
/// Performance test runner for AWS services
/// </summary>
public class AwsPerformanceTestRunner
{
    private readonly IAwsTestEnvironment _testEnvironment;
    private readonly ILogger<AwsPerformanceTestRunner> _logger;
    
    public AwsPerformanceTestRunner(IAwsTestEnvironment testEnvironment, ILogger<AwsPerformanceTestRunner> logger)
    {
        _testEnvironment = testEnvironment ?? throw new ArgumentNullException(nameof(testEnvironment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    
    /// <summary>
    /// Run SQS throughput performance test
    /// </summary>
    public async Task<PerformanceTestResult> RunSqsThroughputTestAsync(int messageCount = 100, int messageSize = 1024)
    {
        var queueUrl = await _testEnvironment.CreateStandardQueueAsync("perf-test-queue");
        
        try
        {
            var message = new string('x', messageSize);
            
            var result = await PerformanceTestHelpers.RunPerformanceTestAsync(
                "SQS Throughput Test",
                async () =>
                {
                    await _testEnvironment.SqsClient.SendMessageAsync(new Amazon.SQS.Model.SendMessageRequest
                    {
                        QueueUrl = queueUrl,
                        MessageBody = message
                    });
                },
                iterations: messageCount,
                warmupIterations: 10);
            
            return result;
        }
        finally
        {
            await _testEnvironment.DeleteQueueAsync(queueUrl);
        }
    }
}

/// <summary>
/// Security test runner for AWS services
/// </summary>
public class AwsSecurityTestRunner
{
    private readonly IAwsTestEnvironment _testEnvironment;
    private readonly ILogger<AwsSecurityTestRunner> _logger;
    
    public AwsSecurityTestRunner(IAwsTestEnvironment testEnvironment, ILogger<AwsSecurityTestRunner> logger)
    {
        _testEnvironment = testEnvironment ?? throw new ArgumentNullException(nameof(testEnvironment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    
    /// <summary>
    /// Run basic IAM permission validation test
    /// </summary>
    public async Task<bool> RunIamPermissionTestAsync()
    {
        try
        {
            // Test basic SQS permissions
            var hasPermission = await _testEnvironment.ValidateIamPermissionsAsync("sqs:CreateQueue", "*");
            return hasPermission;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IAM permission test failed");
            return false;
        }
    }
}

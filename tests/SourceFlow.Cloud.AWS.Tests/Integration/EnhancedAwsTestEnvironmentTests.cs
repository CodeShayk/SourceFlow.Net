using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.AWS.Tests.Integration;

/// <summary>
/// Integration tests for the enhanced AWS test environment abstractions
/// Validates that the new IAwsTestEnvironment, ILocalStackManager, and IAwsResourceManager work correctly
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "RequiresLocalStack")]
public class EnhancedAwsTestEnvironmentTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IAwsTestEnvironment? _testEnvironment;
    
    public EnhancedAwsTestEnvironmentTests(ITestOutputHelper output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }
    
    public async Task InitializeAsync()
    {
        _output.WriteLine("Initializing enhanced AWS test environment...");
        
        // Create test environment using the factory
        _testEnvironment = await AwsTestEnvironmentFactory.CreateLocalStackEnvironmentAsync("enhanced-test");
        
        _output.WriteLine($"Test environment initialized (LocalStack: {_testEnvironment.IsLocalEmulator})");
    }
    
    public async Task DisposeAsync()
    {
        if (_testEnvironment != null)
        {
            _output.WriteLine("Disposing test environment...");
            await _testEnvironment.DisposeAsync();
        }
    }
    
    [Fact]
    public async Task TestEnvironment_ShouldBeAvailable()
    {
        // Arrange & Act
        var isAvailable = await _testEnvironment!.IsAvailableAsync();
        
        // Assert
        Assert.True(isAvailable, "Test environment should be available");
        _output.WriteLine("✓ Test environment is available");
    }
    
    [Fact]
    public async Task TestEnvironment_ShouldProvideAwsClients()
    {
        // Arrange & Act & Assert
        Assert.NotNull(_testEnvironment!.SqsClient);
        Assert.NotNull(_testEnvironment.SnsClient);
        Assert.NotNull(_testEnvironment.KmsClient);
        Assert.NotNull(_testEnvironment.IamClient);
        
        _output.WriteLine("✓ All AWS clients are available");
    }
    
    [Fact]
    public async Task CreateFifoQueue_ShouldCreateQueueSuccessfully()
    {
        // Arrange
        var queueName = "test-fifo-queue";
        
        // Act
        var queueUrl = await _testEnvironment!.CreateFifoQueueAsync(queueName);
        
        // Assert
        Assert.NotNull(queueUrl);
        Assert.NotEmpty(queueUrl);
        Assert.Contains(".fifo", queueUrl);
        
        _output.WriteLine($"✓ Created FIFO queue: {queueUrl}");
        
        // Cleanup
        await _testEnvironment.DeleteQueueAsync(queueUrl);
        _output.WriteLine("✓ Cleaned up FIFO queue");
    }
    
    [Fact]
    public async Task CreateStandardQueue_ShouldCreateQueueSuccessfully()
    {
        // Arrange
        var queueName = "test-standard-queue";
        
        // Act
        var queueUrl = await _testEnvironment!.CreateStandardQueueAsync(queueName);
        
        // Assert
        Assert.NotNull(queueUrl);
        Assert.NotEmpty(queueUrl);
        Assert.DoesNotContain(".fifo", queueUrl);
        
        _output.WriteLine($"✓ Created standard queue: {queueUrl}");
        
        // Cleanup
        await _testEnvironment.DeleteQueueAsync(queueUrl);
        _output.WriteLine("✓ Cleaned up standard queue");
    }
    
    [Fact]
    public async Task CreateTopic_ShouldCreateTopicSuccessfully()
    {
        // Arrange
        var topicName = "test-topic";
        
        // Act
        var topicArn = await _testEnvironment!.CreateTopicAsync(topicName);
        
        // Assert
        Assert.NotNull(topicArn);
        Assert.NotEmpty(topicArn);
        Assert.Contains(topicName, topicArn);
        
        _output.WriteLine($"✓ Created SNS topic: {topicArn}");
        
        // Cleanup
        await _testEnvironment.DeleteTopicAsync(topicArn);
        _output.WriteLine("✓ Cleaned up SNS topic");
    }
    
    [Fact]
    public async Task GetHealthStatus_ShouldReturnHealthForAllServices()
    {
        // Act
        var healthStatus = await _testEnvironment!.GetHealthStatusAsync();
        
        // Assert
        Assert.NotNull(healthStatus);
        Assert.True(healthStatus.Count > 0, "Should have health status for at least one service");
        
        foreach (var service in healthStatus)
        {
            _output.WriteLine($"Service: {service.Key}, Available: {service.Value.IsAvailable}, Response Time: {service.Value.ResponseTime.TotalMilliseconds}ms");
        }
        
        // At least SQS should be available
        Assert.True(healthStatus.ContainsKey("sqs"), "Should have SQS health status");
        _output.WriteLine("✓ Health status retrieved for all services");
    }
    
    [Fact]
    public async Task CreateTestServices_ShouldReturnConfiguredServiceCollection()
    {
        // Act
        var services = _testEnvironment!.CreateTestServices();
        
        // Assert
        Assert.NotNull(services);
        
        // Build service provider to verify services are registered
        var serviceProvider = services.BuildServiceProvider();
        
        // Verify AWS clients are registered
        var sqsClient = serviceProvider.GetService<Amazon.SQS.IAmazonSQS>();
        var snsClient = serviceProvider.GetService<Amazon.SimpleNotificationService.IAmazonSimpleNotificationService>();
        
        Assert.NotNull(sqsClient);
        Assert.NotNull(snsClient);
        
        _output.WriteLine("✓ Test services collection created and configured correctly");
    }
    
    [Fact]
    public async Task TestScenarioRunner_ShouldRunBasicSqsScenario()
    {
        // Arrange
        var services = AwsTestEnvironmentFactory.CreateTestServiceCollection(_testEnvironment!);
        var serviceProvider = services.BuildServiceProvider();
        var scenarioRunner = serviceProvider.GetRequiredService<AwsTestScenarioRunner>();
        
        // Act
        var result = await scenarioRunner.RunSqsBasicScenarioAsync();
        
        // Assert
        Assert.True(result, "Basic SQS scenario should succeed");
        _output.WriteLine("✓ Basic SQS scenario completed successfully");
    }
    
    [Fact]
    public async Task TestScenarioRunner_ShouldRunBasicSnsScenario()
    {
        // Arrange
        var services = AwsTestEnvironmentFactory.CreateTestServiceCollection(_testEnvironment!);
        var serviceProvider = services.BuildServiceProvider();
        var scenarioRunner = serviceProvider.GetRequiredService<AwsTestScenarioRunner>();
        
        // Act
        var result = await scenarioRunner.RunSnsBasicScenarioAsync();
        
        // Assert
        Assert.True(result, "Basic SNS scenario should succeed");
        _output.WriteLine("✓ Basic SNS scenario completed successfully");
    }
    
    [Fact]
    public async Task PerformanceTestRunner_ShouldMeasureSqsThroughput()
    {
        // Arrange
        var services = AwsTestEnvironmentFactory.CreateTestServiceCollection(_testEnvironment!);
        var serviceProvider = services.BuildServiceProvider();
        var performanceRunner = serviceProvider.GetRequiredService<AwsPerformanceTestRunner>();
        
        // Act
        var result = await performanceRunner.RunSqsThroughputTestAsync(messageCount: 10, messageSize: 512);
        
        // Assert
        Assert.NotNull(result);
        Assert.True(result.TotalDuration > TimeSpan.Zero, "Test should take some time");
        Assert.True(result.OperationsPerSecond > 0, "Should have positive throughput");
        Assert.Equal(10, result.Iterations);
        
        _output.WriteLine($"✓ SQS throughput test: {result.OperationsPerSecond:F2} ops/sec, Duration: {result.TotalDuration.TotalMilliseconds}ms");
    }
    
    [Fact]
    public async Task TestEnvironmentBuilder_ShouldCreateCustomEnvironment()
    {
        // Arrange & Act
        var customEnvironment = await AwsTestEnvironmentFactory.CreateBuilder()
            .UseLocalStack(true)
            .EnableIntegrationTests(true)
            .EnablePerformanceTests(false)
            .ConfigureLocalStack(config =>
            {
                config.Debug = true;
                config.EnabledServices = new List<string> { "sqs", "sns" };
            })
            .WithTestPrefix("custom-test")
            .BuildAsync();
        
        try
        {
            // Assert
            Assert.NotNull(customEnvironment);
            Assert.True(customEnvironment.IsLocalEmulator);
            
            var isAvailable = await customEnvironment.IsAvailableAsync();
            Assert.True(isAvailable);
            
            _output.WriteLine("✓ Custom test environment created successfully using builder pattern");
        }
        finally
        {
            await customEnvironment.DisposeAsync();
        }
    }
}

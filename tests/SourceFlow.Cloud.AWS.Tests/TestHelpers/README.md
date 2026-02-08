# Enhanced AWS Test Environment Abstractions

This directory contains the enhanced AWS test environment abstractions that provide comprehensive testing capabilities for SourceFlow's AWS cloud integrations.

## Core Interfaces

### ICloudTestEnvironment
Base interface for cloud test environments providing common functionality:
- Environment availability checking
- Service collection creation
- Resource cleanup

### IAwsTestEnvironment
Enhanced AWS-specific test environment interface extending `ICloudTestEnvironment`:
- Full AWS service client access (SQS, SNS, KMS, IAM)
- FIFO and standard queue creation
- SNS topic management
- KMS key creation and management
- IAM permission validation
- Health status monitoring

### ILocalStackManager
Container lifecycle management for LocalStack AWS service emulation:
- Container startup and shutdown
- Service availability checking
- Health monitoring
- Data reset capabilities
- Log retrieval

### IAwsResourceManager
Automated AWS resource provisioning and cleanup:
- Test resource creation with unique naming
- Resource tracking and cleanup
- Cost estimation
- CloudFormation stack management
- Resource tagging

## Implementations

### AwsTestEnvironment
Main implementation of `IAwsTestEnvironment` that:
- Supports both LocalStack and real AWS environments
- Provides comprehensive AWS service clients
- Implements resource creation and management
- Includes health checking and validation

### LocalStackManager
TestContainers-based LocalStack container management:
- Configurable service enablement
- Health checking with retry logic
- Container lifecycle management
- Service endpoint resolution

### AwsResourceManager
Comprehensive resource management implementation:
- Automatic resource provisioning
- Cleanup with error handling
- Resource existence validation
- Cost estimation capabilities

## Configuration

### AwsTestConfiguration
Enhanced configuration supporting:
- LocalStack vs real AWS selection
- Service-specific configurations (SQS, SNS, KMS, IAM)
- Performance test settings
- Security test settings

### LocalStackConfiguration
Detailed LocalStack container configuration:
- Service selection
- Environment variables
- Port bindings
- Volume mounts
- Health check settings

## Factory and Builder Pattern

### AwsTestEnvironmentFactory
Convenient factory methods for creating test environments:
- `CreateLocalStackEnvironmentAsync()` - Default LocalStack setup
- `CreatePerformanceTestEnvironmentAsync()` - Optimized for performance testing
- `CreateSecurityTestEnvironmentAsync()` - Configured for security testing
- `CreateRealAwsEnvironmentAsync()` - Real AWS services

### AwsTestEnvironmentBuilder
Fluent builder pattern for custom configurations:
```csharp
var environment = await AwsTestEnvironmentFactory.CreateBuilder()
    .UseLocalStack(true)
    .EnableIntegrationTests(true)
    .ConfigureLocalStack(config => config.Debug = true)
    .WithTestPrefix("my-test")
    .BuildAsync();
```

## Test Runners

### AwsTestScenarioRunner
Basic integration test scenarios:
- SQS message send/receive validation
- SNS topic publish validation

### AwsPerformanceTestRunner
Performance testing capabilities:
- SQS throughput measurement
- Latency analysis
- Resource utilization tracking

### AwsSecurityTestRunner
Security validation:
- IAM permission testing
- Encryption validation
- Access control verification

## Usage Examples

### Basic LocalStack Testing
```csharp
var testEnvironment = await AwsTestEnvironmentFactory.CreateLocalStackEnvironmentAsync();

// Create resources
var queueUrl = await testEnvironment.CreateFifoQueueAsync("test-queue");
var topicArn = await testEnvironment.CreateTopicAsync("test-topic");

// Use AWS clients
await testEnvironment.SqsClient.SendMessageAsync(new SendMessageRequest
{
    QueueUrl = queueUrl,
    MessageBody = "Test message"
});

// Cleanup
await testEnvironment.DisposeAsync();
```

### Performance Testing
```csharp
var testEnvironment = await AwsTestEnvironmentFactory.CreatePerformanceTestEnvironmentAsync();
var services = AwsTestEnvironmentFactory.CreateTestServiceCollection(testEnvironment);
var serviceProvider = services.BuildServiceProvider();
var performanceRunner = serviceProvider.GetRequiredService<AwsPerformanceTestRunner>();

var result = await performanceRunner.RunSqsThroughputTestAsync(messageCount: 1000);
Console.WriteLine($"Throughput: {result.OperationsPerSecond:F2} ops/sec");
```

### Custom Configuration
```csharp
var testEnvironment = await AwsTestEnvironmentFactory.CreateBuilder()
    .UseLocalStack(true)
    .ConfigureLocalStack(config =>
    {
        config.EnabledServices = new List<string> { "sqs", "sns", "kms" };
        config.Debug = true;
    })
    .ConfigureServices(services =>
    {
        services.Sqs.EnableDeadLetterQueue = true;
        services.Sqs.MaxReceiveCount = 5;
    })
    .EnablePerformanceTests(true)
    .WithTestPrefix("custom-test")
    .BuildAsync();
```

## Integration with Existing Tests

The enhanced abstractions are designed to work alongside existing test infrastructure:
- Compatible with existing `LocalStackTestFixture`
- Extends existing `AwsTestConfiguration`
- Uses existing `PerformanceTestResult` model
- Integrates with xUnit test framework

## Key Features

1. **Comprehensive AWS Service Support**: Full support for SQS, SNS, KMS, and IAM services
2. **LocalStack Integration**: Seamless LocalStack container management with TestContainers
3. **Resource Management**: Automated provisioning, tracking, and cleanup of test resources
4. **Performance Testing**: Built-in performance measurement and benchmarking capabilities
5. **Security Testing**: IAM permission validation and encryption testing
6. **Flexible Configuration**: Support for both LocalStack and real AWS environments
7. **Factory Pattern**: Convenient creation methods for common test scenarios
8. **Builder Pattern**: Fluent configuration for custom test environments
9. **Health Monitoring**: Comprehensive health checking for all AWS services
10. **Error Handling**: Robust error handling with cleanup guarantees

## Requirements Satisfied

This implementation satisfies the following requirements from the AWS Cloud Integration Testing specification:
- **6.1, 6.2, 6.3**: LocalStack integration with full AWS service emulation
- **9.1, 9.2**: CI/CD integration with automated resource provisioning
- **All service requirements**: Comprehensive support for SQS, SNS, KMS, and IAM testing

The abstractions provide a solid foundation for implementing comprehensive AWS integration tests while maintaining clean separation of concerns and supporting both local development and CI/CD scenarios.
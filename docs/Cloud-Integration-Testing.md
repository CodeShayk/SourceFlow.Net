# SourceFlow.Net Cloud Integration Testing

This document provides an overview of the comprehensive testing framework for SourceFlow's AWS cloud integration, covering property-based testing, performance validation, security testing, and resilience patterns.

## Overview

SourceFlow.Net includes a sophisticated testing framework that validates AWS cloud integration across multiple dimensions:

- **Functional Correctness** - Property-based testing ensures universal correctness properties with 16 comprehensive properties
- **Performance Validation** - Comprehensive benchmarking of cloud service performance with BenchmarkDotNet
- **Security Testing** - Validation of encryption, authentication, and access control with IAM and KMS
- **Resilience Testing** - Circuit breakers, retry policies, and failure handling with comprehensive fault injection
- **Local Development** - Emulator-based testing for rapid development cycles with LocalStack
- **CI/CD Integration** - Automated testing with resource provisioning and cleanup for continuous validation

## Implementation Status

### 🎉 AWS Cloud Integration Testing (Complete)
All phases of the AWS cloud integration testing framework have been successfully implemented:

- ✅ **Phase 1-3**: Enhanced test infrastructure with LocalStack, resource management, and test environment abstractions
- ✅ **Phase 4-5**: Comprehensive SQS and SNS integration tests with property-based validation
- ✅ **Phase 6**: KMS encryption integration tests with round-trip, key rotation, and security validation
- ✅ **Phase 7**: AWS health check integration tests for SQS, SNS, and KMS services
- ✅ **Phase 9**: AWS performance testing with benchmarks for throughput, latency, and scalability
- ✅ **Phase 10**: AWS resilience testing with circuit breakers, retry policies, and failure handling
- ✅ **Phase 11**: AWS security testing with IAM, encryption in transit, and audit logging validation
- ✅ **Phase 12-15**: CI/CD integration, comprehensive documentation, and final validation

**Key Achievements:**
- 16 property-based tests validating universal correctness properties
- 100+ integration tests covering all AWS services (SQS, SNS, KMS)
- Comprehensive performance benchmarks with BenchmarkDotNet
- Full security validation including IAM, KMS, and audit logging
- Complete CI/CD integration with automated resource provisioning
- Extensive documentation for setup, execution, and troubleshooting
- Enhanced wildcard permission validation logic
- Supports scenarios with zero wildcards or controlled wildcard usage
- Validates least privilege principles with realistic constraints

## Testing Architecture

### Test Project Structure

```
tests/
├── SourceFlow.Core.Tests/                # Core framework tests
│   ├── Unit/                             # Unit tests (Category=Unit)
│   └── Integration/                      # Integration tests
├── SourceFlow.Stores.EntityFramework.Tests/ # EF persistence tests
│   ├── Unit/                             # Unit tests (Category=Unit)
│   └── E2E/                             # Integration tests (Category=Integration)
├── SourceFlow.Cloud.AWS.Tests/           # AWS-specific testing
│   ├── Unit/                             # Unit tests with mocks
│   ├── Integration/                      # LocalStack integration tests
│   ├── Performance/                      # BenchmarkDotNet performance tests
│   ├── Security/                         # IAM and KMS security tests
│   ├── Resilience/                       # Circuit breaker and retry tests
│   └── E2E/                             # End-to-end scenario tests
```

### Test Categorization

All test projects use xUnit `[Trait("Category", "...")]` attributes for filtering:

- **`Category=Unit`** - Fast, isolated unit tests with no external dependencies
- **`Category=Integration`** - Integration tests requiring databases or external services
- **`Category=RequiresLocalStack`** - AWS integration tests requiring LocalStack container

**Test Filtering Examples:**
```bash
# Run only unit tests (fast feedback)
dotnet test --filter "Category=Unit"

# Run integration tests
dotnet test --filter "Category=Integration"

# Run AWS integration tests with LocalStack
dotnet test --filter "Category=Integration&Category=RequiresLocalStack"

# Run all tests except LocalStack tests
dotnet test --filter "Category!=RequiresLocalStack"
```

## Testing Frameworks and Tools

### Property-Based Testing
- **FsCheck** - Generates randomized test data to validate universal properties
- **100+ iterations** per property test for comprehensive coverage
- **Custom generators** for cloud service configurations
- **Automatic shrinking** to find minimal failing examples

### Performance Testing
- **BenchmarkDotNet** - Precise micro-benchmarking with statistical analysis
- **Memory diagnostics** - Allocation tracking and GC pressure analysis
- **Throughput measurement** - Messages per second across cloud services
- **Latency analysis** - End-to-end processing times with percentile reporting

### Integration Testing
- **LocalStack** - AWS service emulation for local development
- **TestContainers** - Automated container lifecycle management
- **Real cloud services** - Validation against actual AWS services

## Key Testing Scenarios

### AWS Cloud Integration Testing

#### SQS Command Dispatching
- **FIFO Queue Testing** - Message ordering and deduplication
- **Standard Queue Testing** - High-throughput message delivery
- **Dead Letter Queue Testing** - Failed message handling and recovery
- **Batch Operations** - Efficient bulk message processing
- **Message Attributes** - Metadata preservation and routing

#### SNS Event Publishing
- **Topic Publishing** - Event distribution to multiple subscribers
- **Fan-out Messaging** - Delivery to SQS, Lambda, and HTTP endpoints
- **Message Filtering** - Subscription-based selective delivery
- **Correlation Tracking** - End-to-end message correlation
- **Error Handling** - Failed delivery retry mechanisms

#### KMS Encryption
- **Round-trip Encryption** - Message encryption and decryption validation
- **Key Rotation** - Seamless key rotation without service interruption
- **Sensitive Data Masking** - Automatic masking of sensitive properties
- **Performance Impact** - Encryption overhead measurement

## Property-Based Testing Properties

The testing framework validates these universal correctness properties:

### AWS Properties (16 Implemented)
1. ✅ **SQS Message Processing Correctness** - Commands delivered with proper attributes and ordering
2. ✅ **SQS Dead Letter Queue Handling** - Failed messages captured with complete metadata
3. ✅ **SNS Event Publishing Correctness** - Events delivered to all subscribers with fan-out
4. ✅ **SNS Message Filtering and Error Handling** - Subscription filters and error handling work correctly
5. ✅ **KMS Encryption Round-Trip Consistency** - Encryption/decryption preserves message integrity
   - Property test validates: decrypt(encrypt(plaintext)) == plaintext for all inputs
   - Ensures encryption non-determinism (different ciphertext for same plaintext)
   - Verifies sensitive data protection (plaintext not visible in ciphertext)
   - Validates performance characteristics (encryption/decryption within bounds)
   - Tests Unicode safety and base64 encoding correctness
   - Implemented in: `KmsEncryptionRoundTripPropertyTests.cs` with 100+ test iterations
   - ✅ **Integration tests complete**: Comprehensive test suite in `KmsEncryptionIntegrationTests.cs`
     - End-to-end encryption/decryption with various message types
     - Algorithm validation (AES-256-GCM with envelope encryption)
     - Encryption context and AAD (Additional Authenticated Data) validation
     - Performance testing with different message sizes and concurrent operations
     - Data key caching performance improvements
     - Error handling for invalid ciphertext and corrupted envelopes
6. ✅ **KMS Key Rotation Seamlessness** - Seamless key rotation without service interruption
   - Property test validates: messages encrypted with old keys decrypt after rotation
   - Ensures backward compatibility with previous key versions
   - Verifies automatic key version management
   - Tests rotation monitoring and alerting
   - Implemented in: `KmsKeyRotationPropertyTests.cs` and `KmsKeyRotationIntegrationTests.cs`
7. ✅ **KMS Security and Performance** - Sensitive data masking and performance validation
   - Property test validates: [SensitiveData] attributes properly masked in logs
   - Ensures encryption performance within acceptable bounds
   - Verifies IAM permission enforcement
   - Tests audit logging and compliance
   - Implemented in: `KmsSecurityAndPerformancePropertyTests.cs` and `KmsSecurityAndPerformanceTests.cs`
8. ✅ **AWS Health Check Accuracy** - Health checks reflect actual service availability
   - Property test validates: health checks accurately detect service availability, accessibility, and permissions
   - Ensures health checks complete within acceptable latency (< 5 seconds)
   - Verifies reliability under concurrent access (90%+ consistency)
   - Tests SQS queue existence, accessibility, send/receive permissions
   - Tests SNS topic availability, attributes, publish permissions, subscription status
   - Tests KMS key accessibility, encryption/decryption permissions, key status
   - Implemented in: `AwsHealthCheckPropertyTests.cs` and `AwsHealthCheckIntegrationTests.cs`
9. ✅ **AWS Performance Measurement Consistency** - Reliable performance metrics across test runs
   - Property test validates: performance measurements are consistent within acceptable variance
   - Ensures throughput measurements are reliable across iterations
   - Verifies latency measurements under various load conditions
   - Tests resource utilization tracking accuracy
   - Implemented in: `AwsPerformanceMeasurementPropertyTests.cs`
10. ✅ **LocalStack AWS Service Equivalence** - LocalStack provides equivalent functionality to AWS
11. ✅ **AWS Resilience Pattern Compliance** - Circuit breakers, retry policies work correctly
    - Property test validates: circuit breakers open on failures and close on recovery
    - Ensures retry policies implement exponential backoff with jitter
    - Verifies maximum retry limits are enforced
    - Tests graceful handling of service throttling
    - Implemented in: `AwsResiliencePatternPropertyTests.cs` and resilience integration tests
12. ✅ **AWS Dead Letter Queue Processing** - Failed message analysis and reprocessing
    - Property test validates: failed messages captured with complete metadata
    - Ensures message analysis and categorization work correctly
    - Verifies reprocessing capabilities and workflows
    - Tests monitoring and alerting integration
    - Implemented in: `AwsDeadLetterQueuePropertyTests.cs` and DLQ integration tests
13. ✅ **AWS IAM Security Enforcement** - Proper authentication and authorization
    - Property test validates: IAM role assumption and credential management
    - Ensures least privilege access enforcement with flexible wildcard validation
    - Verifies cross-account access and permission boundaries
    - Tests IAM policy effectiveness and compliance
    - **Enhanced Validation Logic**: Handles property-based test generation edge cases
      - Lenient required permission validation when test generation produces more required permissions than available actions
      - Validates that granted actions include required permissions up to the available action count
      - Prevents false negatives from random test data generation
      - Supports zero wildcards or controlled wildcard usage (up to 50% of actions)
    - Implemented in: `IamSecurityPropertyTests.cs` and `IamRoleTests.cs`
14. ✅ **AWS Encryption in Transit** - TLS encryption for all communications
15. ✅ **AWS Audit Logging** - CloudTrail integration and event logging
16. ✅ **AWS CI/CD Integration Reliability** - Tests run successfully in CI/CD with proper isolation

## Performance Testing

### Throughput Benchmarks
- **SQS Standard Queues** - High-throughput message processing
- **SQS FIFO Queues** - Ordered message processing performance
- **SNS Topic Publishing** - Event publishing rates and fan-out performance

### Latency Analysis
- **End-to-End Latency** - Complete message processing times
- **Network Overhead** - Cloud service communication latency
- **Encryption Overhead** - Performance impact of message encryption
- **Serialization Impact** - Message serialization/deserialization costs

### Scalability Testing
- **Concurrent Connections** - Performance under increasing load
- **Resource Utilization** - Memory, CPU, and network usage
- **Service Limits** - Behavior at cloud service limits
- **Auto-scaling** - Performance during scaling events

## Security Testing

### Authentication and Authorization
- **AWS IAM Roles** - Proper role assumption and credential management
- **Least Privilege** - Access control enforcement testing
- **Cross-Account Access** - Multi-account permission validation

### Encryption Validation
- **AWS KMS** - Message encryption with key rotation
- **Sensitive Data Masking** - Automatic masking in logs
- **Encryption in Transit** - TLS validation for all communications

### Compliance Testing
- **Audit Logging** - CloudTrail integration
- **Data Sovereignty** - Regional data handling compliance
- **Security Standards** - Validation against security best practices

## Resilience Testing

### Circuit Breaker Patterns
- **Failure Detection** - Automatic circuit opening on service failures
- **Recovery Testing** - Circuit closing on service recovery
- **Half-Open State** - Gradual recovery validation
- **Configuration Testing** - Threshold and timeout validation

### Retry Policies
- **Exponential Backoff** - Proper retry timing implementation
- **Jitter Implementation** - Randomization to prevent thundering herd
- **Maximum Retry Limits** - Proper retry limit enforcement
- **Poison Message Handling** - Failed message isolation

### Dead Letter Queue Processing
- **Failed Message Capture** - Complete failure metadata preservation
- **Message Analysis** - Failure pattern detection and categorization
- **Reprocessing Capabilities** - Message recovery and retry workflows
- **Monitoring Integration** - Alerting and operational visibility

## Testing Bus Configuration

### Overview

The Bus Configuration System requires testing at multiple levels to ensure routing is configured correctly and resources are created as expected.

### Unit Testing Bus Configuration

Unit tests validate configuration without connecting to cloud services:

**Testing Configuration Structure:**

```csharp
using SourceFlow.Cloud.Configuration;
using Xunit;

public class BusConfigurationTests
{
    [Fact]
    public void BusConfiguration_Should_Register_Command_Routes()
    {
        // Arrange
        var builder = new BusConfigurationBuilder();
        
        // Act
        var config = builder
            .Send
                .Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
                .Command<UpdateOrderCommand>(q => q.Queue("orders.fifo"))
            .Build();
        
        // Assert
        Assert.Equal(2, config.CommandRoutes.Count);
        Assert.Equal("orders.fifo", config.CommandRoutes[typeof(CreateOrderCommand)]);
        Assert.Equal("orders.fifo", config.CommandRoutes[typeof(UpdateOrderCommand)]);
    }
    
    [Fact]
    public void BusConfiguration_Should_Register_Event_Routes()
    {
        // Arrange
        var builder = new BusConfigurationBuilder();
        
        // Act
        var config = builder
            .Raise
                .Event<OrderCreatedEvent>(t => t.Topic("order-events"))
                .Event<OrderUpdatedEvent>(t => t.Topic("order-events"))
            .Build();
        
        // Assert
        Assert.Equal(2, config.EventRoutes.Count);
        Assert.Equal("order-events", config.EventRoutes[typeof(OrderCreatedEvent)]);
        Assert.Equal("order-events", config.EventRoutes[typeof(OrderUpdatedEvent)]);
    }
    
    [Fact]
    public void BusConfiguration_Should_Register_Listening_Queues()
    {
        // Arrange
        var builder = new BusConfigurationBuilder();
        
        // Act
        var config = builder
            .Listen.To
                .CommandQueue("orders.fifo")
                .CommandQueue("inventory.fifo")
            .Build();
        
        // Assert
        Assert.Equal(2, config.ListeningQueues.Count);
        Assert.Contains("orders.fifo", config.ListeningQueues);
        Assert.Contains("inventory.fifo", config.ListeningQueues);
    }
    
    [Fact]
    public void BusConfiguration_Should_Register_Topic_Subscriptions()
    {
        // Arrange
        var builder = new BusConfigurationBuilder();
        
        // Act
        var config = builder
            .Subscribe.To
                .Topic("order-events")
                .Topic("payment-events")
            .Build();
        
        // Assert
        Assert.Equal(2, config.SubscribedTopics.Count);
        Assert.Contains("order-events", config.SubscribedTopics);
        Assert.Contains("payment-events", config.SubscribedTopics);
    }
    
    [Fact]
    public void BusConfiguration_Should_Validate_Listening_Queue_Required_For_Subscriptions()
    {
        // Arrange
        var builder = new BusConfigurationBuilder();
        
        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder
                .Subscribe.To
                    .Topic("order-events")
                .Build());
        
        Assert.Contains("at least one command queue", exception.Message);
    }
}
```

### Integration Testing with LocalStack

Integration tests validate Bus Configuration with LocalStack:

**AWS Integration Test Example:**

```csharp
using SourceFlow.Cloud.AWS;
using Xunit;

public class AwsBusConfigurationIntegrationTests : IClassFixture<LocalStackFixture>
{
    private readonly LocalStackFixture _localStack;
    
    public AwsBusConfigurationIntegrationTests(LocalStackFixture localStack)
    {
        _localStack = localStack;
    }
    
    [Fact]
    public async Task Bootstrapper_Should_Create_SQS_Queues()
    {
        // Arrange
        var services = new ServiceCollection();
        services.UseSourceFlowAws(
            options => {
                options.ServiceUrl = _localStack.ServiceUrl;
                options.Region = RegionEndpoint.USEast1;
            },
            bus => bus
                .Send
                    .Command<CreateOrderCommand>(q => q.Queue("test-orders.fifo"))
                .Listen.To
                    .CommandQueue("test-orders.fifo"));
        
        var provider = services.BuildServiceProvider();
        
        // Act
        var bootstrapper = provider.GetRequiredService<IHostedService>();
        await bootstrapper.StartAsync(CancellationToken.None);
        
        // Assert
        var sqsClient = provider.GetRequiredService<IAmazonSQS>();
        var response = await sqsClient.GetQueueUrlAsync("test-orders.fifo");
        Assert.NotNull(response.QueueUrl);
        Assert.Contains("test-orders.fifo", response.QueueUrl);
    }
    
    [Fact]
    public async Task Bootstrapper_Should_Create_SNS_Topics()
    {
        // Arrange
        var services = new ServiceCollection();
        services.UseSourceFlowAws(
            options => {
                options.ServiceUrl = _localStack.ServiceUrl;
                options.Region = RegionEndpoint.USEast1;
            },
            bus => bus
                .Raise
                    .Event<OrderCreatedEvent>(t => t.Topic("test-order-events"))
                .Listen.To
                    .CommandQueue("test-orders"));
        
        var provider = services.BuildServiceProvider();
        
        // Act
        var bootstrapper = provider.GetRequiredService<IHostedService>();
        await bootstrapper.StartAsync(CancellationToken.None);
        
        // Assert
        var snsClient = provider.GetRequiredService<IAmazonSimpleNotificationService>();
        var topics = await snsClient.ListTopicsAsync();
        Assert.Contains(topics.Topics, t => t.TopicArn.Contains("test-order-events"));
    }
    
    [Fact]
    public async Task Bootstrapper_Should_Subscribe_Queues_To_Topics()
    {
        // Arrange
        var services = new ServiceCollection();
        services.UseSourceFlowAws(
            options => {
                options.ServiceUrl = _localStack.ServiceUrl;
                options.Region = RegionEndpoint.USEast1;
            },
            bus => bus
                .Listen.To
                    .CommandQueue("test-orders")
                .Subscribe.To
                    .Topic("test-order-events"));
        
        var provider = services.BuildServiceProvider();
        
        // Act
        var bootstrapper = provider.GetRequiredService<IHostedService>();
        await bootstrapper.StartAsync(CancellationToken.None);
        
        // Assert
        var snsClient = provider.GetRequiredService<IAmazonSimpleNotificationService>();
        var topics = await snsClient.ListTopicsAsync();
        var topicArn = topics.Topics.First(t => t.TopicArn.Contains("test-order-events")).TopicArn;
        
        var subscriptions = await snsClient.ListSubscriptionsByTopicAsync(topicArn);
        Assert.NotEmpty(subscriptions.Subscriptions);
        Assert.Contains(subscriptions.Subscriptions, s => s.Protocol == "sqs");
    }
}
```

### Validation Strategies

**Strategy 1: Configuration Snapshot Testing**

Capture and compare Bus Configuration snapshots:

```csharp
[Fact]
public void BusConfiguration_Should_Match_Expected_Snapshot()
{
    // Arrange
    var builder = new BusConfigurationBuilder();
    var config = builder
        .Send
            .Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
        .Raise
            .Event<OrderCreatedEvent>(t => t.Topic("order-events"))
        .Listen.To
            .CommandQueue("orders.fifo")
        .Subscribe.To
            .Topic("order-events")
        .Build();
    
    // Act
    var snapshot = config.ToSnapshot();
    
    // Assert
    var expected = LoadExpectedSnapshot("bus-configuration-v1.json");
    Assert.Equal(expected, snapshot);
}
```

**Strategy 2: End-to-End Routing Validation**

Test complete message flow through configured routing:

```csharp
[Fact]
public async Task Message_Should_Flow_Through_Configured_Routes()
{
    // Arrange
    var services = ConfigureServicesWithBusConfiguration();
    var provider = services.BuildServiceProvider();
    
    // Start bootstrapper
    var bootstrapper = provider.GetRequiredService<IHostedService>();
    await bootstrapper.StartAsync(CancellationToken.None);
    
    // Act
    var commandBus = provider.GetRequiredService<ICommandBus>();
    var command = new CreateOrderCommand(new CreateOrderPayload { /* ... */ });
    await commandBus.PublishAsync(command);
    
    // Assert
    // Verify command was routed to correct queue
    // Verify event was published to correct topic
    // Verify listeners received messages
}
```

**Strategy 3: Resource Existence Validation**

Verify all configured resources exist after bootstrapping:

```csharp
[Fact]
public async Task All_Configured_Resources_Should_Exist_After_Bootstrapping()
{
    // Arrange
    var services = ConfigureServicesWithBusConfiguration();
    var provider = services.BuildServiceProvider();
    var config = provider.GetRequiredService<BusConfiguration>();
    
    // Act
    var bootstrapper = provider.GetRequiredService<IHostedService>();
    await bootstrapper.StartAsync(CancellationToken.None);
    
    // Assert
    foreach (var queue in config.ListeningQueues)
    {
        var exists = await QueueExistsAsync(queue);
        Assert.True(exists, $"Queue {queue} should exist");
    }
    
    foreach (var topic in config.SubscribedTopics)
    {
        var exists = await TopicExistsAsync(topic);
        Assert.True(exists, $"Topic {topic} should exist");
    }
}
```

### Best Practices for Testing Bus Configuration

1. **Use LocalStack for Integration Tests**
   - LocalStack for AWS testing
   - Faster feedback than real cloud services
   - No cloud costs during development

2. **Test Configuration Validation**
   - Verify invalid configurations throw exceptions
   - Test edge cases (empty queues, missing topics)
   - Validate required relationships (queue for subscriptions)

3. **Test Resource Creation Idempotency**
   - Run bootstrapper multiple times
   - Verify no errors on repeated execution
   - Ensure resources aren't duplicated

4. **Test FIFO Queue Detection**
   - Verify .fifo suffix enables sessions/FIFO
   - Test both FIFO and standard queues
   - Validate message ordering guarantees

5. **Mock Bootstrapper for Unit Tests**
   - Test application logic without cloud dependencies
   - Mock IBusBootstrapConfiguration interface
   - Verify routing decisions without resource creation

## Local Development Support

### Emulator Integration
- **LocalStack** - Complete AWS service emulation (SQS, SNS, KMS, IAM)
- **Container Management** - Automatic lifecycle with TestContainers
- **Health Checking** - Service availability validation

### Development Workflow
- **Fast Feedback** - Rapid test execution without cloud dependencies
- **Cost Optimization** - No cloud resource costs during development
- **Offline Development** - Full functionality without internet connectivity
- **Debugging Support** - Local service inspection and troubleshooting

## CI/CD Integration

### Automated Testing
- **Multi-Environment** - Tests against both LocalStack and real AWS services
- **Resource Provisioning** - Automatic cloud resource creation and cleanup via `AwsResourceManager`
- **Parallel Execution** - Concurrent test execution for faster feedback
- **Test Isolation** - Proper resource isolation to prevent interference with unique naming and tagging

### Reporting and Analysis
- **Comprehensive Reports** - Detailed test results with metrics and analysis
- **Performance Trends** - Historical performance tracking and regression detection
- **Security Validation** - Security test results with compliance reporting
- **Failure Analysis** - Actionable error messages with troubleshooting guidance

## AWS Resource Management

### AwsResourceManager (Implemented)
The `AwsResourceManager` provides comprehensive automated resource lifecycle management for AWS integration testing:

- **Resource Provisioning** - Automatic creation of SQS queues, SNS topics, KMS keys, and IAM roles
- **CloudFormation Integration** - Stack-based resource provisioning for complex scenarios
- **Resource Tracking** - Automatic tagging and cleanup with unique test prefixes
- **Cost Estimation** - Resource cost calculation and monitoring capabilities
- **Multi-Account Support** - Cross-account resource management and cleanup
- **Test Isolation** - Unique naming prevents conflicts in parallel test execution

### LocalStack Manager (Implemented)
Enhanced LocalStack container management with comprehensive AWS service emulation:

- **Service Emulation** - Full support for SQS (standard and FIFO), SNS, KMS, and IAM
- **Health Checking** - Service availability validation and readiness detection
- **Port Management** - Automatic port allocation and conflict resolution
- **Container Lifecycle** - Automated startup, health checks, and cleanup
- **Service Validation** - AWS SDK compatibility testing for each service

### AWS Test Environment (Implemented)
Comprehensive test environment abstraction supporting both LocalStack and real AWS:

- **Dual Mode Support** - Seamless switching between LocalStack emulation and real AWS services
- **Resource Creation** - FIFO queues, standard queues, SNS topics, KMS keys with proper configuration
- **Health Monitoring** - Service-level health checks with response time tracking
- **Managed Identity** - Support for IAM roles and credential management
- **Service Clients** - Pre-configured SQS, SNS, KMS, and IAM clients

### Key Features
- **Unique Naming** - Test prefix-based resource naming to prevent conflicts
- **Automatic Cleanup** - Comprehensive resource cleanup to prevent cost leaks
- **Resource Tagging** - Metadata tagging for identification and cost allocation
- **Health Monitoring** - Resource availability and permission validation
- **Batch Operations** - Efficient bulk resource creation and deletion

### Usage Example
```csharp
var resourceManager = serviceProvider.GetRequiredService<IAwsResourceManager>();
var resourceSet = await resourceManager.CreateTestResourcesAsync("test-prefix", 
    AwsResourceTypes.SqsQueues | AwsResourceTypes.SnsTopics);

// Use resources for testing
// ...

// Automatic cleanup
await resourceManager.CleanupResourcesAsync(resourceSet);
```

## Getting Started

### Prerequisites
- **.NET 9.0 SDK** or later
- **Docker Desktop** for LocalStack support
- **AWS CLI** (optional, for real AWS testing)

### Running Tests

```bash
# Run all tests
dotnet test

# Run only unit tests (fast feedback, no external dependencies)
dotnet test --filter "Category=Unit"

# Run integration tests
dotnet test --filter "Category=Integration"

# Run AWS integration tests with LocalStack
dotnet test --filter "Category=Integration&Category=RequiresLocalStack"

# Run specific test categories
dotnet test --filter "Category=Performance"
dotnet test --filter "Category=Security"
dotnet test --filter "Category=Property"

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"
```

### Configuration

Tests can be configured via `appsettings.json`:

```json
{
  "CloudIntegrationTests": {
    "UseEmulators": true,
    "RunPerformanceTests": false,
    "RunSecurityTests": true,
    "Aws": {
      "UseLocalStack": true,
      "Region": "us-east-1"
    }
  }
}
```

## Best Practices

### Test Design
- **Property-based testing** for universal correctness validation
- **Unit tests** for specific scenarios and edge cases
- **Integration tests** for end-to-end validation
- **Performance tests** for scalability and optimization

### Cloud Resource Management
- **Unique naming** with test prefixes to prevent conflicts
- **Automatic cleanup** to prevent resource leaks and costs
- **Resource tagging** for identification and cost tracking
- **Least privilege** access for security testing

### Performance Testing
- **Baseline establishment** for regression detection
- **Multiple iterations** for statistical significance
- **Environment consistency** for reliable measurements
- **Resource monitoring** during test execution

## Troubleshooting

### Common Issues
- **Container startup failures** - Check Docker Desktop and port availability
- **Cloud authentication** - Verify AWS credentials and permissions
- **Performance variations** - Ensure stable test environment
- **Resource cleanup** - Monitor cloud resources for proper cleanup

### Debug Configuration
- **Detailed logging** for test execution visibility
- **Service health checking** for LocalStack availability
- **Resource inspection** - Cloud service validation
- **Performance profiling** for optimization opportunities

## Contributing

When adding new cloud integration tests:

1. **Follow existing patterns** - Use established test structures and naming
2. **Include property tests** - Add universal correctness properties
3. **Add performance benchmarks** - Measure new functionality performance
4. **Document test scenarios** - Provide clear test descriptions
5. **Ensure cleanup** - Proper resource management and cleanup
6. **Update documentation** - Keep guides current with new capabilities

## Related Documentation

- [AWS Cloud Architecture](Architecture/07-AWS-Cloud-Architecture.md)
- [Architecture Overview](Architecture/README.md)
- [Cloud Message Idempotency Guide](Cloud-Message-Idempotency-Guide.md)

---

**Document Version**: 2.0  
**Last Updated**: 2025-02-04  
**Covers**: AWS cloud integration testing capabilities

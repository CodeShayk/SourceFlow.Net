# Design Document: AWS Cloud Integration Testing

## Overview

The aws-cloud-integration-testing feature provides a comprehensive testing framework specifically for validating SourceFlow's AWS cloud integrations. This system ensures that SourceFlow applications work correctly in AWS environments by testing SQS command dispatching with FIFO ordering, SNS event publishing with fan-out messaging, KMS encryption for sensitive data, dead letter queue handling, and performance characteristics under various load conditions.

The design builds upon the existing `SourceFlow.Cloud.AWS.Tests` project structure while significantly expanding it with comprehensive integration testing, LocalStack emulation, performance benchmarking, security validation, and resilience testing. The framework supports both local development using LocalStack emulators and cloud-based testing using real AWS services.

## Architecture

### Enhanced Test Project Structure

The testing framework extends the existing AWS test project with comprehensive testing capabilities:

```
tests/SourceFlow.Cloud.AWS.Tests/
├── Unit/                          # Unit tests with mocks (existing)
│   ├── AwsSqsCommandDispatcherTests.cs
│   ├── AwsSnsEventDispatcherTests.cs
│   ├── PropertyBasedTests.cs      # Enhanced with AWS-specific properties
│   └── RoutingConfigurationTests.cs
├── Integration/                   # Integration tests with LocalStack
│   ├── SqsIntegrationTests.cs     # SQS FIFO and standard queue tests
│   ├── SnsIntegrationTests.cs     # SNS topic and subscription tests
│   ├── KmsIntegrationTests.cs     # KMS encryption and key rotation tests
│   ├── DeadLetterQueueTests.cs    # DLQ handling and recovery tests
│   ├── LocalStackIntegrationTests.cs (existing, enhanced)
│   └── HealthCheckIntegrationTests.cs
├── Performance/                   # BenchmarkDotNet performance tests
│   ├── SqsPerformanceBenchmarks.cs (existing, enhanced)
│   ├── SnsPerformanceBenchmarks.cs
│   ├── KmsPerformanceBenchmarks.cs
│   ├── EndToEndLatencyBenchmarks.cs
│   └── ScalabilityBenchmarks.cs
├── Security/                      # AWS security and IAM tests
│   ├── IamRoleTests.cs
│   ├── KmsEncryptionTests.cs
│   ├── AccessControlTests.cs
│   └── AuditLoggingTests.cs
├── Resilience/                    # Circuit breaker and retry tests
│   ├── CircuitBreakerTests.cs
│   ├── RetryPolicyTests.cs
│   ├── ServiceFailureTests.cs
│   └── ThrottlingTests.cs
├── E2E/                          # End-to-end scenario tests
│   ├── CommandToEventFlowTests.cs
│   ├── SagaOrchestrationTests.cs
│   └── MultiServiceIntegrationTests.cs
└── TestHelpers/                   # Test utilities and fixtures
    ├── LocalStackTestFixture.cs   (existing, enhanced)
    ├── AwsTestEnvironment.cs
    ├── PerformanceTestHelpers.cs  (existing, enhanced)
    ├── SecurityTestHelpers.cs
    ├── ResilienceTestHelpers.cs
    └── TestDataGenerators.cs
```

### Test Environment Management

The architecture supports multiple AWS test environments with enhanced capabilities:

1. **LocalStack Development Environment**: Full AWS service emulation with SQS, SNS, KMS, and IAM
2. **AWS Integration Environment**: Real AWS services with automated resource provisioning
3. **CI/CD Environment**: Automated testing with both LocalStack and AWS services
4. **Performance Testing Environment**: Dedicated AWS resources for load testing

### AWS Service Integration Architecture

The testing framework integrates with AWS services through multiple layers:

```
Test Layer → AWS SDK Layer → Service Layer (LocalStack/AWS)
    ↓              ↓              ↓
Unit Tests → Mock Clients → No Network
Integration → Real Clients → LocalStack Emulator
E2E Tests → Real Clients → AWS Services
```

## Components and Interfaces

### Enhanced Test Environment Abstractions

```csharp
public interface IAwsTestEnvironment : ICloudTestEnvironment
{
    IAmazonSQS SqsClient { get; }
    IAmazonSimpleNotificationService SnsClient { get; }
    IAmazonKeyManagementService KmsClient { get; }
    IAmazonIdentityManagementService IamClient { get; }
    
    Task<string> CreateFifoQueueAsync(string queueName);
    Task<string> CreateStandardQueueAsync(string queueName);
    Task<string> CreateTopicAsync(string topicName);
    Task<string> CreateKmsKeyAsync(string keyAlias);
    Task<bool> ValidateIamPermissionsAsync(string action, string resource);
}

public interface ILocalStackManager
{
    Task StartAsync(LocalStackConfiguration config);
    Task StopAsync();
    Task<bool> IsServiceAvailableAsync(string serviceName);
    Task WaitForServicesAsync(params string[] services);
    string GetServiceEndpoint(string serviceName);
}

public interface IAwsResourceManager
{
    Task<AwsResourceSet> CreateTestResourcesAsync(string testPrefix);
    Task CleanupResourcesAsync(AwsResourceSet resources);
    Task<bool> ResourceExistsAsync(string resourceArn);
    Task<IEnumerable<string>> ListTestResourcesAsync(string testPrefix);
}
```

### AWS Test Environment Implementation

```csharp
public class AwsTestEnvironment : IAwsTestEnvironment
{
    private readonly AwsTestConfiguration _configuration;
    private readonly ILocalStackManager _localStackManager;
    private readonly IAwsResourceManager _resourceManager;
    
    public IAmazonSQS SqsClient { get; private set; }
    public IAmazonSimpleNotificationService SnsClient { get; private set; }
    public IAmazonKeyManagementService KmsClient { get; private set; }
    public IAmazonIdentityManagementService IamClient { get; private set; }
    
    public bool IsLocalEmulator => _configuration.UseLocalStack;
    
    public async Task InitializeAsync()
    {
        if (IsLocalEmulator)
        {
            await _localStackManager.StartAsync(_configuration.LocalStack);
            await _localStackManager.WaitForServicesAsync("sqs", "sns", "kms", "iam");
            
            // Configure clients for LocalStack
            var clientConfig = new AmazonSQSConfig
            {
                ServiceURL = _localStackManager.GetServiceEndpoint("sqs"),
                UseHttp = true
            };
            
            SqsClient = new AmazonSQSClient("test", "test", clientConfig);
            // Similar setup for other clients...
        }
        else
        {
            // Configure clients for real AWS
            SqsClient = new AmazonSQSClient();
            SnsClient = new AmazonSimpleNotificationServiceClient();
            KmsClient = new AmazonKeyManagementServiceClient();
            IamClient = new AmazonIdentityManagementServiceClient();
        }
        
        await ValidateServicesAsync();
    }
    
    public async Task<string> CreateFifoQueueAsync(string queueName)
    {
        var fifoQueueName = queueName.EndsWith(".fifo") ? queueName : $"{queueName}.fifo";
        
        var response = await SqsClient.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = fifoQueueName,
            Attributes = new Dictionary<string, string>
            {
                ["FifoQueue"] = "true",
                ["ContentBasedDeduplication"] = "true",
                ["MessageRetentionPeriod"] = "1209600", // 14 days
                ["VisibilityTimeoutSeconds"] = "30"
            }
        });
        
        return response.QueueUrl;
    }
}
```

### Enhanced LocalStack Manager

```csharp
public class LocalStackManager : ILocalStackManager
{
    private readonly ITestContainersBuilder _containerBuilder;
    private IContainer _container;
    
    public async Task StartAsync(LocalStackConfiguration config)
    {
        _container = _containerBuilder
            .WithImage("localstack/localstack:latest")
            .WithEnvironment("SERVICES", string.Join(",", config.EnabledServices))
            .WithEnvironment("DEBUG", config.Debug ? "1" : "0")
            .WithEnvironment("DATA_DIR", "/tmp/localstack/data")
            .WithPortBinding(4566, 4566) // LocalStack main port
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(4566).ForPath("/_localstack/health")))
            .Build();
        
        await _container.StartAsync();
        
        // Wait for all services to be ready
        await WaitForServicesAsync(config.EnabledServices.ToArray());
    }
    
    public async Task<bool> IsServiceAvailableAsync(string serviceName)
    {
        try
        {
            var httpClient = new HttpClient();
            var response = await httpClient.GetAsync($"http://localhost:4566/_localstack/health");
            
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var healthStatus = JsonSerializer.Deserialize<LocalStackHealthStatus>(content);
                
                return healthStatus.Services.ContainsKey(serviceName) && 
                       healthStatus.Services[serviceName] == "available";
            }
        }
        catch
        {
            // Service not available
        }
        
        return false;
    }
}
```

### AWS Performance Testing Components

```csharp
public class AwsPerformanceTestRunner : IPerformanceTestRunner
{
    private readonly IAwsTestEnvironment _environment;
    private readonly IMetricsCollector _metricsCollector;
    
    public async Task<PerformanceTestResult> RunSqsThroughputTestAsync(SqsThroughputScenario scenario)
    {
        var queueUrl = await _environment.CreateStandardQueueAsync($"perf-test-{Guid.NewGuid():N}");
        var stopwatch = Stopwatch.StartNew();
        var messageCount = 0;
        var errors = new List<string>();
        
        try
        {
            var tasks = Enumerable.Range(0, scenario.ConcurrentSenders)
                .Select(async senderId =>
                {
                    for (int i = 0; i < scenario.MessagesPerSender; i++)
                    {
                        try
                        {
                            var message = GenerateTestMessage(scenario.MessageSize);
                            await _environment.SqsClient.SendMessageAsync(new SendMessageRequest
                            {
                                QueueUrl = queueUrl,
                                MessageBody = message,
                                MessageAttributes = CreateMessageAttributes(senderId, i)
                            });
                            
                            Interlocked.Increment(ref messageCount);
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Sender {senderId}, Message {i}: {ex.Message}");
                        }
                    }
                });
            
            await Task.WhenAll(tasks);
            stopwatch.Stop();
            
            return new PerformanceTestResult
            {
                TestName = $"SQS Throughput - {scenario.ConcurrentSenders} senders",
                Duration = stopwatch.Elapsed,
                MessagesPerSecond = messageCount / stopwatch.Elapsed.TotalSeconds,
                TotalMessages = messageCount,
                Errors = errors,
                ResourceUsage = await _metricsCollector.GetResourceUsageAsync()
            };
        }
        finally
        {
            await _environment.SqsClient.DeleteQueueAsync(queueUrl);
        }
    }
}
```

### AWS Security Testing Components

```csharp
public class AwsSecurityTestRunner
{
    private readonly IAwsTestEnvironment _environment;
    private readonly IAwsResourceManager _resourceManager;
    
    public async Task<SecurityTestResult> ValidateIamPermissionsAsync(IamPermissionScenario scenario)
    {
        var result = new SecurityTestResult { TestName = scenario.Name };
        
        try
        {
            // Test required permissions
            foreach (var permission in scenario.RequiredPermissions)
            {
                var hasPermission = await _environment.ValidateIamPermissionsAsync(
                    permission.Action, permission.Resource);
                
                if (!hasPermission)
                {
                    result.Violations.Add(new SecurityViolation
                    {
                        Type = "MissingPermission",
                        Description = $"Missing required permission: {permission.Action} on {permission.Resource}",
                        Severity = "High",
                        Recommendation = $"Add IAM policy allowing {permission.Action}"
                    });
                }
            }
            
            // Test forbidden permissions
            foreach (var permission in scenario.ForbiddenPermissions)
            {
                var hasPermission = await _environment.ValidateIamPermissionsAsync(
                    permission.Action, permission.Resource);
                
                if (hasPermission)
                {
                    result.Violations.Add(new SecurityViolation
                    {
                        Type = "ExcessivePermission",
                        Description = $"Has forbidden permission: {permission.Action} on {permission.Resource}",
                        Severity = "Medium",
                        Recommendation = "Remove excessive IAM permissions following least privilege principle"
                    });
                }
            }
            
            result.AccessControlValid = result.Violations.Count == 0;
        }
        catch (Exception ex)
        {
            result.Violations.Add(new SecurityViolation
            {
                Type = "ValidationError",
                Description = $"Failed to validate permissions: {ex.Message}",
                Severity = "High",
                Recommendation = "Check IAM configuration and test setup"
            });
        }
        
        return result;
    }
}
```

## Data Models

### AWS Test Configuration Models

```csharp
public class AwsTestConfiguration
{
    public string Region { get; set; } = "us-east-1";
    public bool UseLocalStack { get; set; } = true;
    public bool RunIntegrationTests { get; set; } = true;
    public bool RunPerformanceTests { get; set; } = false;
    public bool RunSecurityTests { get; set; } = true;
    
    public LocalStackConfiguration LocalStack { get; set; } = new();
    public AwsServiceConfiguration Services { get; set; } = new();
    public PerformanceTestConfiguration Performance { get; set; } = new();
    public SecurityTestConfiguration Security { get; set; } = new();
}

public class LocalStackConfiguration
{
    public string Endpoint { get; set; } = "http://localhost:4566";
    public List<string> EnabledServices { get; set; } = new() { "sqs", "sns", "kms", "iam" };
    public bool Debug { get; set; } = false;
    public bool PersistData { get; set; } = false;
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
}

public class AwsServiceConfiguration
{
    public SqsConfiguration Sqs { get; set; } = new();
    public SnsConfiguration Sns { get; set; } = new();
    public KmsConfiguration Kms { get; set; } = new();
    public IamConfiguration Iam { get; set; } = new();
}

public class SqsConfiguration
{
    public int MessageRetentionPeriod { get; set; } = 1209600; // 14 days
    public int VisibilityTimeout { get; set; } = 30;
    public int MaxReceiveCount { get; set; } = 3;
    public bool EnableDeadLetterQueue { get; set; } = true;
    public Dictionary<string, string> DefaultAttributes { get; set; } = new();
}
```

### AWS Test Scenario Models

```csharp
public class SqsThroughputScenario : TestScenario
{
    public QueueType QueueType { get; set; } = QueueType.Standard;
    public int MessagesPerSender { get; set; } = 100;
    public bool UseBatchSending { get; set; } = false;
    public int BatchSize { get; set; } = 10;
    public bool EnableDeadLetterQueue { get; set; } = true;
}

public class SnsPerformanceScenario : TestScenario
{
    public int SubscriberCount { get; set; } = 5;
    public SubscriberType SubscriberType { get; set; } = SubscriberType.SQS;
    public bool UseMessageFiltering { get; set; } = false;
    public Dictionary<string, string> MessageAttributes { get; set; } = new();
}

public class KmsEncryptionScenario : TestScenario
{
    public string KeyAlias { get; set; } = "alias/sourceflow-test";
    public EncryptionAlgorithm Algorithm { get; set; } = EncryptionAlgorithm.SYMMETRIC_DEFAULT;
    public bool TestKeyRotation { get; set; } = false;
    public List<string> SensitiveFields { get; set; } = new();
}

public enum QueueType
{
    Standard,
    Fifo
}

public enum SubscriberType
{
    SQS,
    Lambda,
    HTTP,
    Email
}

public enum EncryptionAlgorithm
{
    SYMMETRIC_DEFAULT,
    RSAES_OAEP_SHA_1,
    RSAES_OAEP_SHA_256
}
```

### AWS Resource Management Models

```csharp
public class AwsResourceSet
{
    public string TestPrefix { get; set; } = "";
    public List<string> QueueUrls { get; set; } = new();
    public List<string> TopicArns { get; set; } = new();
    public List<string> KmsKeyIds { get; set; } = new();
    public List<string> IamRoleArns { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Dictionary<string, string> Tags { get; set; } = new();
}

public class AwsHealthCheckResult
{
    public string ServiceName { get; set; } = "";
    public bool IsAvailable { get; set; }
    public TimeSpan ResponseTime { get; set; }
    public string Endpoint { get; set; } = "";
    public Dictionary<string, object> ServiceMetrics { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}
```

### AWS Performance Test Models

```csharp
public class SqsPerformanceMetrics : PerformanceTestResult
{
    public double SendMessagesPerSecond { get; set; }
    public double ReceiveMessagesPerSecond { get; set; }
    public TimeSpan AverageSendLatency { get; set; }
    public TimeSpan AverageReceiveLatency { get; set; }
    public int DeadLetterMessages { get; set; }
    public int BatchOperations { get; set; }
    public double BatchEfficiency { get; set; }
}

public class SnsPerformanceMetrics : PerformanceTestResult
{
    public double PublishMessagesPerSecond { get; set; }
    public double DeliverySuccessRate { get; set; }
    public TimeSpan AveragePublishLatency { get; set; }
    public TimeSpan AverageDeliveryLatency { get; set; }
    public int SubscriberCount { get; set; }
    public Dictionary<string, double> PerSubscriberMetrics { get; set; } = new();
}
```

## Correctness Properties

*A property is a characteristic or behavior that should hold true across all valid executions of a system—essentially, a formal statement about what the system should do. Properties serve as the bridge between human-readable specifications and machine-verifiable correctness guarantees.*

Now I need to use the prework tool to analyze the acceptance criteria before writing the correctness properties:
## Property Reflection

After completing the initial prework analysis, I need to perform property reflection to eliminate redundancy and consolidate related properties:

**Property Reflection Analysis:**

1. **SQS Message Handling Properties (1.1-1.5)**: These can be consolidated into comprehensive SQS properties that cover ordering, throughput, dead letter handling, batching, and attribute preservation.

2. **SNS Publishing Properties (2.1-2.5)**: These can be consolidated into comprehensive SNS properties covering publishing, fan-out, filtering, correlation, and error handling.

3. **KMS Encryption Properties (3.1-3.5)**: The round-trip encryption (3.1) and key rotation (3.2) are distinct and should remain separate. Performance testing (3.5) can be combined with the main encryption property.

4. **Health Check Properties (4.1-4.5)**: These can be consolidated into a single comprehensive health check accuracy property that covers all AWS services.

5. **Performance Properties (5.1-5.5)**: These can be consolidated into comprehensive performance measurement properties covering throughput, latency, and scalability.

6. **LocalStack Equivalence Properties (6.1-6.5)**: These can be consolidated into a single property that validates LocalStack provides equivalent functionality to real AWS services.

7. **Resilience Properties (7.1-7.5)**: Circuit breaker and retry properties can be consolidated, while DLQ handling remains separate.

8. **Security Properties (8.1-8.5)**: IAM authentication and permission properties can be consolidated, while encryption and audit logging remain separate.

9. **CI/CD Properties (9.1-9.5)**: These can be consolidated into comprehensive CI/CD integration properties.

**Consolidated Properties:**
- Combine 1.1, 1.2, 1.4, 1.5 into "SQS Message Processing Correctness"
- Keep 1.3 separate as "SQS Dead Letter Queue Handling"
- Combine 2.1, 2.2, 2.4 into "SNS Event Publishing Correctness"
- Combine 2.3, 2.5 into "SNS Message Filtering and Error Handling"
- Keep 3.1 as "KMS Encryption Round-Trip Consistency"
- Keep 3.2 as "KMS Key Rotation Seamlessness"
- Combine 3.3, 3.4, 3.5 into "KMS Security and Performance"
- Combine 4.1-4.5 into "AWS Health Check Accuracy"
- Combine 5.1-5.5 into "AWS Performance Measurement Consistency"
- Combine 6.1-6.5 into "LocalStack AWS Service Equivalence"
- Combine 7.1, 7.2, 7.4, 7.5 into "AWS Resilience Pattern Compliance"
- Keep 7.3 separate as "AWS Dead Letter Queue Processing"
- Combine 8.1, 8.2, 8.3 into "AWS IAM Security Enforcement"
- Keep 8.4, 8.5 separate as specific security properties
- Combine 9.1-9.5 into "AWS CI/CD Integration Reliability"

### Property 1: SQS Message Processing Correctness
*For any* valid SourceFlow command and SQS queue configuration (standard or FIFO), when the command is dispatched through SQS, it should be delivered correctly with proper message attributes (EntityId, SequenceNo, CommandType), maintain FIFO ordering within message groups when applicable, support batch operations up to AWS limits, and achieve consistent throughput performance.
**Validates: Requirements 1.1, 1.2, 1.4, 1.5**

### Property 2: SQS Dead Letter Queue Handling
*For any* command that fails processing beyond the maximum retry count, it should be automatically moved to the configured dead letter queue with complete failure metadata, retry history, and be available for analysis and reprocessing.
**Validates: Requirements 1.3**

### Property 3: SNS Event Publishing Correctness
*For any* valid SourceFlow event and SNS topic configuration, when the event is published, it should be delivered to all subscribers with proper message attributes, correlation ID preservation, and fan-out messaging to multiple subscriber types (SQS, Lambda, HTTP).
**Validates: Requirements 2.1, 2.2, 2.4**

### Property 4: SNS Message Filtering and Error Handling
*For any* SNS subscription with message filtering rules, only events matching the filter criteria should be delivered to that subscriber, and failed deliveries should trigger appropriate retry mechanisms and error handling.
**Validates: Requirements 2.3, 2.5**

### Property 5: KMS Encryption Round-Trip Consistency
*For any* message containing sensitive data, when encrypted using AWS KMS and then decrypted, the resulting message should be identical to the original message with all sensitive data properly protected.
**Validates: Requirements 3.1**

### Property 6: KMS Key Rotation Seamlessness
*For any* encrypted message flow, when KMS keys are rotated, existing messages should continue to be decryptable using the old key version and new messages should use the new key without service interruption.
**Validates: Requirements 3.2**

### Property 7: KMS Security and Performance
*For any* KMS encryption operation, proper IAM permissions should be enforced, sensitive data should be automatically masked in logs, and encryption operations should complete within acceptable performance thresholds.
**Validates: Requirements 3.3, 3.4, 3.5**

### Property 8: AWS Health Check Accuracy
*For any* AWS service configuration (SQS, SNS, KMS), health checks should accurately reflect the actual availability, accessibility, and permission status of the service, returning true when services are operational and false when they are not.
**Validates: Requirements 4.1, 4.2, 4.3, 4.4, 4.5**

### Property 9: AWS Performance Measurement Consistency
*For any* AWS performance test scenario, when executed multiple times under similar conditions, the performance measurements (SQS/SNS throughput, end-to-end latency, resource utilization) should be consistent within acceptable variance ranges and scale appropriately with load.
**Validates: Requirements 5.1, 5.2, 5.3, 5.4, 5.5**

### Property 10: LocalStack AWS Service Equivalence
*For any* test scenario that runs successfully against real AWS services (SQS, SNS, KMS), the same test should run successfully against LocalStack emulators with functionally equivalent results and meaningful performance metrics.
**Validates: Requirements 6.1, 6.2, 6.3, 6.4, 6.5**

### Property 11: AWS Resilience Pattern Compliance
*For any* AWS service operation, when failures occur, the system should implement proper circuit breaker patterns, exponential backoff retry policies with jitter, graceful handling of service throttling, and automatic recovery when services become available.
**Validates: Requirements 7.1, 7.2, 7.4, 7.5**

### Property 12: AWS Dead Letter Queue Processing
*For any* message that fails processing in AWS services, it should be captured in the appropriate dead letter queue with complete failure metadata and be retrievable for analysis, reprocessing, or archival.
**Validates: Requirements 7.3**

### Property 13: AWS IAM Security Enforcement
*For any* AWS service operation, proper IAM role authentication should be enforced, permissions should follow least privilege principles, and cross-account access should work correctly with proper permission boundaries.

**Validates: Requirements 8.1, 8.2, 8.3**

**Enhanced Validation Logic:**
- **Flexible Wildcard Handling**: The property test validates that wildcard permissions (`*` or `service:*`) are minimized when the `IncludeWildcardPermissions` flag is set
- **Zero-Wildcard Support**: Allows scenarios where no wildcards are generated (wildcard count = 0), which is valid for strict least-privilege configurations
- **Controlled Wildcard Usage**: When wildcards are present, validates they don't exceed 50% of total actions or a minimum threshold of 2 actions
- **Realistic Constraints**: Accommodates the random nature of property-based test generation while ensuring core security principles are maintained

This flexible validation ensures the property test remains robust across diverse input scenarios while still validating that least privilege principles are properly enforced.

### Property 14: AWS Encryption in Transit
*For any* communication with AWS services, TLS encryption should be used for all API calls and data transmission should be secure end-to-end.
**Validates: Requirements 8.4**

### Property 15: AWS Audit Logging
*For any* security-relevant operation, appropriate audit events should be logged to CloudTrail with sufficient detail for security analysis and compliance requirements.
**Validates: Requirements 8.5**

### Property 16: AWS CI/CD Integration Reliability
*For any* CI/CD test execution, tests should run successfully against both LocalStack and real AWS services, automatically provision and clean up resources, provide comprehensive reporting with actionable error messages, and maintain proper test isolation.
**Validates: Requirements 9.1, 9.2, 9.3, 9.4, 9.5**

## Error Handling

### AWS Service Failures
The testing framework handles various AWS service failure scenarios specific to the AWS cloud environment:

- **SQS Service Failures**: Tests validate graceful degradation when SQS queues are unavailable, including proper circuit breaker activation and dead letter queue fallback
- **SNS Service Failures**: Tests verify proper error handling for SNS topic publishing failures, subscription delivery failures, and fan-out messaging issues
- **KMS Service Failures**: Tests validate encryption/decryption failure handling, key unavailability scenarios, and permission denied errors
- **Network Connectivity Issues**: Tests simulate AWS service endpoint connectivity issues and validate retry behavior with exponential backoff
- **AWS Service Limits**: Tests validate behavior when AWS service limits are exceeded (SQS message size, SNS publish rate, KMS encryption requests)

### LocalStack Emulator Failures
The framework provides robust error handling for LocalStack-specific issues:

- **Container Startup Failures**: Automatic retry and fallback to real AWS services when LocalStack containers fail to start
- **Service Emulation Gaps**: Clear error messages when LocalStack doesn't fully emulate AWS service behavior
- **Port Conflicts**: Automatic port detection and conflict resolution for LocalStack services
- **Resource Cleanup**: Proper cleanup of LocalStack containers and resources after test completion

### AWS Resource Management Failures
The testing framework includes safeguards against AWS resource management issues:

- **Resource Creation Failures**: Retry mechanisms for AWS resource provisioning with exponential backoff
- **Permission Errors**: Clear error messages for insufficient IAM permissions with specific remediation guidance
- **Resource Cleanup Failures**: Best-effort cleanup with detailed logging of any resources that couldn't be deleted
- **Cross-Account Access Issues**: Proper error handling for cross-account resource access failures

### Test Data Integrity and Security
The framework ensures test data integrity and security in AWS environments:

- **Message Encryption Validation**: Automatic verification that sensitive test data is properly encrypted
- **Test Data Isolation**: Unique prefixes and tags for all test resources to prevent cross-contamination
- **Credential Security**: Secure handling of AWS credentials with automatic rotation and least privilege access
- **Audit Trail**: Complete audit logging of all test operations for security and compliance

## Testing Strategy

### Dual Testing Approach for AWS Integration
The testing strategy employs both unit testing and property-based testing as complementary approaches specifically tailored for AWS cloud integration:

- **Unit Tests**: Validate specific AWS service interactions, edge cases, and error conditions for individual AWS components
- **Property Tests**: Verify universal properties across all AWS service inputs using randomized test data and AWS service configurations
- **Integration Tests**: Validate end-to-end scenarios with real AWS services and LocalStack emulators
- **Performance Tests**: Measure and validate AWS service performance characteristics under various load conditions

### Property-Based Testing Configuration for AWS
The framework uses **xUnit** and **FsCheck** for .NET property-based testing with AWS-specific configuration:

- **Minimum 100 iterations** per property test to ensure comprehensive coverage of AWS service scenarios
- **AWS-specific generators** for SQS queue configurations, SNS topic setups, KMS key configurations, and IAM policies
- **AWS service constraint generators** that respect AWS service limits (SQS message size, SNS topic limits, etc.)
- **Shrinking strategies** optimized for AWS resource configurations to find minimal failing examples
- **Test tagging** with format: **Feature: aws-cloud-integration-testing, Property {number}: {property_text}**

Each correctness property is implemented by a single property-based test that references its design document property and validates AWS-specific behavior.

### Unit Testing Balance for AWS Services
Unit tests focus on AWS-specific scenarios:
- **Specific AWS Examples**: Concrete scenarios demonstrating correct AWS service usage patterns
- **AWS Edge Cases**: Boundary conditions specific to AWS service limits and constraints
- **AWS Error Conditions**: Invalid AWS configurations, permission errors, and service failure scenarios
- **AWS Integration Points**: Interactions between SourceFlow components and AWS SDK clients

Property tests handle comprehensive AWS configuration coverage through randomization, while unit tests provide targeted validation of critical AWS integration scenarios.

### Test Environment Strategy for AWS
The testing strategy supports multiple AWS-specific environments:

1. **Local Development with LocalStack**: Fast feedback using LocalStack emulators for SQS, SNS, KMS, and IAM
2. **AWS Integration Testing**: Validation against real AWS services in isolated test accounts
3. **AWS Performance Testing**: Dedicated AWS resources optimized for load and scalability testing
4. **CI/CD Pipeline**: Automated testing with both LocalStack emulators and real AWS services

### AWS Performance Testing Strategy
Performance tests are designed specifically for AWS service characteristics:
- **AWS Service Baselines**: Measure performance characteristics under normal AWS service conditions
- **AWS Limit Testing**: Validate performance at AWS service limits (SQS throughput, SNS fan-out, KMS encryption rates)
- **AWS Region Performance**: Test performance across different AWS regions and availability zones
- **AWS Cost Optimization**: Identify opportunities for AWS resource usage optimization and cost reduction

### AWS Security Testing Strategy
Security tests validate AWS-specific security features:
- **KMS Encryption Effectiveness**: End-to-end encryption and decryption correctness with AWS KMS
- **IAM Access Control**: Proper authentication and authorization enforcement using AWS IAM
- **AWS Service Security**: Validation of AWS service security features (SQS encryption, SNS access policies)
- **AWS Compliance**: Ensure compliance with AWS security best practices and standards

### AWS Documentation and Reporting Strategy
The testing framework provides comprehensive AWS-specific documentation and reporting:
- **AWS Setup Guides**: Step-by-step instructions for AWS account configuration, IAM setup, and service provisioning
- **LocalStack Setup**: Instructions for LocalStack installation and configuration for AWS service emulation
- **AWS Performance Reports**: Detailed metrics specific to AWS services with cost analysis and optimization recommendations
- **AWS Troubleshooting**: Common AWS issues, error codes, and resolution steps with links to AWS documentation
- **AWS Security Reports**: Security validation results with AWS-specific recommendations and compliance status
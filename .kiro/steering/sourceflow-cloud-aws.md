# SourceFlow AWS Cloud Extension

**Project**: `src/SourceFlow.Cloud.AWS/`  
**Purpose**: AWS cloud integration for distributed command and event processing

**Dependencies**: 
- `SourceFlow` (core framework with integrated cloud functionality)
- AWS SDK packages (SQS, SNS, KMS)

## Core Functionality

### AWS Services Integration
- **Amazon SQS** - Command dispatching and queuing with FIFO support
- **Amazon SNS** - Event publishing and fan-out messaging
- **AWS KMS** - Message encryption for sensitive data
- **AWS Health Checks** - Service availability monitoring

### Infrastructure Components
- **`AwsBusBootstrapper`** - Hosted service for automatic resource provisioning
- **`SqsClientFactory`** - Factory for creating configured SQS clients
- **`SnsClientFactory`** - Factory for creating configured SNS clients
- **`AwsHealthCheck`** - Health check implementation for AWS services

### Dispatcher Implementations
- **`AwsSqsCommandDispatcher`** - Routes commands to SQS queues
- **`AwsSnsEventDispatcher`** - Publishes events to SNS topics
- **Enhanced Versions** - Advanced features with encryption and monitoring

### Listener Services
- **`AwsSqsCommandListener`** - Background service consuming SQS commands
- **`AwsSnsEventListener`** - Background service consuming SNS events
- **Hosted Service Integration** - Automatic lifecycle management

### Monitoring & Observability
- **`AwsDeadLetterMonitor`** - Failed message monitoring and analysis
- **`AwsTelemetryExtensions`** - AWS-specific metrics and tracing

## Configuration System

### Fluent Bus Configuration

The Bus Configuration System provides a type-safe, intuitive way to configure AWS messaging infrastructure using a fluent API. This approach eliminates the need to manually manage SQS queue URLs and SNS topic ARNs.

**Complete Configuration Example:**

```csharp
using SourceFlow.Cloud.AWS;
using Amazon;

services.UseSourceFlowAws(
    options => { 
        options.Region = RegionEndpoint.USEast1;
        options.EnableEncryption = true;
        options.KmsKeyId = "alias/sourceflow-key";
        options.MaxConcurrentCalls = 10;
    },
    bus => bus
        .Send
            .Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
            .Command<UpdateOrderCommand>(q => q.Queue("orders.fifo"))
            .Command<CancelOrderCommand>(q => q.Queue("orders.fifo"))
            .Command<AdjustInventoryCommand>(q => q.Queue("inventory.fifo"))
            .Command<ProcessPaymentCommand>(q => q.Queue("payments.fifo"))
        .Raise
            .Event<OrderCreatedEvent>(t => t.Topic("order-events"))
            .Event<OrderUpdatedEvent>(t => t.Topic("order-events"))
            .Event<OrderCancelledEvent>(t => t.Topic("order-events"))
            .Event<InventoryAdjustedEvent>(t => t.Topic("inventory-events"))
            .Event<PaymentProcessedEvent>(t => t.Topic("payment-events"))
        .Listen.To
            .CommandQueue("orders.fifo")
            .CommandQueue("inventory.fifo")
            .CommandQueue("payments.fifo")
        .Subscribe.To
            .Topic("order-events")
            .Topic("payment-events")
            .Topic("inventory-events"));
```

### AWS-Specific Bus Configuration Details

#### SQS Queue URL Resolution

The bootstrapper automatically converts short queue names to full SQS URLs:

**Short Name:** `"orders.fifo"`  
**Resolved URL:** `https://sqs.us-east-1.amazonaws.com/123456789012/orders.fifo`

**How it works:**
1. Bootstrapper retrieves AWS account ID from STS
2. Constructs full SQS URL using region and account ID
3. Stores resolved URL in routing configuration
4. Dispatchers use full URL for message sending

**Benefits:**
- No need to hardcode account IDs or regions
- Configuration is portable across environments
- Easier to read and maintain

#### SNS Topic ARN Resolution

The bootstrapper automatically converts short topic names to full SNS ARNs:

**Short Name:** `"order-events"`  
**Resolved ARN:** `arn:aws:sns:us-east-1:123456789012:order-events`

**How it works:**
1. Bootstrapper retrieves AWS account ID from STS
2. Constructs full SNS ARN using region and account ID
3. Stores resolved ARN in routing configuration
4. Dispatchers use full ARN for message publishing

#### FIFO Queue Configuration

Use the `.fifo` suffix to enable FIFO (First-In-First-Out) queue features:

```csharp
.Send
    .Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
```

**Automatic FIFO Attributes:**
- `FifoQueue = true` - Enables FIFO mode
- `ContentBasedDeduplication = true` - Automatic deduplication based on message body
- `MessageGroupId` - Set to entity ID for ordering per entity
- `MessageDeduplicationId` - Generated from message content hash

**When to use FIFO queues:**
- Commands must be processed in order per entity
- Exactly-once processing is required
- Message deduplication is needed

**Standard Queue Alternative:**
```csharp
.Send
    .Command<SendEmailCommand>(q => q.Queue("notifications"))
```
- Higher throughput (no ordering guarantees)
- At-least-once delivery
- Best for independent operations

#### Bootstrapper Resource Creation

The `AwsBusBootstrapper` automatically creates missing AWS resources at application startup:

**SQS Queue Creation:**
```csharp
// For FIFO queues (detected by .fifo suffix)
var createQueueRequest = new CreateQueueRequest
{
    QueueName = "orders.fifo",
    Attributes = new Dictionary<string, string>
    {
        { "FifoQueue", "true" },
        { "ContentBasedDeduplication", "true" },
        { "MessageRetentionPeriod", "1209600" }, // 14 days
        { "VisibilityTimeout", "30" }
    }
};

// For standard queues
var createQueueRequest = new CreateQueueRequest
{
    QueueName = "notifications",
    Attributes = new Dictionary<string, string>
    {
        { "MessageRetentionPeriod", "1209600" },
        { "VisibilityTimeout", "30" }
    }
};
```

**SNS Topic Creation:**
```csharp
var createTopicRequest = new CreateTopicRequest
{
    Name = "order-events",
    Attributes = new Dictionary<string, string>
    {
        { "DisplayName", "Order Events Topic" }
    }
};
```

**SNS Subscription Creation:**

The bootstrapper automatically subscribes command queues to configured topics:

```csharp
// For each topic in Subscribe.To configuration
// And each queue in Listen.To configuration
var subscribeRequest = new SubscribeRequest
{
    TopicArn = "arn:aws:sns:us-east-1:123456789012:order-events",
    Protocol = "sqs",
    Endpoint = "arn:aws:sqs:us-east-1:123456789012:orders.fifo",
    Attributes = new Dictionary<string, string>
    {
        { "RawMessageDelivery", "true" }
    }
};
```

**Resource Creation Behavior:**
- Idempotent operations (safe to run multiple times)
- Skips creation if resource already exists
- Logs resource creation for audit trail
- Fails fast if permissions are insufficient

#### IAM Permission Requirements

**Minimum Required Permissions for Bootstrapper:**

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": [
        "sqs:CreateQueue",
        "sqs:GetQueueUrl",
        "sqs:GetQueueAttributes",
        "sqs:SetQueueAttributes",
        "sqs:ReceiveMessage",
        "sqs:SendMessage",
        "sqs:DeleteMessage"
      ],
      "Resource": "arn:aws:sqs:*:*:*"
    },
    {
      "Effect": "Allow",
      "Action": [
        "sns:CreateTopic",
        "sns:GetTopicAttributes",
        "sns:Subscribe",
        "sns:Publish"
      ],
      "Resource": "arn:aws:sns:*:*:*"
    },
    {
      "Effect": "Allow",
      "Action": [
        "sts:GetCallerIdentity"
      ],
      "Resource": "*"
    }
  ]
}
```

**With KMS Encryption:**

```json
{
  "Effect": "Allow",
  "Action": [
    "kms:Decrypt",
    "kms:Encrypt",
    "kms:GenerateDataKey"
  ],
  "Resource": "arn:aws:kms:*:*:key/*"
}
```

**Production Best Practices:**
- Use least privilege principle
- Restrict resources to specific queue/topic ARNs
- Use separate IAM roles for different environments
- Enable CloudTrail for audit logging

### Bus Bootstrapper
- **Automatic Resource Creation** - Creates missing SQS queues and SNS topics at startup
- **Name Resolution** - Converts short names to full URLs/ARNs
- **FIFO Queue Detection** - Automatically configures FIFO attributes for .fifo queues
- **Topic Subscription** - Subscribes queues to topics automatically
- **Validation** - Ensures at least one command queue exists when subscribing to topics
- **Hosted Service** - Runs before listeners to ensure routing is ready

### AWS Options
```csharp
services.UseSourceFlowAws(options => {
    options.Region = RegionEndpoint.USEast1;
    options.EnableCommandRouting = true;
    options.EnableEventRouting = true;
    options.EnableEncryption = true;
    options.KmsKeyId = "alias/sourceflow-key";
});
```

## Service Registration

### Core Pattern
```csharp
services.UseSourceFlowAws(
    options => { /* AWS settings */ },
    bus => { /* Bus configuration */ },
    configureIdempotency: null); // Optional: custom idempotency configuration
// Automatically registers:
// - AWS SDK clients (SQS, SNS) via factories
// - Command and event dispatchers
// - AwsBusBootstrapper as hosted service
// - Background listeners
// - BusConfiguration with routing
// - Idempotency service (in-memory by default)
// - Health checks
// - Telemetry services
```

### Idempotency Configuration

The `UseSourceFlowAws` method supports four approaches for configuring idempotency:

#### 1. Default (In-Memory) - Recommended for Single Instance

```csharp
services.UseSourceFlowAws(
    options => { options.Region = RegionEndpoint.USEast1; },
    bus => bus.Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo")));
// InMemoryIdempotencyService registered automatically
```

#### 2. Pre-Registered Service - Recommended for Multi-Instance

```csharp
// Register SQL-based idempotency before AWS configuration
services.AddSourceFlowIdempotency(connectionString, cleanupIntervalMinutes: 60);

services.UseSourceFlowAws(
    options => { options.Region = RegionEndpoint.USEast1; },
    bus => bus.Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo")));
// Uses pre-registered EfIdempotencyService
```

#### 3. Explicit Configuration - Alternative Approach

```csharp
services.UseSourceFlowAws(
    options => { options.Region = RegionEndpoint.USEast1; },
    bus => bus.Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo")),
    configureIdempotency: services =>
    {
        services.AddSourceFlowIdempotency(connectionString, cleanupIntervalMinutes: 60);
        // Or register custom implementation:
        // services.AddScoped<IIdempotencyService, MyCustomIdempotencyService>();
    });
```

#### 4. Fluent Builder API - Expressive Configuration

```csharp
// Configure idempotency using fluent builder
var idempotencyBuilder = new IdempotencyConfigurationBuilder()
    .UseEFIdempotency(connectionString, cleanupIntervalMinutes: 60);

idempotencyBuilder.Build(services);

services.UseSourceFlowAws(
    options => { options.Region = RegionEndpoint.USEast1; },
    bus => bus.Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo")));
```

**Builder Methods:**
- `UseEFIdempotency(connectionString, cleanupIntervalMinutes)` - Entity Framework-based (multi-instance)
- `UseInMemory()` - In-memory implementation (single-instance)
- `UseCustom<TImplementation>()` - Custom implementation by type
- `UseCustom(factory)` - Custom implementation with factory function

**Registration Logic:**
1. If `configureIdempotency` parameter is provided, it's executed
2. If `configureIdempotency` is null, checks if `IIdempotencyService` is already registered
3. If not registered, registers `InMemoryIdempotencyService` as default

**See Also**: [Idempotency Configuration Guide](../../docs/Idempotency-Configuration-Guide.md)

### Service Lifetimes
- **Singleton**: AWS clients, event dispatchers, bus configuration, listeners, bootstrapper
- **Scoped**: Command dispatchers, idempotency service (matches core framework pattern)

### Registration Order
1. AWS client factories
2. BusConfiguration from fluent API
3. Idempotency service (in-memory, pre-registered, or custom)
4. AwsBusBootstrapper (must run before listeners)
5. Command and event dispatchers
6. Background listeners
7. Health checks and telemetry

## Message Serialization

### JSON Serialization
- **`JsonMessageSerializer`** - Handles command/event serialization
- **Custom Converters** - `CommandPayloadConverter`, `EntityConverter`, `MetadataConverter`
- **Type Safety** - Preserves full type information for deserialization

### Message Attributes
- **CommandType** - Full assembly-qualified type name
- **EntityId** - Entity reference for FIFO ordering
- **SequenceNo** - Event sourcing sequence number
- **Custom Attributes** - Extensible metadata support

## Routing Strategies

### Fluent Configuration (Recommended)
```csharp
services.UseSourceFlowAws(
    options => { options.Region = RegionEndpoint.USEast1; },
    bus => bus
        .Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
        .Raise.Event<OrderCreatedEvent>(t => t.Topic("order-events")));
```

### Key Features
- **Short Names Only** - Provide queue/topic names, not full URLs/ARNs
- **Automatic Resolution** - Bootstrapper resolves full paths at startup
- **Resource Creation** - Missing queues/topics created automatically
- **FIFO Support** - .fifo suffix automatically enables FIFO attributes
- **Type Safety** - Compile-time validation of command/event types

## Security Features

### Message Encryption
- **`AwsKmsMessageEncryption`** - KMS-based message encryption
- **Sensitive Data Masking** - `[SensitiveData]` attribute support
- **Key Rotation** - Automatic KMS key rotation support

### Access Control
- **IAM Integration** - Uses AWS SDK credential chain
- **Least Privilege** - Minimal required permissions
- **Cross-Account Support** - Multi-account message routing

## Monitoring & Observability

### Health Checks
- **`AwsHealthCheck`** - Validates SQS/SNS connectivity
- **Service Availability** - Queue/topic existence verification
- **Permission Validation** - Access rights verification

### Telemetry Integration
- **`AwsTelemetryExtensions`** - AWS-specific metrics and tracing
- **CloudWatch Integration** - Native AWS monitoring
- **Custom Metrics** - Message throughput, error rates, latency

### Dead Letter Queues
- **`AwsDeadLetterMonitor`** - Failed message monitoring
- **Automatic Retry** - Configurable retry policies
- **Error Analysis** - Failure pattern detection

## Performance Optimizations

### Connection Management
- **Client Factories** - `SqsClientFactory`, `SnsClientFactory`
- **Connection Pooling** - Reuse AWS SDK clients
- **Regional Optimization** - Multi-region support

### Batch Processing
- **SQS Batch Operations** - Up to 10 messages per request
- **SNS Fan-out** - Efficient multi-subscriber delivery
- **Parallel Processing** - Concurrent message handling

## Development Guidelines

### Bus Configuration Best Practices
- Use fluent API for type-safe configuration
- Provide short names only (e.g., "orders.fifo", not full URLs)
- Use .fifo suffix for queues requiring ordering
- Group related commands to the same queue
- Let bootstrapper create resources in development
- Use CloudFormation/Terraform for production infrastructure
- Configure at least one command queue when subscribing to topics

### Bootstrapper Behavior
- Runs once at application startup as hosted service
- Creates missing SQS queues with appropriate attributes
- Creates missing SNS topics (idempotent operation)
- Subscribes queues to topics automatically
- Resolves short names to full URLs/ARNs
- Must complete before listeners start polling

### Message Design
- Keep messages small and focused
- Include correlation IDs for tracing
- Use FIFO queues for ordering requirements
- Design for idempotency
- Use content-based deduplication for FIFO queues

### Error Handling
- Implement proper retry policies
- Use dead letter queues for failed messages
- Log correlation IDs for debugging
- Monitor queue depths and processing rates
- Handle `CircuitBreakerOpenException` gracefully

### Security Best Practices
- Encrypt sensitive message content with KMS
- Use IAM roles instead of access keys
- Implement message validation
- Audit message routing configurations
- Use least privilege IAM policies

### Testing Strategies
- Use LocalStack for local development
- Mock AWS services in unit tests
- Integration tests with real AWS services
- Load testing for throughput validation
- Test FIFO ordering guarantees
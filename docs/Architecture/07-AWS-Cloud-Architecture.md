# AWS Cloud Architecture

## Overview

The SourceFlow.Cloud.AWS extension provides distributed command and event processing using AWS cloud services. This document describes the architecture, implementation patterns, and design decisions for AWS cloud integration.

**Target Audience**: Developers implementing AWS cloud integration for distributed SourceFlow applications.

---

## Table of Contents

1. [AWS Services Integration](#aws-services-integration)
2. [Bus Configuration System](#bus-configuration-system)
3. [Command Routing Architecture](#command-routing-architecture)
4. [Event Routing Architecture](#event-routing-architecture)
5. [Idempotency Service Architecture](#idempotency-service-architecture)
6. [Bootstrapper Resource Provisioning](#bootstrapper-resource-provisioning)
7. [Message Serialization](#message-serialization)
8. [Security and Encryption](#security-and-encryption)
9. [Observability and Monitoring](#observability-and-monitoring)
10. [Performance Optimizations](#performance-optimizations)

---

## AWS Services Integration

### Core AWS Services

SourceFlow.Cloud.AWS integrates with three primary AWS services:

#### 1. Amazon SQS (Simple Queue Service)
**Purpose**: Command dispatching and queuing

**Features Used**:
- Standard queues for high-throughput, at-least-once delivery
- FIFO queues for ordered, exactly-once processing per entity
- Dead letter queues for failed message handling
- Long polling for efficient message retrieval

**Use Cases**:
- Distributing commands across multiple application instances
- Ensuring ordered command processing per entity (FIFO)
- Decoupling command producers from consumers

#### 2. Amazon SNS (Simple Notification Service)
**Purpose**: Event publishing and fan-out messaging

**Features Used**:
- Topics for publish-subscribe patterns
- SQS subscriptions for reliable event delivery
- Message filtering (future enhancement)
- Fan-out to multiple subscribers

**Use Cases**:
- Broadcasting events to multiple consumers
- Cross-service event notifications
- Decoupling event producers from consumers

#### 3. AWS KMS (Key Management Service)
**Purpose**: Message encryption for sensitive data

**Features Used**:
- Symmetric encryption keys
- Automatic key rotation
- IAM-based access control
- Envelope encryption pattern

**Use Cases**:
- Encrypting sensitive command/event payloads
- Protecting PII and confidential business data
- Compliance with data protection regulations

---

## Bus Configuration System

### Architecture Overview

The Bus Configuration System provides a fluent API for configuring AWS message routing without hardcoding queue URLs or topic ARNs.

```
User Configuration (Short Names)
        ↓
BusConfiguration (Type-Safe Routing)
        ↓
AwsBusBootstrapper (Name Resolution)
        ↓
AWS Resources (Full URLs/ARNs)
```

### Configuration Flow

```csharp
services.UseSourceFlowAws(
    options => { options.Region = RegionEndpoint.USEast1; },
    bus => bus
        .Send
            .Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
        .Raise
            .Event<OrderCreatedEvent>(t => t.Topic("order-events"))
        .Listen.To
            .CommandQueue("orders.fifo")
        .Subscribe.To
            .Topic("order-events"));
```

### Key Components

#### BusConfiguration
**Purpose**: Store type-safe routing configuration

**Structure**:
```csharp
public class BusConfiguration
{
    // Command Type → Queue Name mapping
    Dictionary<Type, string> CommandRoutes { get; }
    
    // Event Type → Topic Name mapping
    Dictionary<Type, string> EventRoutes { get; }
    
    // Queue names to listen for commands
    List<string> CommandQueues { get; }
    
    // Topic names to subscribe for events
    List<string> EventTopics { get; }
}
```

#### BusConfigurationBuilder
**Purpose**: Fluent API for building configuration

**Sections**:
- `Send`: Configure command routing
- `Raise`: Configure event routing
- `Listen.To`: Configure command queue listeners
- `Subscribe.To`: Configure event topic subscriptions

---

## Command Routing Architecture

### High-Level Flow

```
Command Published
    ↓
CommandBus (assigns sequence number)
    ↓
AwsSqsCommandDispatcher (checks routing)
    ↓
SQS Queue (message persisted)
    ↓
AwsSqsCommandListener (polls queue)
    ↓
CommandBus.Publish (local processing)
    ↓
Saga Handles Command
```

### AwsSqsCommandDispatcher

**Purpose**: Route commands to SQS queues based on configuration

**Key Responsibilities**:
1. Check if command type is configured for AWS routing
2. Serialize command to JSON
3. Set message attributes (CommandType, EntityId, SequenceNo)
4. Send to configured SQS queue
5. Handle FIFO queue requirements (MessageGroupId, MessageDeduplicationId)

**FIFO Queue Handling**:
```csharp
// For queues ending with .fifo
MessageGroupId = command.Entity.Id.ToString(); // Ensures ordering per entity
MessageDeduplicationId = GenerateDeduplicationId(command); // Content-based
```

### AwsSqsCommandListener

**Purpose**: Poll SQS queues and process commands locally

**Key Responsibilities**:
1. Long-poll configured SQS queues
2. Deserialize messages to commands
3. Check idempotency (prevent duplicate processing)
4. Publish to local CommandBus
5. Delete message from queue after successful processing
6. Handle errors and dead letter queue routing

**Concurrency**:
- Configurable `MaxConcurrentCalls` for parallel processing
- Each message processed in separate scope for isolation

---

## Event Routing Architecture

### High-Level Flow

```
Event Published
    ↓
EventQueue (enqueues event)
    ↓
AwsSnsEventDispatcher (checks routing)
    ↓
SNS Topic (message published)
    ↓
SQS Queue (subscribed to topic)
    ↓
AwsSqsCommandListener (polls queue)
    ↓
EventQueue.Enqueue (local processing)
    ↓
Aggregates/Views Handle Event
```

### AwsSnsEventDispatcher

**Purpose**: Publish events to SNS topics based on configuration

**Key Responsibilities**:
1. Check if event type is configured for AWS routing
2. Serialize event to JSON
3. Set message attributes (EventType, EntityId, SequenceNo)
4. Publish to configured SNS topic

### Topic-to-Queue Subscription

**Architecture**:
```
SNS Topic (order-events)
    ↓
SQS Subscription (fwd-to-orders)
    ↓
SQS Queue (orders.fifo)
    ↓
AwsSqsCommandListener
```

**Benefits**:
- Reliable delivery (SQS persistence)
- Ordered processing (FIFO queues)
- Dead letter queue support
- Decoupling of publishers and subscribers

---

## Idempotency Service Architecture

### Purpose

Prevent duplicate message processing in distributed systems where at-least-once delivery guarantees can result in duplicate messages.

### Architecture Options

#### 1. In-Memory Idempotency (Single Instance)

**Implementation**: `InMemoryIdempotencyService`

**Structure**:
```csharp
ConcurrentDictionary<string, DateTime> processedMessages
```

**Use Case**: Single-instance deployments or local development

**Limitations**: Not shared across instances

#### 2. SQL-Based Idempotency (Multi-Instance)

**Implementation**: `EfIdempotencyService`

**Database Table**:
```sql
CREATE TABLE IdempotencyRecords (
    IdempotencyKey NVARCHAR(500) PRIMARY KEY,
    ProcessedAt DATETIME2 NOT NULL,
    ExpiresAt DATETIME2 NOT NULL,
    MessageType NVARCHAR(500) NULL,
    CloudProvider NVARCHAR(50) NULL
);

CREATE INDEX IX_IdempotencyRecords_ExpiresAt 
    ON IdempotencyRecords(ExpiresAt);
```

**Use Case**: Multi-instance deployments requiring shared state

**Features**:
- Distributed duplicate detection
- Automatic cleanup of expired records
- Configurable TTL per message

### Idempotency Key Generation

**Format**: `{CloudProvider}:{MessageType}:{MessageId}`

**Example**: `AWS:CreateOrderCommand:abc123-def456`

### Integration with Dispatchers

```csharp
// In AwsSqsCommandListener
var idempotencyKey = GenerateIdempotencyKey(message);

if (await idempotencyService.HasProcessedAsync(idempotencyKey))
{
    // Duplicate detected - skip processing
    await DeleteMessage(message);
    return;
}

// Process message
await commandBus.Publish(command);

// Mark as processed
await idempotencyService.MarkAsProcessedAsync(idempotencyKey, ttl);
```

---

## Bootstrapper Resource Provisioning

### AwsBusBootstrapper

**Purpose**: Automatically provision AWS resources at application startup

**Lifecycle**: Runs as IHostedService before listeners start

### Provisioning Process

#### 1. Account ID Resolution
```csharp
var identity = await stsClient.GetCallerIdentityAsync();
var accountId = identity.Account;
```

#### 2. Queue URL Resolution
```csharp
// Short name: "orders.fifo"
// Resolved URL: "https://sqs.us-east-1.amazonaws.com/123456789012/orders.fifo"

var queueUrl = $"https://sqs.{region}.amazonaws.com/{accountId}/{queueName}";
```

#### 3. Topic ARN Resolution
```csharp
// Short name: "order-events"
// Resolved ARN: "arn:aws:sns:us-east-1:123456789012:order-events"

var topicArn = $"arn:aws:sns:{region}:{accountId}:{topicName}";
```

#### 4. Resource Creation

**SQS Queues**:
```csharp
// Standard queue
await sqsClient.CreateQueueAsync(new CreateQueueRequest
{
    QueueName = "notifications",
    Attributes = new Dictionary<string, string>
    {
        { "MessageRetentionPeriod", "1209600" }, // 14 days
        { "VisibilityTimeout", "30" }
    }
});

// FIFO queue (detected by .fifo suffix)
await sqsClient.CreateQueueAsync(new CreateQueueRequest
{
    QueueName = "orders.fifo",
    Attributes = new Dictionary<string, string>
    {
        { "FifoQueue", "true" },
        { "ContentBasedDeduplication", "true" },
        { "MessageRetentionPeriod", "1209600" },
        { "VisibilityTimeout", "30" }
    }
});
```

**SNS Topics**:
```csharp
await snsClient.CreateTopicAsync(new CreateTopicRequest
{
    Name = "order-events",
    Attributes = new Dictionary<string, string>
    {
        { "DisplayName", "Order Events Topic" }
    }
});
```

**SNS Subscriptions**:
```csharp
// Subscribe queue to topic
await snsClient.SubscribeAsync(new SubscribeRequest
{
    TopicArn = "arn:aws:sns:us-east-1:123456789012:order-events",
    Protocol = "sqs",
    Endpoint = "arn:aws:sqs:us-east-1:123456789012:orders.fifo",
    Attributes = new Dictionary<string, string>
    {
        { "RawMessageDelivery", "true" }
    }
});
```

### Idempotency

All resource creation operations are idempotent:
- Creating existing queue returns existing queue URL
- Creating existing topic returns existing topic ARN
- Subscribing existing subscription is a no-op

---

## Message Serialization

### JsonMessageSerializer

**Purpose**: Serialize/deserialize commands and events for AWS messaging

### Serialization Strategy

**Command Serialization**:
```json
{
  "Entity": {
    "Id": 123
  },
  "Payload": {
    "CustomerId": 456,
    "OrderDate": "2026-03-04T10:00:00Z"
  },
  "Metadata": {
    "SequenceNo": 1,
    "Timestamp": "2026-03-04T10:00:00Z",
    "CorrelationId": "abc123"
  }
}
```

**Message Attributes**:
- `CommandType`: Full assembly-qualified type name
- `EntityId`: Entity reference for FIFO ordering
- `SequenceNo`: Event sourcing sequence number

### Custom Converters

#### CommandPayloadConverter
**Purpose**: Handle polymorphic command payloads

**Strategy**: Serialize payload separately with type information

#### EntityConverter
**Purpose**: Serialize EntityRef objects

**Strategy**: Simple ID-based serialization

#### MetadataConverter
**Purpose**: Serialize command/event metadata

**Strategy**: Dictionary-based serialization with type preservation

---

## Security and Encryption

### AwsKmsMessageEncryption

**Purpose**: Encrypt sensitive message content using AWS KMS

### Encryption Flow

```
Plaintext Message
    ↓
Generate Data Key (KMS)
    ↓
Encrypt Message (Data Key)
    ↓
Encrypt Data Key (KMS Master Key)
    ↓
Store: Encrypted Message + Encrypted Data Key
```

### Decryption Flow

```
Retrieve: Encrypted Message + Encrypted Data Key
    ↓
Decrypt Data Key (KMS Master Key)
    ↓
Decrypt Message (Data Key)
    ↓
Plaintext Message
```

### Encryption Configuration

```csharp
services.UseSourceFlowAws(
    options =>
    {
        options.EnableEncryption = true;
        options.KmsKeyId = "alias/sourceflow-key";
    },
    bus => ...);
```

**Encryption applies to**:
- Command payloads
- Event payloads
- Message metadata (optional)

**Key Management**:
- Use KMS key aliases for easier rotation
- Enable automatic key rotation in KMS
- Use separate keys per environment

### IAM Permissions

**Minimum Required for Bootstrapper and Runtime**:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "SQSQueueManagement",
      "Effect": "Allow",
      "Action": [
        "sqs:CreateQueue",
        "sqs:GetQueueUrl",
        "sqs:GetQueueAttributes",
        "sqs:SetQueueAttributes",
        "sqs:TagQueue"
      ],
      "Resource": "arn:aws:sqs:*:*:*"
    },
    {
      "Sid": "SQSMessageOperations",
      "Effect": "Allow",
      "Action": [
        "sqs:ReceiveMessage",
        "sqs:SendMessage",
        "sqs:DeleteMessage",
        "sqs:ChangeMessageVisibility"
      ],
      "Resource": "arn:aws:sqs:*:*:*"
    },
    {
      "Sid": "SNSTopicManagement",
      "Effect": "Allow",
      "Action": [
        "sns:CreateTopic",
        "sns:GetTopicAttributes",
        "sns:SetTopicAttributes",
        "sns:TagResource"
      ],
      "Resource": "arn:aws:sns:*:*:*"
    },
    {
      "Sid": "SNSPublishAndSubscribe",
      "Effect": "Allow",
      "Action": [
        "sns:Subscribe",
        "sns:Unsubscribe",
        "sns:Publish"
      ],
      "Resource": "arn:aws:sns:*:*:*"
    },
    {
      "Sid": "STSGetCallerIdentity",
      "Effect": "Allow",
      "Action": [
        "sts:GetCallerIdentity"
      ],
      "Resource": "*"
    },
    {
      "Sid": "KMSEncryption",
      "Effect": "Allow",
      "Action": [
        "kms:Decrypt",
        "kms:Encrypt",
        "kms:GenerateDataKey",
        "kms:DescribeKey"
      ],
      "Resource": "arn:aws:kms:*:*:key/*"
    }
  ]
}
```

**Production Best Practice - Restrict to Specific Resources**:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "SQSSpecificQueues",
      "Effect": "Allow",
      "Action": [
        "sqs:CreateQueue",
        "sqs:GetQueueUrl",
        "sqs:GetQueueAttributes",
        "sqs:SetQueueAttributes",
        "sqs:TagQueue",
        "sqs:ReceiveMessage",
        "sqs:SendMessage",
        "sqs:DeleteMessage",
        "sqs:ChangeMessageVisibility"
      ],
      "Resource": [
        "arn:aws:sqs:us-east-1:123456789012:orders.fifo",
        "arn:aws:sqs:us-east-1:123456789012:payments.fifo",
        "arn:aws:sqs:us-east-1:123456789012:inventory.fifo"
      ]
    },
    {
      "Sid": "SNSSpecificTopics",
      "Effect": "Allow",
      "Action": [
        "sns:CreateTopic",
        "sns:GetTopicAttributes",
        "sns:SetTopicAttributes",
        "sns:TagResource",
        "sns:Subscribe",
        "sns:Unsubscribe",
        "sns:Publish"
      ],
      "Resource": [
        "arn:aws:sns:us-east-1:123456789012:order-events",
        "arn:aws:sns:us-east-1:123456789012:payment-events"
      ]
    },
    {
      "Sid": "STSGetCallerIdentity",
      "Effect": "Allow",
      "Action": [
        "sts:GetCallerIdentity"
      ],
      "Resource": "*"
    },
    {
      "Sid": "KMSSpecificKey",
      "Effect": "Allow",
      "Action": [
        "kms:Decrypt",
        "kms:Encrypt",
        "kms:GenerateDataKey",
        "kms:DescribeKey"
      ],
      "Resource": "arn:aws:kms:us-east-1:123456789012:key/12345678-1234-1234-1234-123456789012"
    }
  ]
}
```

---

## Observability and Monitoring

### AwsTelemetryExtensions

**Purpose**: AWS-specific metrics and tracing

### Metrics

**Command Dispatching**:
- `sourceflow.aws.command.dispatched` - Commands sent to SQS
- `sourceflow.aws.command.dispatch_duration` - Dispatch latency
- `sourceflow.aws.command.dispatch_error` - Dispatch failures

**Event Publishing**:
- `sourceflow.aws.event.published` - Events published to SNS
- `sourceflow.aws.event.publish_duration` - Publish latency
- `sourceflow.aws.event.publish_error` - Publish failures

**Message Processing**:
- `sourceflow.aws.message.received` - Messages received from SQS
- `sourceflow.aws.message.processed` - Messages successfully processed
- `sourceflow.aws.message.processing_duration` - Processing latency
- `sourceflow.aws.message.processing_error` - Processing failures

### Distributed Tracing

**Activity Source**: `SourceFlow.Cloud.AWS`

**Spans Created**:
- `AwsSqsCommandDispatcher.Dispatch` - Command dispatch to SQS
- `AwsSnsEventDispatcher.Dispatch` - Event publish to SNS
- `AwsSqsCommandListener.ProcessMessage` - Message processing

**Trace Context Propagation**:
- Correlation IDs passed via message attributes
- Parent span context preserved across service boundaries

### Health Checks

**AwsHealthCheck**:
- Validates SQS connectivity
- Validates SNS connectivity
- Validates KMS access (if encryption enabled)
- Checks queue/topic existence

---

## Performance Optimizations

### Connection Management

**SqsClientFactory**:
- Singleton AWS SDK clients
- Connection pooling
- Regional optimization

**SnsClientFactory**:
- Singleton AWS SDK clients
- Connection pooling
- Regional optimization

### Batch Processing

**SQS Batch Operations**:
- Receive up to 10 messages per request
- Delete messages in batches
- Reduces API calls and improves throughput

### Parallel Processing

**Concurrent Message Handling**:
```csharp
// Configurable concurrency
options.MaxConcurrentCalls = 10;

// Each message processed in parallel
await Task.WhenAll(messages.Select(ProcessMessage));
```

### Message Prefetching

**Long Polling**:
```csharp
// Wait up to 20 seconds for messages
WaitTimeSeconds = 20
```

**Benefits**:
- Reduces empty responses
- Lowers API costs
- Improves latency

---

## Architecture Diagrams

### Command Flow

```
┌─────────────┐
│   Client    │
└──────┬──────┘
       │ Publish Command
       ▼
┌─────────────────┐
│   CommandBus    │
└──────┬──────────┘
       │ Dispatch
       ▼
┌──────────────────────┐
│ AwsSqsCommand        │
│ Dispatcher           │
└──────┬───────────────┘
       │ SendMessage
       ▼
┌──────────────────────┐
│   SQS Queue          │
│   (orders.fifo)      │
└──────┬───────────────┘
       │ ReceiveMessage
       ▼
┌──────────────────────┐
│ AwsSqsCommand        │
│ Listener             │
└──────┬───────────────┘
       │ Publish (local)
       ▼
┌─────────────────┐
│   CommandBus    │
└──────┬──────────┘
       │ Dispatch
       ▼
┌─────────────────┐
│   Saga          │
└─────────────────┘
```

### Event Flow

```
┌─────────────┐
│    Saga     │
└──────┬──────┘
       │ PublishEvent
       ▼
┌─────────────────┐
│   EventQueue    │
└──────┬──────────┘
       │ Dispatch
       ▼
┌──────────────────────┐
│ AwsSnsEvent          │
│ Dispatcher           │
└──────┬───────────────┘
       │ Publish
       ▼
┌──────────────────────┐
│   SNS Topic          │
│   (order-events)     │
└──────┬───────────────┘
       │ Fan-out
       ▼
┌──────────────────────┐
│   SQS Queue          │
│   (orders.fifo)      │
└──────┬───────────────┘
       │ ReceiveMessage
       ▼
┌──────────────────────┐
│ AwsSqsCommand        │
│ Listener             │
└──────┬───────────────┘
       │ Enqueue (local)
       ▼
┌─────────────────┐
│   EventQueue    │
└──────┬──────────┘
       │ Dispatch
       ▼
┌─────────────────┐
│ Aggregate/View  │
└─────────────────┘
```

---

## Summary

The AWS Cloud Architecture provides:

✅ **Distributed Command Processing** - SQS-based command routing
✅ **Event Fan-Out** - SNS-based event publishing
✅ **Message Encryption** - KMS-based sensitive data protection
✅ **Idempotency** - Duplicate message detection
✅ **Auto-Provisioning** - Bootstrapper creates AWS resources
✅ **Type-Safe Configuration** - Fluent API for routing
✅ **Observability** - Metrics, tracing, and health checks
✅ **Performance** - Connection pooling, batching, parallel processing

**Key Design Principles**:
- Zero core modifications required
- Plugin architecture via ICommandDispatcher/IEventDispatcher
- Configuration over convention
- Fail-fast with clear error messages
- Production-ready with comprehensive testing

---

## Related Documentation

- [SourceFlow Core Architecture](./README.md)
- [Cloud Core Consolidation](./06-Cloud-Core-Consolidation.md)
- [AWS Cloud Extension Package](../SourceFlow.Cloud.AWS-README.md)
- [Cloud Integration Testing](../Cloud-Integration-Testing.md)
- [Cloud Message Idempotency Guide](../Cloud-Message-Idempotency-Guide.md)

---

**Document Version**: 1.0  
**Last Updated**: 2026-03-04  
**Status**: Complete

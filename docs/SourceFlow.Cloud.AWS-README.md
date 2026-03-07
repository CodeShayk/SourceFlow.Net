# SourceFlow.Cloud.AWS

**AWS cloud integration for distributed command and event processing**

[![NuGet](https://img.shields.io/nuget/v/SourceFlow.Cloud.AWS.svg)](https://www.nuget.org/packages/SourceFlow.Cloud.AWS/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

## Overview

SourceFlow.Cloud.AWS extends the SourceFlow.Net framework with AWS cloud services integration, enabling distributed command and event processing using Amazon SQS, SNS, and KMS. This package provides production-ready dispatchers, listeners, and configuration for building scalable, cloud-native event-sourced applications.

**Key Features:**
- 🚀 Amazon SQS command dispatching with FIFO support
- 📢 Amazon SNS event publishing with fan-out
- 🔐 AWS KMS message encryption for sensitive data
- ⚙️ Fluent bus configuration API
- 🔄 Automatic resource provisioning
- 📊 Built-in observability and health checks
- 🧪 LocalStack integration for local development

---

## Table of Contents

1. [Installation](#installation)
2. [Quick Start](#quick-start)
3. [Configuration](#configuration)
4. [AWS Services](#aws-services)
5. [Bus Configuration System](#bus-configuration-system)
6. [Message Encryption](#message-encryption)
7. [Idempotency](#idempotency)
8. [Local Development](#local-development)
9. [Monitoring](#monitoring)
10. [Best Practices](#best-practices)

---

## Installation

### NuGet Package

```bash
dotnet add package SourceFlow.Cloud.AWS
```

### Prerequisites

- SourceFlow >= 2.0.0
- AWS SDK for .NET
- .NET Standard 2.1, .NET 8.0, .NET 9.0, or .NET 10.0

---

## Quick Start

### Basic Setup

```csharp
using SourceFlow.Cloud.AWS;
using Amazon;

// Configure SourceFlow with AWS integration
services.UseSourceFlow();

services.UseSourceFlowAws(
    options =>
    {
        options.Region = RegionEndpoint.USEast1;
        options.MaxConcurrentCalls = 10;
    },
    bus => bus
        .Send
            .Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
            .Command<ProcessPaymentCommand>(q => q.Queue("payments.fifo"))
        .Raise
            .Event<OrderCreatedEvent>(t => t.Topic("order-events"))
            .Event<PaymentProcessedEvent>(t => t.Topic("payment-events"))
        .Listen.To
            .CommandQueue("orders.fifo")
            .CommandQueue("payments.fifo")
        .Subscribe.To
            .Topic("order-events")
            .Topic("payment-events"));
```

### What This Does

1. **Registers AWS dispatchers** for commands and events
2. **Configures routing** - which commands go to which queues
3. **Starts listeners** - polls SQS queues for messages
4. **Creates resources** - automatically provisions queues, topics, and subscriptions
5. **Enables idempotency** - prevents duplicate message processing

---

## Configuration

### Fluent Configuration (Recommended)

```csharp
services.UseSourceFlowAws(options =>
{
    // Required: AWS Region
    options.Region = RegionEndpoint.USEast1;
    
    // Optional: Enable/disable features
    options.EnableCommandRouting = true;
    options.EnableEventRouting = true;
    options.EnableCommandListener = true;
    options.EnableEventListener = true;
    
    // Optional: Concurrency
    options.MaxConcurrentCalls = 10;
    
    // Optional: Message encryption
    options.EnableEncryption = true;
    options.KmsKeyId = "alias/sourceflow-key";
});
```

### Configuration from appsettings.json

**appsettings.json**:

```json
{
  "SourceFlow": {
    "Aws": {
      "Region": "us-east-1",
      "MaxConcurrentCalls": 10,
      "EnableEncryption": true,
      "KmsKeyId": "alias/sourceflow-key"
    },
    "Bus": {
      "Commands": {
        "CreateOrderCommand": "orders.fifo",
        "UpdateOrderCommand": "orders.fifo",
        "ProcessPaymentCommand": "payments.fifo"
      },
      "Events": {
        "OrderCreatedEvent": "order-events",
        "OrderUpdatedEvent": "order-events",
        "PaymentProcessedEvent": "payment-events"
      },
      "ListenQueues": [
        "orders.fifo",
        "payments.fifo"
      ],
      "SubscribeTopics": [
        "order-events",
        "payment-events"
      ]
    }
  }
}
```

**Program.cs**:

```csharp
var configuration = builder.Configuration;

services.UseSourceFlowAws(
    options =>
    {
        var awsConfig = configuration.GetSection("SourceFlow:Aws");
        options.Region = RegionEndpoint.GetBySystemName(awsConfig["Region"]);
        options.MaxConcurrentCalls = awsConfig.GetValue<int>("MaxConcurrentCalls", 10);
        options.EnableEncryption = awsConfig.GetValue<bool>("EnableEncryption", false);
        options.KmsKeyId = awsConfig["KmsKeyId"];
    },
    bus =>
    {
        var busConfig = configuration.GetSection("SourceFlow:Bus");
        
        // Configure command routing from appsettings
        var commandsSection = busConfig.GetSection("Commands");
        var sendBuilder = bus.Send;
        foreach (var command in commandsSection.GetChildren())
        {
            var commandType = Type.GetType(command.Key);
            var queueName = command.Value;
            // Dynamic registration based on configuration
            sendBuilder.Command(commandType, q => q.Queue(queueName));
        }
        
        // Configure event routing from appsettings
        var eventsSection = busConfig.GetSection("Events");
        var raiseBuilder = bus.Raise;
        foreach (var evt in eventsSection.GetChildren())
        {
            var eventType = Type.GetType(evt.Key);
            var topicName = evt.Value;
            // Dynamic registration based on configuration
            raiseBuilder.Event(eventType, t => t.Topic(topicName));
        }
        
        // Configure listeners from appsettings
        var listenQueues = busConfig.GetSection("ListenQueues").Get<string[]>();
        var listenBuilder = bus.Listen.To;
        foreach (var queue in listenQueues)
        {
            listenBuilder.CommandQueue(queue);
        }
        
        // Configure subscriptions from appsettings
        var subscribeTopics = busConfig.GetSection("SubscribeTopics").Get<string[]>();
        var subscribeBuilder = bus.Subscribe.To;
        foreach (var topic in subscribeTopics)
        {
            subscribeBuilder.Topic(topic);
        }
        
        return bus;
    });
```

**Simplified Configuration Helper**:

```csharp
public static class AwsConfigurationExtensions
{
    public static IServiceCollection UseSourceFlowAwsFromConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        return services.UseSourceFlowAws(
            options => ConfigureAwsOptions(options, configuration),
            bus => ConfigureBusFromSettings(bus, configuration));
    }
    
    private static void ConfigureAwsOptions(AwsOptions options, IConfiguration configuration)
    {
        var awsConfig = configuration.GetSection("SourceFlow:Aws");
        options.Region = RegionEndpoint.GetBySystemName(awsConfig["Region"]);
        options.MaxConcurrentCalls = awsConfig.GetValue<int>("MaxConcurrentCalls", 10);
        options.EnableEncryption = awsConfig.GetValue<bool>("EnableEncryption", false);
        options.KmsKeyId = awsConfig["KmsKeyId"];
    }
    
    private static BusConfigurationBuilder ConfigureBusFromSettings(
        BusConfigurationBuilder bus,
        IConfiguration configuration)
    {
        var busConfig = configuration.GetSection("SourceFlow:Bus");
        
        // Commands
        var commands = busConfig.GetSection("Commands").Get<Dictionary<string, string>>();
        foreach (var (commandType, queueName) in commands)
        {
            bus.Send.Command(Type.GetType(commandType), q => q.Queue(queueName));
        }
        
        // Events
        var events = busConfig.GetSection("Events").Get<Dictionary<string, string>>();
        foreach (var (eventType, topicName) in events)
        {
            bus.Raise.Event(Type.GetType(eventType), t => t.Topic(topicName));
        }
        
        // Listen queues
        var listenQueues = busConfig.GetSection("ListenQueues").Get<string[]>();
        foreach (var queue in listenQueues)
        {
            bus.Listen.To.CommandQueue(queue);
        }
        
        // Subscribe topics
        var subscribeTopics = busConfig.GetSection("SubscribeTopics").Get<string[]>();
        foreach (var topic in subscribeTopics)
        {
            bus.Subscribe.To.Topic(topic);
        }
        
        return bus;
    }
}

// Usage
services.UseSourceFlowAwsFromConfiguration(configuration);
```

### Configuration Options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `Region` | `RegionEndpoint` | Required | AWS region for services |
| `EnableCommandRouting` | `bool` | `true` | Enable command dispatching to SQS |
| `EnableEventRouting` | `bool` | `true` | Enable event publishing to SNS |
| `EnableCommandListener` | `bool` | `true` | Enable SQS command listener |
| `EnableEventListener` | `bool` | `true` | Enable SNS event listener |
| `MaxConcurrentCalls` | `int` | `10` | Concurrent message processing |
| `EnableEncryption` | `bool` | `false` | Enable KMS encryption |
| `KmsKeyId` | `string` | `null` | KMS key ID or alias |

---

## AWS Services

### Amazon SQS (Simple Queue Service)

**Purpose**: Command dispatching and queuing

#### Standard Queues

```csharp
.Send.Command<SendEmailCommand>(q => q.Queue("notifications"))
```

**Characteristics**:
- High throughput (unlimited TPS)
- At-least-once delivery
- Best-effort ordering
- Use for independent operations

#### FIFO Queues

```csharp
.Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
```

**Characteristics**:
- Exactly-once processing
- Strict ordering per entity
- Content-based deduplication
- Use for ordered operations

**FIFO Configuration**:
- Queue name must end with `.fifo`
- `MessageGroupId` set to entity ID
- `MessageDeduplicationId` generated from content
- Maximum 300 TPS per message group

### Amazon SNS (Simple Notification Service)

**Purpose**: Event publishing and fan-out

```csharp
.Raise.Event<OrderCreatedEvent>(t => t.Topic("order-events"))
```

**Characteristics**:
- Publish-subscribe pattern
- Fan-out to multiple subscribers
- Topic-to-queue subscriptions
- Message filtering (future)

**How It Works**:
```
Event Published
    ↓
SNS Topic (order-events)
    ↓
Fan-out to Subscribers
    ↓
SQS Queue (orders.fifo)
    ↓
Command Listener
```

### AWS KMS (Key Management Service)

**Purpose**: Message encryption for sensitive data

```csharp
services.UseSourceFlowAws(
    options =>
    {
        options.EnableEncryption = true;
        options.KmsKeyId = "alias/sourceflow-key";
    },
    bus => ...);
```

**Encryption Flow**:
1. Generate data key from KMS
2. Encrypt message with data key
3. Encrypt data key with KMS master key
4. Store encrypted message + encrypted data key

---

## Bus Configuration System

### Fluent API

The bus configuration system provides a type-safe, intuitive way to configure message routing.

#### Send Commands

```csharp
.Send
    .Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
    .Command<UpdateOrderCommand>(q => q.Queue("orders.fifo"))
    .Command<CancelOrderCommand>(q => q.Queue("orders.fifo"))
```

#### Raise Events

```csharp
.Raise
    .Event<OrderCreatedEvent>(t => t.Topic("order-events"))
    .Event<OrderUpdatedEvent>(t => t.Topic("order-events"))
    .Event<OrderCancelledEvent>(t => t.Topic("order-events"))
```

#### Listen to Command Queues

```csharp
.Listen.To
    .CommandQueue("orders.fifo")
    .CommandQueue("inventory.fifo")
    .CommandQueue("payments.fifo")
```

#### Subscribe to Event Topics

```csharp
.Subscribe.To
    .Topic("order-events")
    .Topic("payment-events")
    .Topic("inventory-events")
```

### Short Name Resolution

**Configuration**: Provide short names only

```csharp
.Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
```

**Resolved at Startup**:
- Short name: `"orders.fifo"`
- Resolved URL: `https://sqs.us-east-1.amazonaws.com/123456789012/orders.fifo`

**Benefits**:
- No hardcoded account IDs
- Portable across environments
- Easier to read and maintain

### Resource Provisioning

The `AwsBusBootstrapper` automatically creates missing AWS resources at startup:

**SQS Queues**:
```csharp
// Standard queue
CreateQueueRequest {
    QueueName = "notifications",
    Attributes = {
        { "MessageRetentionPeriod", "1209600" }, // 14 days
        { "VisibilityTimeout", "30" }
    }
}

// FIFO queue (detected by .fifo suffix)
CreateQueueRequest {
    QueueName = "orders.fifo",
    Attributes = {
        { "FifoQueue", "true" },
        { "ContentBasedDeduplication", "true" },
        { "MessageRetentionPeriod", "1209600" },
        { "VisibilityTimeout", "30" }
    }
}
```

**SNS Topics**:
```csharp
CreateTopicRequest {
    Name = "order-events",
    Attributes = {
        { "DisplayName", "Order Events Topic" }
    }
}
```

**SNS Subscriptions**:
```csharp
// Subscribe queue to topic
SubscribeRequest {
    TopicArn = "arn:aws:sns:us-east-1:123456789012:order-events",
    Protocol = "sqs",
    Endpoint = "arn:aws:sqs:us-east-1:123456789012:orders.fifo",
    Attributes = {
        { "RawMessageDelivery", "true" }
    }
}
```

**Idempotency**: All operations are idempotent - safe to run multiple times.

---

## Message Encryption

### KMS Configuration

Enable message encryption for sensitive data using AWS KMS:

```csharp
services.UseSourceFlowAws(
    options =>
    {
        options.EnableEncryption = true;
        options.KmsKeyId = "alias/sourceflow-key";  // or key ID
    },
    bus => ...);
```

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

### KMS Key Setup

**Create KMS Key**:

```bash
aws kms create-key \
  --description "SourceFlow message encryption key" \
  --key-usage ENCRYPT_DECRYPT

aws kms create-alias \
  --alias-name alias/sourceflow-key \
  --target-key-id <key-id>
```

**Key Policy**:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "Enable IAM User Permissions",
      "Effect": "Allow",
      "Principal": {
        "AWS": "arn:aws:iam::123456789012:root"
      },
      "Action": "kms:*",
      "Resource": "*"
    },
    {
      "Sid": "Allow SourceFlow Application",
      "Effect": "Allow",
      "Principal": {
        "AWS": "arn:aws:iam::123456789012:role/SourceFlowApplicationRole"
      },
      "Action": [
        "kms:Decrypt",
        "kms:Encrypt",
        "kms:GenerateDataKey",
        "kms:DescribeKey"
      ],
      "Resource": "*"
    }
  ]
}
```

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

**Production Best Practice - Restrict Resources**:

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
        "sqs:TagQueue",
        "sqs:ReceiveMessage",
        "sqs:SendMessage",
        "sqs:DeleteMessage",
        "sqs:ChangeMessageVisibility"
      ],
      "Resource": [
        "arn:aws:sqs:us-east-1:123456789012:orders.fifo",
        "arn:aws:sqs:us-east-1:123456789012:payments.fifo",
        "arn:aws:sqs:us-east-1:123456789012:notifications"
      ]
    },
    {
      "Sid": "SNSTopicManagement",
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
      "Sid": "KMSEncryption",
      "Effect": "Allow",
      "Action": [
        "kms:Decrypt",
        "kms:Encrypt",
        "kms:GenerateDataKey",
        "kms:DescribeKey"
      ],
      "Resource": "arn:aws:kms:us-east-1:123456789012:key/your-key-id"
    }
  ]
}
```

---

## Idempotency

### Default (In-Memory)

Automatically registered for single-instance deployments:

```csharp
services.UseSourceFlowAws(
    options => { options.Region = RegionEndpoint.USEast1; },
    bus => ...);
// InMemoryIdempotencyService registered automatically
```

### Multi-Instance (SQL-Based)

For production deployments with multiple instances:

```csharp
// Install package
// dotnet add package SourceFlow.Stores.EntityFramework

// Register SQL-based idempotency
services.AddSourceFlowIdempotency(
    connectionString: "Server=...;Database=...;",
    cleanupIntervalMinutes: 60);

// Configure AWS
services.UseSourceFlowAws(
    options => { options.Region = RegionEndpoint.USEast1; },
    bus => ...);
```

**See**: [Cloud Message Idempotency Guide](Cloud-Message-Idempotency-Guide.md) for detailed configuration.

---

## Local Development

### LocalStack Integration

LocalStack provides local AWS service emulation for development and testing.

#### Setup

```bash
# Install LocalStack
pip install localstack

# Start LocalStack
localstack start
```

#### Configuration

```csharp
services.UseSourceFlowAws(
    options =>
    {
        options.Region = RegionEndpoint.USEast1;
        
        // LocalStack endpoints
        options.ServiceURL = "http://localhost:4566";
    },
    bus => bus
        .Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
        .Listen.To.CommandQueue("orders.fifo"));
```

#### Environment Variables

```bash
# LocalStack endpoints
export AWS_ENDPOINT_URL=http://localhost:4566

# LocalStack uses hardcoded test credentials in test fixtures
# BasicAWSCredentials("test", "test") provides better endpoint compatibility
export AWS_DEFAULT_REGION=us-east-1
```

**Note**: LocalStack does not validate AWS credentials. The test infrastructure uses `BasicAWSCredentials` with dummy "test"/"test" values for better compatibility with AWS SDK endpoint resolution. This approach avoids endpoint override issues that can occur with `AnonymousAWSCredentials`.

#### Testing

```csharp
[Trait("Category", "Integration")]
[Trait("Category", "RequiresLocalStack")]
public class AwsIntegrationTests : LocalStackRequiredTestBase
{
    [Fact]
    public async Task Should_Process_Command_Through_SQS()
    {
        // Test implementation
    }
}
```

**Run Tests**:
```bash
# Unit tests only
dotnet test --filter "Category=Unit"

# Integration tests with LocalStack
dotnet test --filter "Category=Integration&Category=RequiresLocalStack"
```

---

## Monitoring

### Health Checks

```csharp
services.AddHealthChecks()
    .AddCheck<AwsHealthCheck>("aws");
```

**Checks**:
- SQS connectivity
- SNS connectivity
- KMS access (if encryption enabled)
- Queue/topic existence

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

**Spans**:
- `AwsSqsCommandDispatcher.Dispatch`
- `AwsSnsEventDispatcher.Dispatch`
- `AwsSqsCommandListener.ProcessMessage`

**Trace Context**: Propagated via message attributes

---

## Best Practices

### Queue Design

1. **Use FIFO queues for ordered operations**
   ```csharp
   .Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
   ```

2. **Use standard queues for independent operations**
   ```csharp
   .Send.Command<SendEmailCommand>(q => q.Queue("notifications"))
   ```

3. **Group related commands to the same queue**
   ```csharp
   .Send
       .Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
       .Command<UpdateOrderCommand>(q => q.Queue("orders.fifo"))
       .Command<CancelOrderCommand>(q => q.Queue("orders.fifo"))
   ```

### IAM Permissions

**Development Environment (Broad Permissions)**:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "SQSFullAccess",
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
      "Resource": "arn:aws:sqs:*:*:*"
    },
    {
      "Sid": "SNSFullAccess",
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
      "Resource": "arn:aws:sns:*:*:*"
    },
    {
      "Sid": "STSGetCallerIdentity",
      "Effect": "Allow",
      "Action": [
        "sts:GetCallerIdentity"
      ],
      "Resource": "*"
    }
  ]
}
```

**Production Environment (Restricted Resources)**:

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
        "arn:aws:sqs:us-east-1:123456789012:inventory.fifo",
        "arn:aws:sqs:us-east-1:123456789012:notifications"
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
        "arn:aws:sns:us-east-1:123456789012:payment-events",
        "arn:aws:sns:us-east-1:123456789012:inventory-events"
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

**Explanation of Permissions**:

| Permission | Purpose | Required For |
|------------|---------|--------------|
| `sqs:CreateQueue` | Create queues during bootstrapping | Bootstrapper |
| `sqs:GetQueueUrl` | Resolve queue names to URLs | Bootstrapper, Dispatchers |
| `sqs:GetQueueAttributes` | Verify queue configuration | Bootstrapper |
| `sqs:SetQueueAttributes` | Configure queue settings | Bootstrapper |
| `sqs:TagQueue` | Add tags to queues | Bootstrapper (optional) |
| `sqs:ReceiveMessage` | Poll messages from queues | Listeners |
| `sqs:SendMessage` | Send commands to queues | Dispatchers |
| `sqs:DeleteMessage` | Remove processed messages | Listeners |
| `sqs:ChangeMessageVisibility` | Extend processing time | Listeners |
| `sns:CreateTopic` | Create topics during bootstrapping | Bootstrapper |
| `sns:GetTopicAttributes` | Verify topic configuration | Bootstrapper |
| `sns:SetTopicAttributes` | Configure topic settings | Bootstrapper |
| `sns:TagResource` | Add tags to topics | Bootstrapper (optional) |
| `sns:Subscribe` | Subscribe queues to topics | Bootstrapper |
| `sns:Unsubscribe` | Remove subscriptions | Bootstrapper (cleanup) |
| `sns:Publish` | Publish events to topics | Dispatchers |
| `sts:GetCallerIdentity` | Get AWS account ID | Bootstrapper |
| `kms:Decrypt` | Decrypt messages | Listeners (if encryption enabled) |
| `kms:Encrypt` | Encrypt messages | Dispatchers (if encryption enabled) |
| `kms:GenerateDataKey` | Generate encryption keys | Dispatchers (if encryption enabled) |
| `kms:DescribeKey` | Verify key configuration | Bootstrapper (if encryption enabled) |

### Production Deployment

1. **Use SQL-based idempotency**
   ```csharp
   services.AddSourceFlowIdempotency(connectionString);
   ```

2. **Enable encryption for sensitive data**
   ```csharp
   options.EnableEncryption = true;
   options.KmsKeyId = "alias/sourceflow-key";
   ```

3. **Configure appropriate concurrency**
   ```csharp
   options.MaxConcurrentCalls = 10;  // Adjust based on load
   ```

4. **Use infrastructure as code**
   - CloudFormation or Terraform for production
   - Let bootstrapper create resources in development

5. **Monitor metrics and health checks**
   ```csharp
   services.AddHealthChecks().AddCheck<AwsHealthCheck>("aws");
   ```

### Error Handling

1. **Configure dead letter queues**
   - Automatic for all queues
   - Review failed messages regularly

2. **Implement retry policies**
   - SQS visibility timeout for retries
   - Exponential backoff built-in

3. **Monitor processing errors**
   - Track `sourceflow.aws.message.processing_error`
   - Alert on high error rates

---

## Architecture

### Command Flow

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

### Event Flow

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

---

## Related Documentation

- [SourceFlow Core](SourceFlow.Net-README.md)
- [AWS Cloud Architecture](Architecture/07-AWS-Cloud-Architecture.md)
- [Cloud Message Idempotency Guide](Cloud-Message-Idempotency-Guide.md)
- [Cloud Integration Testing](Cloud-Integration-Testing.md)
- [Entity Framework Stores](SourceFlow.Stores.EntityFramework-README.md)

---

## Support

- **Documentation**: [GitHub Wiki](https://github.com/sourceflow/sourceflow.net/wiki)
- **Issues**: [GitHub Issues](https://github.com/sourceflow/sourceflow.net/issues)
- **Discussions**: [GitHub Discussions](https://github.com/sourceflow/sourceflow.net/discussions)

---

## License

MIT License - see [LICENSE](../LICENSE) file for details.

---

**Package Version**: 2.0.0  
**Last Updated**: 2026-03-04  
**Status**: Production Ready

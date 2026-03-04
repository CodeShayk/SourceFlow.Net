# SourceFlow.Cloud.AWS

AWS Cloud Extension for SourceFlow.Net provides integration with AWS SQS (Simple Queue Service) and SNS (Simple Notification Service) for cloud-based message processing.

## Features

- **AWS SQS Integration**: Send and receive commands via SQS queues
- **AWS SNS Integration**: Publish and subscribe to events via SNS topics
- **Selective Routing**: Route specific commands/events to AWS while keeping others local
- **FIFO Ordering**: Support for message ordering using SQS FIFO queues
- **Configuration-based Routing**: Define routing rules in appsettings.json
- **Attribute-based Routing**: Use attributes to define routing for specific types
- **Health Checks**: Built-in health checks for AWS connectivity
- **Telemetry**: Comprehensive logging and error handling

## Installation

```bash
dotnet add package SourceFlow.Cloud.AWS
```

## Configuration

### Basic Setup with In-Memory Idempotency (Single Instance)

For single-instance deployments, the default in-memory idempotency service is automatically registered:

```csharp
services.UseSourceFlow(); // Existing registration

services.UseSourceFlowAws(
    options =>
    {
        options.Region = RegionEndpoint.USEast1;
    },
    bus => bus
        .Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
        .Raise.Event<OrderCreatedEvent>(t => t.Topic("order-events"))
        .Listen.To.CommandQueue("orders.fifo")
        .Subscribe.To.Topic("order-events"));
```

### Multi-Instance Deployment with SQL-Based Idempotency

For multi-instance deployments, use the Entity Framework-based idempotency service to ensure duplicate detection across all instances:

```csharp
services.UseSourceFlow(); // Existing registration

// Register Entity Framework stores and SQL-based idempotency
services.AddSourceFlowEfStores(connectionString);
services.AddSourceFlowIdempotency(
    connectionString: connectionString,
    cleanupIntervalMinutes: 60);

// Configure AWS with the registered idempotency service
services.UseSourceFlowAws(
    options =>
    {
        options.Region = RegionEndpoint.USEast1;
    },
    bus => bus
        .Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
        .Raise.Event<OrderCreatedEvent>(t => t.Topic("order-events"))
        .Listen.To.CommandQueue("orders.fifo")
        .Subscribe.To.Topic("order-events"));
```

**Note**: The SQL-based idempotency service requires the `SourceFlow.Stores.EntityFramework` package:

```bash
dotnet add package SourceFlow.Stores.EntityFramework
```

### Custom Idempotency Service

You can also provide a custom idempotency implementation:

```csharp
services.UseSourceFlowAws(
    options => { options.Region = RegionEndpoint.USEast1; },
    bus => bus.Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo")),
    configureIdempotency: services =>
    {
        services.AddScoped<IIdempotencyService, MyCustomIdempotencyService>();
    });
```

### appsettings.json

```json
{
  "SourceFlow": {
    "Aws": {
      "Commands": {
        "DefaultRouting": "Local",
        "Routes": [
          {
            "CommandType": "MyApp.Commands.CreateOrderCommand",
            "QueueUrl": "https://sqs.us-east-1.amazonaws.com/123456/order-commands.fifo",
            "RouteToAws": true
          }
        ],
        "ListeningQueues": [
          "https://sqs.us-east-1.amazonaws.com/123456/order-commands.fifo"
        ]
      },
      "Events": {
        "DefaultRouting": "Local",
        "Routes": [
          {
            "EventType": "MyApp.Events.OrderCreatedEvent",
            "TopicArn": "arn:aws:sns:us-east-1:123456:order-events",
            "RouteToAws": true
          }
        ],
        "ListeningQueues": [
          "https://sqs.us-east-1.amazonaws.com/123456/order-events-subscriber"
        ]
      }
    }
  }
}
```

### Program.cs (or Startup.cs)

```csharp
// Register SourceFlow with AWS extension
services.UseSourceFlow(); // Existing registration

services.UseSourceFlowAws(options =>
{
    options.Region = RegionEndpoint.USEast1;
    options.EnableCommandRouting = true;
    options.EnableEventRouting = true;
    options.SqsReceiveWaitTimeSeconds = 20;
    options.SqsVisibilityTimeoutSeconds = 300;
});
```

## Usage

### Attribute-based Routing

```csharp
[AwsCommandRouting(QueueUrl = "https://sqs.us-east-1.amazonaws.com/123456/order-commands.fifo")]
public class CreateOrderCommand : Command<CreateOrderCommandData>
{
    // ...
}

[AwsEventRouting(TopicArn = "arn:aws:sns:us-east-1:123456:order-events")]
public class OrderCreatedEvent : Event<OrderCreatedEventData>
{
    // ...
}
```

### Selective Command Processing

Commands can be processed both locally and in AWS by registering multiple dispatchers:

```csharp
// Command will be sent to both local and AWS dispatchers
await commandBus.Dispatch(new CreateOrderCommand(orderData));
```

### Event Publishing

Events are similarly dispatched to both local and AWS endpoints:

```csharp
// Event will be published to both local and AWS event queues
await eventQueue.Publish(new OrderCreatedEvent(orderData));
```

## Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                         Client Application                          │
└────────────────┬───────────────────────────────┬────────────────────┘
                 │                               │
                 ▼                               ▼
      ┌─────────────────────┐        ┌─────────────────────┐
      │   ICommandBus       │        │   IEventQueue       │
      └──────────┬──────────┘        └──────────┬──────────┘
                 │                               │
                 ▼                               ▼
      ┌─────────────────────┐        ┌─────────────────────┐
      │ ICommandDispatcher[]│        │ IEventDispatcher[]  │
      ├─────────────────────┤        ├─────────────────────┤
      │ • CommandDispatcher │        │ • EventDispatcher   │
      │   (local)           │        │   (local)           │
      │ • AwsSqsCommand-    │        │ • AwsSnsEvent-      │
      │   Dispatcher        │        │   Dispatcher        │
      └──────────┬──────────┘        └──────────┬──────────┘
                 │                               │
                 │ Selective                     │ Selective
                 │ (based on                     │ (based on
                 │  attributes/                  │  attributes/
                 │  config)                      │  config)
                 │                               │
         ┌───────┴────────┐              ┌──────┴─────────┐
         ▼                ▼              ▼                ▼
    ┌────────┐      ┌──────────┐   ┌────────┐      ┌──────────┐
    │ Local  │      │ AWS SQS  │   │ Local  │      │ AWS SNS  │
    │ Sagas  │      │ Queue    │   │ Subs   │      │ Topic    │
    └────────┘      └─────┬────┘   └────────┘      └─────┬────┘
                          │                              │
                    ┌─────▼────────┐              ┌──────▼─────┐
                    │ AwsSqsCommand│              │ AWS SQS    │
                    │ Listener     │              │ Queue      │
                    │              │              │ (SNS->SQS) │
                    └──────┬───────┘              └──────┬─────┘
                           │                             │
                           │                      ┌──────▼────────┐
                           │                      │ AwsSnsEvent   │
                           │                      │ Listener      │
                           │                      └──────┬────────┘
                           │                             │
                           ▼                             ▼
                  ┌─────────────────┐         ┌─────────────────┐
                  │ ICommandSub-    │         │ IEventSub-      │
                  │ scriber         │         │ scriber         │
                  │ (existing)      │         │ (existing)      │
                  └─────────────────┘         └─────────────────┘
```

## Requirements

- .NET 8.0 or higher
- AWS account with appropriate permissions for SQS and SNS
- IAM permissions for SQS and SNS operations (see below)

### IAM Permissions

Your application needs the following IAM permissions:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": [
        "sqs:SendMessage",
        "sqs:ReceiveMessage",
        "sqs:DeleteMessage",
        "sqs:GetQueueUrl",
        "sqs:GetQueueAttributes"
      ],
      "Resource": "arn:aws:sqs:*:*:sourceflow-*"
    },
    {
      "Effect": "Allow",
      "Action": [
        "sns:Publish"
      ],
      "Resource": "arn:aws:sns:*:*:sourceflow-*"
    }
  ]
}
```

## Error Handling and Resilience

- **Retry Logic**: Automatic retry with exponential backoff for transient failures
- **Dead Letter Queues**: Failed messages are moved to DLQ after max retry attempts
- **Health Checks**: Monitor AWS service connectivity and queue accessibility
- **Circuit Breaker**: Optional pattern to fail fast when AWS services are unavailable

## Security

- Authentication via AWS SDK default credential chain (no hardcoded credentials)
- HTTPS encryption for all communications
- Optional KMS encryption for messages at rest

## Performance Optimizations

- Connection pooling for AWS clients
- Message batching for improved throughput
- Efficient JSON serialization with custom converters
- Async/await patterns throughout for non-blocking operations

## Contributing

Please read [CONTRIBUTING.md](../../CONTRIBUTING.md) for details on our code of conduct, and the process for submitting pull requests to us.

## License

This project is licensed under the MIT License - see the [LICENSE](../../LICENSE) file for details.
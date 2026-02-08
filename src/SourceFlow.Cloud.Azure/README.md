# SourceFlow Cloud Azure Extension

This package provides Azure Service Bus integration for SourceFlow.Net, enabling cloud-based message processing while maintaining backward compatibility with the existing in-process architecture.

## Overview

The Azure Cloud Extension allows you to:
- Send commands to Azure Service Bus queues using sessions for ordering
- Subscribe to commands from Azure Service Bus queues
- Publish events to Azure Service Bus topics
- Subscribe to events from Azure Service Bus topic subscriptions
- Selective routing per command/event type
- JSON serialization for messages

## Installation

Install the NuGet package:

```bash
dotnet add package SourceFlow.Cloud.Azure
```

## Configuration

### Azure Service Bus Setup

Create Azure Service Bus resources with the following settings:
- **Queues**: Enable sessions for FIFO ordering per entity
- **Topics**: For event pub/sub pattern
- **Subscriptions**: For different services to subscribe to topics

### App Settings Configuration

```json
{
  "SourceFlow": {
    "Azure": {
      "ServiceBus": {
        "ConnectionString": "Endpoint=sb://namespace.servicebus.windows.net/;..."
      },
      "Commands": {
        "DefaultRouting": "Local",
        "Routes": [
          {
            "CommandType": "MyApp.Commands.CreateOrderCommand",
            "QueueName": "order-commands",
            "RouteToAzure": true
          }
        ],
        "ListeningQueues": [
          "order-commands",
          "payment-commands"
        ]
      },
      "Events": {
        "DefaultRouting": "Both",
        "Routes": [
          {
            "EventType": "MyApp.Events.OrderCreatedEvent",
            "TopicName": "order-events",
            "RouteToAzure": true
          }
        ],
        "ListeningSubscriptions": [
          {
            "TopicName": "order-events",
            "SubscriptionName": "order-processor"
          }
        ]
      }
    }
  }
}
```

### Service Registration

Register the Azure extension in your DI container:

```csharp
services.UseSourceFlow(); // Existing registration

services.UseSourceFlowAzure(options =>
{
    options.ServiceBusConnectionString = configuration["Azure:ServiceBus:ConnectionString"];
    options.EnableCommandRouting = true;
    options.EnableEventRouting = true;
    options.EnableCommandListener = true;
    options.EnableEventListener = true;
});
```

### Attribute-Based Routing

You can also use attributes to define routing:

```csharp
[AzureCommandRouting(QueueName = "order-commands", RequireSession = true)]
public class CreateOrderCommand : Command<CreateOrderCommandData>
{
    // ...
}

[AzureEventRouting(TopicName = "order-events")]
public class OrderCreatedEvent : Event<OrderCreatedEventData>
{
    // ...
}
```

## Features

- **Azure Service Bus Queues**: For command queuing with session-based FIFO ordering
- **Azure Service Bus Topics**: For event pub/sub with subscription filtering
- **Selective routing**: Per command/event type routing (same as AWS pattern)
- **JSON serialization**: For messages
- **Command Listener**: Receives from Service Bus queues and routes to Sagas
- **Event Listener**: Receives from Service Bus topics and routes to Aggregates/Views
- **Session Support**: Maintains ordering per entity using Service Bus sessions
- **Health Checks**: Built-in health checks for Azure Service Bus connectivity
- **Telemetry**: Comprehensive metrics and tracing with OpenTelemetry

## Architecture

The extension maintains the same architecture as the core SourceFlow but adds cloud dispatchers and listeners:

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
      │ • AzureServiceBus-  │        │ • AzureServiceBus-  │
      │   CommandDispatcher │        │   EventDispatcher   │
      └──────────┬──────────┘        └──────────┬──────────┘
                 │                               │
                 │ Selective                     │ Selective
                 │ (based on                     │ (based on
                 │  attributes/                  │  attributes/
                 │  config)                      │  config)
                 │                               │
         ┌───────┴────────┐              ┌──────┴─────────┐
         ▼                ▼              ▼                ▼
    ┌────────┐      ┌──────────────┐ ┌────────┐    ┌────────────────┐
    │ Local  │      │ Azure Service│ │ Local  │    │ Azure Service  │
    │ Sagas  │      │ Bus Queue    │ │ Subs   │    │ Bus Topic      │
    └────────┘      └─────┬────────┘ └────────┘    └─────┬──────────┘
                          │                              │
                    ┌─────▼──────────────┐        ┌──────▼────────────┐
                    │ AzureServiceBus    │        │ Azure Service Bus │
                    │ CommandListener    │        │ Topic Subscription│
                    └──────┬─────────────┘               │
                           │                      ┌──────▼──────────┐
                           │                      │ AzureServiceBus │
                           │                      │ EventListener   │
                           │                      └──────┬──────────┘
                           │                             │
                           ▼                             ▼
                  ┌─────────────────┐         ┌─────────────────┐
                  │ ICommandSub-    │         │ IEventSub-      │
                  │ scriber         │         │ scriber         │
                  │ (existing)      │         │ (existing)      │
                  └─────────────────┘         └─────────────────┘
```

## Security

For production scenarios, use Managed Identity instead of connection strings:

```csharp
services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var fullyQualifiedNamespace = config["SourceFlow:Azure:ServiceBus:Namespace"];

    return new ServiceBusClient(
        fullyQualifiedNamespace,
        new DefaultAzureCredential(),
        new ServiceBusClientOptions
        {
            RetryOptions = new ServiceBusRetryOptions
            {
                Mode = ServiceBusRetryMode.Exponential,
                MaxRetries = 3
            }
        });
});
```

Assign appropriate RBAC roles:
- **Azure Service Bus Data Sender**: For dispatchers
- **Azure Service Bus Data Receiver**: For listeners
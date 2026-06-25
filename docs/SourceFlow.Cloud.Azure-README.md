# SourceFlow.Cloud.Azure

**Azure cloud integration for distributed command and event processing**

[![NuGet](https://img.shields.io/nuget/v/SourceFlow.Cloud.Azure.svg)](https://www.nuget.org/packages/SourceFlow.Cloud.Azure/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

## Overview

SourceFlow.Cloud.Azure extends the SourceFlow.Net framework with Azure cloud services integration, enabling distributed command and event processing using Azure Service Bus and Azure Key Vault. This package provides production-ready dispatchers, listeners, and configuration for building scalable, cloud-native event-sourced applications. The fluent bus API is identical to the AWS provider — only the backing services change.

**Key Features:**
- 🚀 Azure Service Bus command dispatching with session-based ordering
- 📢 Azure Service Bus topic/subscription event publishing with fan-out
- 🔐 Azure Key Vault envelope encryption for sensitive data
- ⚙️ Fluent bus configuration API
- 🔄 Automatic resource provisioning (queues, topics, subscriptions)
- 📊 Built-in observability and health checks
- 🧪 Service Bus emulator integration for local development

---

## Table of Contents

1. [Installation](#installation)
2. [Quick Start](#quick-start)
3. [Configuration](#configuration)
4. [Azure Services](#azure-services)
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
dotnet add package SourceFlow.Cloud.Azure
```

### Prerequisites

- SourceFlow >= 2.0.0
- Azure SDK for .NET (Service Bus, Identity, Key Vault)
- .NET 8.0, .NET 9.0, or .NET 10.0

---

## Quick Start

```csharp
using SourceFlow.Cloud.Azure;

// Register SourceFlow core
services.UseSourceFlow(typeof(Program).Assembly);

// Configure Azure cloud messaging
services.UseSourceFlowAzure(
    options =>
    {
        options.ServiceBusConnectionString = configuration["Azure:ServiceBus:ConnectionString"];
    },
    bus => bus
        .Send
            .Command<CreateOrderCommand>(q => q.Queue("orders"))
            .Command<ProcessPaymentCommand>(q => q.Queue("payments"))
        .Raise
            .Event<OrderCreatedEvent>(t => t.Topic("order-events"))
            .Event<PaymentProcessedEvent>(t => t.Topic("payment-events"))
        .Listen.To
            .CommandQueue("orders")
            .CommandQueue("payments")
        .Subscribe.To
            .Topic("order-events")
            .Topic("payment-events"));
```

This registers Azure dispatchers, configures routing, starts Service Bus listeners, and automatically provisions queues/topics/subscriptions at startup.

### Passwordless authentication

Instead of a connection string, set `SourceFlow:Azure:ServiceBus:FullyQualifiedNamespace`
(e.g. `myns.servicebus.windows.net`) to authenticate with `DefaultAzureCredential`
(Managed Identity, Azure CLI, Visual Studio, etc.).

---

## Configuration

Connection settings are read from configuration when not supplied via options:

| Key | Description |
| --- | --- |
| `SourceFlow:Azure:ServiceBus:ConnectionString` | Service Bus connection string |
| `SourceFlow:Azure:ServiceBus:FullyQualifiedNamespace` | Namespace for Managed Identity auth |

| Option | Type | Default | Description |
| --- | --- | --- | --- |
| `ServiceBusConnectionString` | string | null | Service Bus connection string |
| `EnableCommandRouting` | bool | true | Enable command dispatching to queues |
| `EnableEventRouting` | bool | true | Enable event publishing to topics |
| `EnableCommandListener` | bool | true | Enable queue command processors |
| `EnableEventListener` | bool | true | Enable topic subscription processors |

---

## Azure Services

- **Azure Service Bus queues** — command dispatching with `SessionId` (entity id) for
  strict FIFO ordering per entity, optional duplicate detection, and dead-letter queues.
- **Azure Service Bus topics/subscriptions** — event publishing with fan-out to multiple
  subscriptions; subscriptions forward to the listening command queue.
- **Azure Key Vault** — envelope encryption keys for message payload protection.

---

## Bus Configuration System

The fluent `BusConfigurationBuilder` is shared with the rest of SourceFlow.Net:

```csharp
bus => bus
    .Send.Command<CreateOrderCommand>(q => q.Queue("orders"))
    .Raise.Event<OrderCreatedEvent>(t => t.Topic("order-events"))
    .Listen.To.CommandQueue("orders")
    .Subscribe.To.Topic("order-events");
```

---

## Message Encryption

Enable envelope encryption for sensitive message payloads backed by Azure Key Vault:

```csharp
services.AddSingleton<IMessageEncryption>(sp =>
    new AzureKeyVaultMessageEncryption(
        keyVaultUrl: "https://my-vault.vault.azure.net/",
        keyName:     "sourceflow-key",
        credential:  new DefaultAzureCredential()));

services.UseSourceFlowAzure(options => ..., bus => ...);
```

**Encryption flow:** Generate data key → Encrypt message with AES-GCM (data key) →
Wrap data key with the Key Vault master key → Store in the Service Bus message.

---

## Idempotency

- **In-memory (single instance)** — registered by default as a singleton with a background
  cleanup service. Suitable for single-instance deployments.
- **SQL-based (multi-instance / production)** — install `SourceFlow.Stores.EntityFramework`
  and call `services.AddSourceFlowIdempotency(connectionString, cleanupIntervalMinutes)`
  before `UseSourceFlowAzure(...)`.

> ⚠️ Always use SQL-based idempotency for multi-instance deployments — the in-memory store
> lives in a single process and is insufficient for distributed systems.

---

## Local Development

Azurite emulates Blob/Queue/Table storage but **not** Service Bus. For local development and
CI, use the official Azure Service Bus emulator (backed by SQL Edge), declaring your entities
up front in its `Config.json`:

```bash
docker compose -f .github/azure-emulator/docker-compose.yml up -d

export AZURE_SERVICEBUS_CONNECTION_STRING="Endpoint=sb://localhost;\
SharedAccessKeyName=RootManageSharedAccessKey;\
SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true"
```

The emulator serves only entities declared in `Config.json` (no runtime creation) and caps
total queues + topics at 50.

---

## Monitoring

- **Activity Source:** `SourceFlow.Cloud.Azure`
- **Health check:** registered automatically as `azure-servicebus` (tags: `azure`,
  `servicebus`, `messaging`), covering namespace connectivity, queue/topic existence, and
  Key Vault access when encryption is enabled.
- Trace context is propagated via the Service Bus message `ApplicationProperties`
  (`traceparent`) for end-to-end distributed tracing.

---

## Best Practices

- Use sessions for ordered operations (the dispatcher sets `SessionId` = entity id).
- Enable duplicate detection on queues fed by at-least-once producers.
- Group related commands to the same queue (`CreateOrder`, `UpdateOrder`, `CancelOrder` → `orders`).
- Enable SQL-based idempotency in production.
- Prefer Managed Identity (`FullyQualifiedNamespace` + RBAC) over connection strings.
- Enable Key Vault encryption for PII, financial, or health data.
- Use IaC (Bicep/Terraform) for production resources; the bootstrapper is for dev convenience.
- Monitor health checks and dead-letter queue depth.

---

## License

MIT — see [LICENSE](LICENSE).

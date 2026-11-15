# SourceFlow.Cloud.GCP

**Google Cloud integration for distributed command and event processing**

[![NuGet](https://img.shields.io/nuget/v/SourceFlow.Cloud.GCP.svg)](https://www.nuget.org/packages/SourceFlow.Cloud.GCP/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

## Overview

SourceFlow.Cloud.GCP extends the SourceFlow.Net framework with Google Cloud integration, enabling distributed command and event processing using Google Cloud Pub/Sub and Cloud KMS. The fluent bus API is identical to the AWS and Azure providers — only the backing services change.

Google Cloud Pub/Sub has only **topics** and **subscriptions** (no separate queues). A command "queue" is modelled as a topic plus a pull subscription; an event "topic" is a topic plus a pull subscription per subscriber.

**Key Features:**
- 🚀 Pub/Sub command dispatching (publish to topics, pull from subscriptions)
- 📢 Pub/Sub event publishing with per-subscriber pull subscriptions
- 🔐 Cloud KMS envelope encryption for sensitive data
- ⚙️ Fluent bus configuration API
- 🔄 Automatic resource provisioning (topics + subscriptions)
- 📊 Built-in health checks and OpenTelemetry metrics
- 🧪 Pub/Sub emulator support for local development

---

## Installation

```bash
dotnet add package SourceFlow.Cloud.GCP
```

**Prerequisites:** SourceFlow ≥ 2.0.0, Google Cloud SDK / Application Default Credentials, .NET 8.0 / 9.0 / 10.0.

---

## Quick Start

```csharp
using SourceFlow.Cloud.GCP;

// Register SourceFlow core
services.UseSourceFlow(typeof(Program).Assembly);

// Configure Google Cloud Pub/Sub messaging
services.UseSourceFlowGcp(
    options => { options.ProjectId = "my-project"; },
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

This registers GCP dispatchers, configures routing, starts the Pub/Sub pull listeners, and automatically provisions topics and subscriptions at startup.

---

## Configuration Options

| Option | Type | Default | Description |
| --- | --- | --- | --- |
| `ProjectId` | string | (required) | Google Cloud project that owns the topics/subscriptions |
| `EnableCommandRouting` | bool | true | Enable command dispatching to topics |
| `EnableEventRouting` | bool | true | Enable event publishing to topics |
| `EnableCommandListener` | bool | true | Enable the command pull listener |
| `EnableEventListener` | bool | true | Enable the event pull listener |
| `MaxMessagesPerPull` | int | 10 | Messages requested per pull |
| `AckDeadlineSeconds` | int | 60 | Ack deadline applied to subscriptions at bootstrap |
| `SubscriptionSuffix` | string | `-sub` | Suffix used to derive a subscription id from a name |

---

## Resource Provisioning

The `GcpBusBootstrapper` runs as an `IHostedService` at startup and idempotently creates:

- **Topics** — one per command queue name and per event topic name.
- **Pull subscriptions** — `{name}-sub` for each listening command queue and each subscribed event topic.

All operations tolerate `AlreadyExists`, so it is safe to run on every startup.

---

## Message Encryption (Cloud KMS)

```csharp
services.AddSingleton(new GcpKmsOptions
{
    KeyName = "projects/my-project/locations/global/keyRings/my-ring/cryptoKeys/my-key"
});
services.AddSingleton<IMessageEncryption, GcpKmsMessageEncryption>();
```

Envelope encryption: a random 256-bit data key encrypts the payload with AES-256-GCM, and Cloud KMS wraps (encrypts) the data key. Cloud KMS has no `GenerateDataKey` operation, so the data key is generated locally and wrapped via the KMS `Encrypt` call.

---

## Local Development (Pub/Sub emulator)

```bash
gcloud beta emulators pubsub start --host-port=localhost:8085
export PUBSUB_EMULATOR_HOST=localhost:8085
```

The client libraries auto-detect `PUBSUB_EMULATOR_HOST` (via `EmulatorDetection.EmulatorOrProduction`). The bootstrapper creates topics/subscriptions in the emulator at startup — no manual setup required.

---

## Idempotency

- **In-memory (single instance)** — registered by default as a singleton with a background cleanup service.
- **SQL-based (multi-instance / production)** — install `SourceFlow.Stores.EntityFramework` and call `services.AddSourceFlowIdempotency(connectionString)` before `UseSourceFlowGcp(...)`.

---

## Monitoring

- **Activity/Meter source:** `SourceFlow.Cloud.GCP` (`gcp.pubsub.commands.dispatched`, `gcp.pubsub.events.published`).
- **Health check:** registered automatically; verifies Pub/Sub connectivity by listing topics in the project.

---

## License

MIT — see [LICENSE](LICENSE).

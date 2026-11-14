# SourceFlow.Cloud.Aws

`SourceFlow.Cloud.Aws` connects SourceFlow.Net services through Amazon SQS and SNS, and provides AWS KMS as the key provider for payload protection and crypto-shredding. A service's endpoint is an SQS queue (FIFO when ordered) with a redrive dead-letter queue, a topic is an SNS topic, and each subscribing service gets its own raw-delivery SNS-to-SQS subscription. The transport only moves bytes and headers, settles messages and provisions resources. Retries, the inbox, parking, encryption and telemetry are core behaviour and work the same on every broker (see [Architecture: Cloud Transports](Architecture/07-Cloud-Transports.md)).

## Contents

- [Installation](#installation)
- [Quick start](#quick-start)
- [How SourceFlow maps to SQS and SNS](#how-sourceflow-maps-to-sqs-and-sns)
- [Configuration](#configuration)
- [Credentials and region](#credentials-and-region)
- [Resource names](#resource-names)
- [Ordering (FIFO)](#ordering-fifo)
- [Provisioning](#provisioning)
- [IAM permissions](#iam-permissions)
- [Receiving, retries and dead letters](#receiving-retries-and-dead-letters)
- [Message size and wire format](#message-size-and-wire-format)
- [Subscription filters](#subscription-filters)
- [AWS KMS key provider](#aws-kms-key-provider)
- [Health checks](#health-checks)
- [Local development with LocalStack](#local-development-with-localstack)
- [The v2 wire bridge](#the-v2-wire-bridge)
- [Troubleshooting](#troubleshooting)
- [Reference](#reference)

## Installation

```bash
dotnet add package SourceFlow.Cloud.Aws
```

The package targets `net10.0` only. It depends on the core package `SourceFlow.Net` and on `AWSSDK.SQS`, `AWSSDK.SimpleNotificationService`, `AWSSDK.KeyManagementService` and `AWSSDK.Extensions.NETCore.Setup`.

You also need a storage adapter, for example [SourceFlow.Storage.PostgreSQL](SourceFlow.Storage.PostgreSQL-README.md), because the outbox and inbox that make delivery effectively-once live in the store.

## Quick start

```csharp
using SourceFlow.Cloud.Aws;
using SourceFlow.Hosting;
using SourceFlow.Storage.PostgreSQL;

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddSourceFlow(sf =>
{
    sf.ServiceName = "orders";
    sf.AddModule<OrdersModule>();
    sf.UsePostgreSql(builder.Configuration.GetConnectionString("SourceFlow")!);

    sf.UseAwsTransport(o =>
    {
        o.Region = "eu-west-1";
        o.NamePrefix = "prod-";          // one account, several environments
    });

    sf.Routing(r =>
    {
        r.Command<ReserveStock>().To("inventory").ReplyOnReject();   // a command owned by another service
        r.Publish<OrderPlaced>().AsIs().To("orders.events");         // an integration event, through the outbox
        r.Subscribe("inventory.events");                             // events of another service
    });
});

builder.Services.AddHealthChecks()
    .AddSourceFlow()          // sourceflow.ready, .store, .transport, ...
    .AddSourceFlowAws();      // sourceflow-aws: the endpoint queue exists and SQS answers

WebApplication app = builder.Build();
app.Run();
```

At startup, `UseAwsTransport` validates the options, so a value the SDK cannot honour (an unknown region, a timeout out of range) fails before the host starts. When the host starts it provisions or validates the service's topology ([Provisioning](#provisioning)), opens one receiver on the endpoint queue, starts the outbox relay, and starts the dead-letter ingestor.

The TravelBooking sample's hosts ([`samples/TravelBooking/Shared/ServiceHost.cs`](../../../samples/TravelBooking/Shared/ServiceHost.cs)) select AWS, Azure or Google Cloud from configuration (`Transport:Kind`, `Transport:Region`, `Transport:ServiceUrl`, `Transport:NamePrefix`). The `docker compose` demo runs them on AWS, against LocalStack.

## How SourceFlow maps to SQS and SNS

| SourceFlow concept | AWS resource | Notes |
|--------------------|--------------|-------|
| Endpoint (a service's input) | SQS queue `{prefix}{endpoint}`, or `{prefix}{endpoint}.fifo` when ordered | Receives the service's commands **and** the events of every topic it subscribes to. One receiver per service instance. |
| Dead-letter backstop | SQS queue `{prefix}{endpoint}-dlq` (`-dlq.fifo` when ordered) | Attached by a redrive policy with `maxReceiveCount` = the topology's delivery limit. Kept for 14 days. |
| Topic | SNS topic `{prefix}{topic}` (`.fifo` when ordered) | Owned by the publishing service. |
| Subscription | SNS subscription from the topic to the subscriber's endpoint queue | `RawMessageDelivery=true`, optional filter policy on `ce_type`, and a queue policy that lets the topic send. One per subscribing service; instances of a service compete on its queue. |
| Remote command | `SendMessageBatch` to the owner's queue | The sender finds `{base}` or `{base}.fifo`, whichever exists, so it does not need to know the owner's ordering. |

The capabilities the transport declares to the core (`AwsTransport.AwsCapabilities`):

| Capability | Value |
|------------|-------|
| `SystemName` (`messaging.system`) | `aws_sqs` |
| `MaxBodyBytes` | 180 KiB (`AwsWire.MaxBodyBytes`) |
| `MaxAttributes` | 10 (SQS and SNS limit) |
| `MaxRetention` | 14 days |
| `NativeDelay` / `MaxDelay` | yes / 12 hours (`ChangeMessageVisibility`) |
| `OrderedDelivery` | yes (FIFO message groups) |
| `NativeDeadLetter` | yes (redrive policy) |
| `SubscriptionFilters` | yes (SNS filter policies, with a receiver-side fallback) |
| `MaxBatchSize` | 10 |
| `Deduplication` | not declared: standard queues do not deduplicate; the inbox does |
| `MaxLockDuration` | 12 hours |

## Configuration

`UseAwsTransport(Action<AwsTransportOptions>)` registers `AwsTransport` as a singleton and as the service's `ITransport`. Every option maps to an SDK setting; there is no option the transport ignores.

### `AwsTransportOptions`

| Option | Type | Default | Maps to / rules |
|--------|------|---------|-----------------|
| `Region` | `string?` | `null` (the SDK region chain: `AWS_REGION`, profile, instance metadata) | `ClientConfig.RegionEndpoint`. Must be a known AWS region unless `ServiceUrl` is set. |
| `Profile` | `string?` | `null` (the SDK default credential chain) | A named profile in the shared credentials or config file. A missing profile fails when the transport is created. |
| `ServiceUrl` | `Uri?` | `null` | `ClientConfig.ServiceURL` for SQS and SNS, with `AuthenticationRegion = Region`. Use it for LocalStack (`http://localhost:4566`) or a VPC endpoint. |
| `MaxSdkRetries` | `int` | `3` | `ClientConfig.MaxErrorRetry`; 0 to 10. The SDK retries first, then the core's retry policy takes over. |
| `Timeout` | `TimeSpan` | 30 seconds | `ClientConfig.Timeout`; greater than 0 and at most 5 minutes. |
| `ReceiveWaitTime` | `TimeSpan` | 20 seconds | Caps `ReceiveMessageRequest.WaitTimeSeconds` (long polling); 0 to 20 seconds. Sub-second waits are short polls. |
| `NamePrefix` | `string` | `""` | Prepended to every physical queue and topic name, usually the environment (`prod-`). Letters, digits, `-` and `_` only; at most 40 characters. |
| `MessageRetention` | `TimeSpan` | 14 days | `MessageRetentionPeriod` of endpoint queues the transport creates; 1 minute to 14 days. |
| `V2Messages` | `V2MessageMap?` | `null` | Read-only; set by `AcceptV2Messages(...)`. |

| Method | Purpose |
|--------|---------|
| `AcceptV2Messages(Action<V2MessageMap> map)` | Turns on the v2 wire bridge with an explicit allow-list. Off by default. See [The v2 wire bridge](#the-v2-wire-bridge). |
| `Validate()` | Called by `UseAwsTransport`; throws `ArgumentException` for an option out of range. |
| `ToAwsOptions()` | The equivalent `AWSOptions` (region, profile, endpoint, retries, timeout), for creating further AWS clients with the same settings. |
| `ToSqsConfig()`, `ToSnsConfig()` | The SDK client configurations the options describe. |
| `ResolveCredentials()` | The credentials of `Profile`, or `null` for the default chain. |

### Runtime options that shape the transport

These are core options (`sf.Configure(o => …)`, see [SourceFlow.Net](SourceFlow.Net-README.md)), listed here because they decide how the AWS resources behave.

| `SourceFlowOptions` | Default | Effect on AWS |
|---------------------|---------|---------------|
| `ReceiveLockDuration` | 30 seconds | The visibility timeout of each `ReceiveMessage`. The receive endpoint renews it at half this interval while a handler runs. At most 12 hours. |
| `PollInterval` | 1 second | The wait the receive loop asks for; SQS long-polls in whole seconds, capped by `ReceiveWaitTime`. |
| `PrefetchPerWorker` | 16 | Messages asked for per receive; SQS returns at most 10. |
| `ImmediateRetries` | 3 | In-process retries of a transient failure before a delayed retry. |
| `DelayedRetries` | 10 s, 1 min, 10 min, 1 h | Delays applied with `ChangeMessageVisibility` (native delay). Exhausting them parks the message. |
| `IngestBrokerDeadLetters` | `true` | Runs the dead-letter ingestor on receiver instances. |
| `DeadLetterIngestInterval` | 30 seconds | How often the ingestor checks the `-dlq` queue when it was empty. Every check is an SQS call. |

The delivery limit written into the redrive policy is `1 + DelayedRetries.Count + 2`: 7 with the default schedule.

`SourceFlowBuilder.Provisioning` chooses the provisioning mode; see [Provisioning](#provisioning).

### Using your own SDK clients

`AwsTransport` also has a constructor over existing clients, which you own and dispose. Only the naming, receive and retention options apply then.

```csharp
AwsTransportOptions options = new() { NamePrefix = "prod-" };
AWSOptions aws = builder.Configuration.GetAWSOptions();

builder.Services.AddSingleton(new AwsTransport(aws.CreateServiceClient<IAmazonSQS>(), aws.CreateServiceClient<IAmazonSimpleNotificationService>(), options));
builder.Services.AddSourceFlow(sf =>
{
    // ...
    sf.UseTransport(sp => sp.GetRequiredService<AwsTransport>());
});
```

Registering `AwsTransport` itself in the container keeps `AddSourceFlowAws()` working, because the health check resolves `AwsTransport`.

## Credentials and region

Credentials are not an option. The transport uses the AWS SDK credential chain: environment variables, web identity (EKS IRSA), SSO, container and instance roles, and the shared credentials file. Set `Profile` to pick a named profile from the shared files instead.

- In production, run under an IAM role with the [runtime permissions](#iam-permissions) and leave `Profile` unset.
- The region comes from `Region`, or from the SDK chain (`AWS_REGION`, profile) when it is `null`.
- Throttling and outages are mapped to transient errors (`TransportThrottledException` with a one-second `RetryAfter`, `TransportUnavailableException`). Credential resolution failures are also transient, so a role that is temporarily unavailable recovers without a restart.

## Resource names

Names come from `AwsNaming` and are deterministic, so every service and every infrastructure-as-code module computes the same name for the same logical resource.

| Resource | Name |
|----------|------|
| Endpoint queue | `{prefix}{endpoint}`, plus `.fifo` when ordered |
| Dead-letter queue | `{prefix}{endpoint}-dlq`, plus `.fifo` when ordered |
| Topic | `{prefix}{topic}`, plus `.fifo` when ordered |

- Characters outside `[A-Za-z0-9_-]` become `-`, so the logical topic `orders.events` with prefix `prod-` is the SNS topic `prod-orders-events`.
- The base name is at most 71 characters, so the FIFO and dead-letter suffixes still fit the 80-character SQS limit. Longer names keep a readable head and an 8-character hash of the full name.
- Names are not pluggable. To see exactly what a service will use, export its topology ([Provisioning](#provisioning)).

## Ordering (FIFO)

The core always keeps per-partition-key order inside one instance. Broker ordering adds order across instances. Declare it in routing:

```csharp
sf.Routing(r =>
{
    r.OrderedEndpoint();                                   // the endpoint queue is FIFO
    r.Publish<AccountOpened>().AsIs().To("accounts.events");
    r.OrderedTopic("accounts.events");                     // the SNS topic is FIFO
});
```

- A FIFO message's `MessageGroupId` is the envelope's partition key (the stream id by default, or the `OrderedBy(...)` key of a publication). A message without one uses its own message id as its group.
- `MessageDeduplicationId` is the message id, so a FIFO queue also drops a re-sent message id for 5 minutes. The inbox is still what guarantees effectively-once.
- A FIFO SNS topic can only deliver to FIFO queues, and a standard topic only to standard queues. `Validate` reports a mismatch as `OrderingMismatch`, and `CreateIfMissing` does not subscribe a mismatched pair.
- `OrderedTopic` on a topic the service does not publish fails at startup: a topic's ordering belongs to its publisher.
- A subscriber may start before the publisher. A subscribed topic that does not exist yet is created with the subscriber's endpoint ordering.
- FIFO-ness cannot be changed in place. Changing it means new queues and topics (the old ones keep their names without, or with, `.fifo`).

## Provisioning

Provisioning follows [ADR-017](../ADRs/ADR-017-provisioning-modes-and-topology-as-data.md). The host calls `ProvisionAsync(topology, mode)` once at startup.

| `ProvisioningMode` | What happens on AWS |
|--------------------|---------------------|
| `CreateIfMissing` | Creates what is missing: the dead-letter queue (14-day retention), the endpoint queue with `MessageRetentionPeriod` and the redrive policy, published topics, subscribed topics that do not exist, raw-delivery subscriptions with their filter policy, and the queue policy for the subscribed topics. Existing resources are never changed. Then validates. |
| `Validate` | Describes resources and fails startup with a `TopologyValidationException` listing every difference at once. |
| `None` | Only checks that the topology itself is consistent (names, delivery limit, ordered topics published, filters parse). No AWS call. |

When `SourceFlowBuilder.Provisioning` is left `null`, the mode is `CreateIfMissing` in the Development environment (or without a host environment) and `Validate` in every other environment.

```csharp
builder.Services.AddSourceFlow(sf =>
{
    sf.Provisioning = ProvisioningMode.Validate;   // explicit, whatever the environment
    // ...
});
```

What `Validate` checks:

| Problem kind | Detected when |
|--------------|---------------|
| `MissingEndpoint` | The endpoint queue (`{base}` or `{base}.fifo`) does not exist. |
| `OrderingMismatch` | The queue or a published topic is FIFO and the topology is not, or the reverse; or a subscribed topic and the queue differ in FIFO-ness. |
| `MissingDeadLetter` | The queue has no redrive policy, its target queue does not exist, or `maxReceiveCount` differs from the topology's delivery limit. |
| `MissingTopic` | A published or subscribed topic does not exist. |
| `MissingSubscription` | The queue is not subscribed to a subscribed topic. |
| `Misconfigured` | Raw message delivery is off, the filter policy scope is not `MessageAttributes`, or the queue policy does not allow the topic to send. |
| `FilterDrift` | The subscription's filter policy differs from the routing filter. |

### Infrastructure as code

For production, create the resources from the service's topology export and run with `Validate`:

```bash
# inside the service host (the CLI's model commands run in the service, see SourceFlow.Tool)
dotnet run -- sourceflow topology export --output orders.topology.json
dotnet run -- sourceflow topology validate
```

The export lists every physical resource with its AWS properties (`fifo`, `messageRetentionSeconds`, `deadLetterQueue`, `maxReceiveCount`, `allowTopics`, `rawMessageDelivery`, `filterPolicy`, `filterPolicyScope`). The Terraform module in [`deploy/topology/terraform/aws`](../../../deploy/topology/README.md) consumes it without computing any name, and outputs `runtime_policy_json`, a least-privilege IAM policy for the service. In code, the same description is `AwsTopology.Describe(options, topology)`, or `ITopologyDescriber.Describe` on the transport. No AWS call is made.

A topic belongs to its publisher: the subscriber's module looks a subscribed topic up rather than creating it, so apply the publisher's stack first.

## IAM permissions

### Runtime with `Validate` (production)

This is the policy the Terraform module outputs (`runtime_policy_json`):

| Statement | Actions | Resources |
|-----------|---------|-----------|
| Receive | `sqs:ReceiveMessage`, `sqs:DeleteMessage`, `sqs:ChangeMessageVisibility`, `sqs:GetQueueAttributes`, `sqs:GetQueueUrl`, `sqs:SendMessage` | The endpoint queue and its dead-letter queue |
| Publish | `sns:Publish`, `sns:GetTopicAttributes` | The topics the service publishes |
| Send | `sqs:SendMessage`, `sqs:GetQueueUrl`, `sqs:GetQueueAttributes` | The queues of remote endpoints the service sends commands to |
| Describe | `sqs:GetQueueUrl`, `sqs:GetQueueAttributes`, `sqs:ListQueues`, `sns:ListTopics`, `sns:GetTopicAttributes`, `sns:ListSubscriptionsByTopic`, `sns:GetSubscriptionAttributes` | `*` |

- `sqs:SendMessage` on the endpoint's own queues is needed because an explicit dead letter sends a copy to the `-dlq` queue.
- `sns:ListTopics` is needed because SNS has no lookup by name: the transport lists topics to find one.
- The publish path uses `PublishBatch` and the send path `SendMessageBatch`; IAM authorises those with `sns:Publish` and `sqs:SendMessage`.

### Provisioning with `CreateIfMissing` (development)

Add `sqs:CreateQueue`, `sqs:SetQueueAttributes`, `sns:CreateTopic` and `sns:Subscribe`. Keep these out of production roles; that is the point of `Validate`.

### KMS

`AwsKmsKeyProvider` calls `GenerateDataKey` and `Decrypt`, so the service needs `kms:GenerateDataKey` and `kms:Decrypt` on its key.

## Receiving, retries and dead letters

One `AwsReceiver` long-polls the endpoint queue (`ReceiveMessage` with up to 10 messages, all message attributes, and `ApproximateReceiveCount`). The core receive loop does the rest:

| Step | SQS operation |
|------|---------------|
| Receive | `ReceiveMessage` with `VisibilityTimeout = ReceiveLockDuration` |
| Keep the lock while the handler runs | `ChangeMessageVisibility` every half lock |
| Complete after the handler commits | `DeleteMessage` |
| Delayed retry (transient failure) | `ChangeMessageVisibility` to the delay (up to 12 hours) |
| Explicit dead letter | `SendMessage` of a copy with `sf_reason` to the `-dlq` queue, then `DeleteMessage` |
| Graceful stop | `ChangeMessageVisibilityBatch` to 0 for every unsettled message, so another instance gets them at once |

Failure behaviour:

- **Delivery count.** `DeliveryCount` is `ApproximateReceiveCount`. The core uses it to pick the next delayed retry.
- **Parking.** A permanent failure, or a transient one after the last delayed retry, is parked in the store's dead-letter table and the SQS message is deleted. Use `dotnet sourceflow dlq list|show|replay|discard` to work with parked messages (see [SourceFlow.Tool](SourceFlow.Tool-README.md)).
- **Broker backstop.** A message the receiver never settles (for example a process that keeps crashing) reaches `maxReceiveCount` and SQS moves it to the `-dlq` queue. The dead-letter ingestor drains that queue into the store, reason `MaxReceiveCountExceeded`, with the envelope intact, so `dlq replay` can re-send it.
- **Late settlement.** Completing or abandoning with a stale receipt handle (the visibility timeout expired and the message was redelivered) is ignored. A renew with a stale handle reports a lost lock; the redelivery is absorbed by the inbox.
- **Foreign messages.** A message that is not a SourceFlow CloudEvent (and not an accepted v2 message) is left on the queue until the redrive policy moves it. On the dead-letter queue it is hidden for 12 hours so the ingestor moves on.
- **Outbox side.** `SendMessageBatch` and `PublishBatch` failures are reported per message (`TransportSendException`). Transient failures back off and retry; after `OutboxMaxAttempts` (10) or on a permanent error such as a missing queue (`DestinationNotFoundException`), the row is parked.

The effectively-once guarantee comes from the outbox and inbox, not from SQS. See the [Message Idempotency Guide](Message-Idempotency-Guide.md).

## Message size and wire format

Messages travel in CloudEvents binary mode (`AwsWire`):

| Item | How it travels |
|------|----------------|
| CloudEvents attributes | String message attributes named `ce_{attribute}` (`ce_id`, `ce_type`, `ce_source`, `ce_sftenant`, ...) |
| Extensions beyond the 10-attribute budget | One JSON attribute, `ce_sfext` |
| Body | UTF-8 text when SQS can carry it unchanged, otherwise base64 with `sf_body=base64` (`sf_body=empty` for an empty body). Bodies are never transcoded. |
| Published events | `sf_topic`, the logical topic, for receiver-side filters |
| Explicit dead letters | `sf_reason` |

Limits:

- `MaxBodyBytes` is 180 KiB, so a base64 body and its attributes still fit the 256 KiB SQS and SNS message limit. A batch is split so it stays within 10 messages and 256 KiB.
- An oversized body is refused before anything is sent for it (`MessageTooLargeException`). The core checks the same limit when it writes the outbox row, so an oversized event fails the command that produced it instead of being parked later.
- There is no claim-check. Keep large payloads in your own storage and send a reference.

## Subscription filters

Routing filters are CloudEvents SQL:

```csharp
sf.Routing(r =>
{
    r.Subscribe("payments.events", s => s.Only<PaymentCaptured>());                  // type IN (...)
    r.Subscribe("shipping.events", s => s.Where("type LIKE 'shipping.shipment-%'")); // a type prefix
    r.Subscribe("inventory.events", s => s.Where("sftenant = 'acme'"));              // evaluated by the receiver
});
```

- A filter that is a list of types (`type = '…'`, `type IN (…)`, or an OR of those) or of type prefixes (`type LIKE 'prefix%'`) becomes an SNS filter policy on `ce_type` (scope `MessageAttributes`). SNS drops non-matching events before delivery.
- Any other filter leaves the subscription unfiltered. The receiver evaluates it per topic (using `sf_topic`) with the same evaluator and deletes non-matching events unseen. Results are identical either way.

## AWS KMS key provider

The package also implements the security SPI's `IKeyProvider` with AWS KMS. It is registered separately from the transport:

```csharp
builder.Services.AddSourceFlow(sf =>
{
    // ...
    sf.Security(s =>
    {
        s.UseAwsKms(k => k.KeyId = "alias/accounts-messages");
        s.Protect(p => p.Protect("accounts.events"));   // payload protection for this topic
        s.EncryptPersonalData();                        // crypto-shredding of [PersonalData] members
    });
});
```

`AwsKmsKeyProvider` (name `aws-kms`) calls `GenerateDataKey` (AES-256) for a new data key and `Decrypt` to unwrap one. The key reference stored with each wrapped key is the key ARN that KMS reports, so key rotation is transparent. Throttling and outages are transient; an invalid ciphertext, a disabled or missing key, or a wrong key usage are permanent.

| `AwsKmsOptions` | Type | Default | Notes |
|-----------------|------|---------|-------|
| `KeyId` | `string?` | required | Key id, key ARN, alias name (`alias/accounts-messages`) or alias ARN. |
| `Region` | `string?` | SDK chain | |
| `Profile` | `string?` | default chain | |
| `ServiceUrl` | `Uri?` | `null` | For LocalStack (`http://localhost:4566`). |
| `MaxSdkRetries` | `int` | `3` | 0 to 10. |
| `Timeout` | `TimeSpan` | 10 seconds | Per KMS call. |

How protection works (envelope encryption, data-key reuse limits, `DecryptionFailed` parking, erasure) is described in the [Security and Compliance Guide](Security-and-Compliance-Guide.md).

## Health checks

| Check | Registered by | What it does |
|-------|---------------|--------------|
| `sourceflow.transport` (tag `ready`) | `AddHealthChecks().AddSourceFlow()` | Calls `AwsTransport.ProbeAsync`: the endpoint queue exists and SQS answers. The core caches the result. |
| `sourceflow-aws` (tag `ready`) | `AddHealthChecks().AddSourceFlowAws(name)` | The same probe as a standalone `AwsTransportHealthCheck`. |

Map them on readiness, not liveness, so an AWS outage does not restart the service.

## Local development with LocalStack

The repository pins `localstack/localstack:3.8` with SQS, SNS and KMS:

```bash
docker compose -f .github/emulators/localstack/docker-compose.yml up -d
curl -s http://localhost:4566/_localstack/health
```

Point the transport at it:

```csharp
sf.UseAwsTransport(o =>
{
    o.Region = "eu-west-1";
    o.ServiceUrl = new Uri("http://localhost:4566");
});
```

```bash
export AWS_ACCESS_KEY_ID=test
export AWS_SECRET_ACCESS_KEY=test
export AWS_REGION=eu-west-1
```

- In the Development environment the default mode is `CreateIfMissing`, so the queues, topics and subscriptions appear on first start.
- The queue policy that lets SNS send to SQS is created on LocalStack too, although LocalStack does not enforce it.
- `UseAwsKms` accepts the same `ServiceUrl` for KMS on LocalStack.

How the test suites use LocalStack is described in [Cloud Integration Testing](Cloud-Integration-Testing.md).

## The v2 wire bridge

During a migration, v2 producers (`SourceFlow.Cloud.AWS`) may still send v2-shaped messages to a v3 service's queue. The AWS transport can translate them through an explicit allow-list:

```csharp
sf.UseAwsTransport(o =>
{
    o.Region = "eu-west-1";
    o.AcceptV2Messages(map => map
        .Type("MyApp.Commands.CreateAccount, MyApp", "bank.account.open", 1));
});
```

- The receiver tries the bridge only for a message that is not a SourceFlow CloudEvent.
- Commands are read from the `CommandType`, `EntityId` and `SequenceNo` attributes; events from `EventType`, either raw on SQS or inside an SNS notification.
- The v3 message id is UUIDv5 of the v2 message id, so a redelivered v2 message is a duplicate for the inbox.
- An unmapped v2 type is parked as `UnknownMessageType`. Without `AcceptV2Messages`, v2 messages are left for the redrive policy.

The full procedure, including converters (`Func<V2Message, JsonNode?>`), is in [Migrating from v2, section 7](../guides/migrating-from-v2.md#7-the-v2-wire-bridge-aws). The bridge is planned for removal in 4.0.

## Troubleshooting

| Symptom | Likely cause | What to do |
|---------|--------------|------------|
| Startup fails with `TopologyValidationException` listing `MissingEndpoint`, `MissingTopic` or `MissingSubscription` | The environment is not Development, so the mode is `Validate`, and the resources were not created | Apply the Terraform module from `topology export`, or set `sf.Provisioning = ProvisioningMode.CreateIfMissing` where creating resources is acceptable. |
| `Misconfigured ... raw message delivery is off` | The subscription was created by hand or by v2 | Recreate it with `RawMessageDelivery=true`. Without it SQS receives the SNS JSON wrapper instead of the CloudEvent. |
| `OrderingMismatch` between a topic and a queue | A FIFO topic and a standard queue (or the reverse) | Make both sides agree; FIFO-ness is fixed at creation, so recreate one side. |
| `MissingDeadLetter ... expected max deliveries 7, found maxReceiveCount 5` | `DelayedRetries` changed, or the queue was created elsewhere | Update the redrive policy to the new limit (`1 + DelayedRetries.Count + 2`). |
| `ArgumentException: AwsTransportOptions.Region 'xx' is not a known AWS region` | Typo in `Region`, or a LocalStack region without `ServiceUrl` | Fix the region, or set `ServiceUrl`. |
| `DestinationNotFoundException: Destination 'inventory' does not exist` | The remote service's queue does not exist yet | Deploy the owning service (or its infrastructure) first, then `dotnet sourceflow dlq replay` the parked outbox rows. |
| Events published but never received | Queue policy missing, or a filter policy that excludes the type | Run `topology validate`; it reports both. |
| Messages reappear while a handler is still running | The handler outlives `ReceiveLockDuration` and renewal failed | Check logs for lost-lock warnings; raise `ReceiveLockDuration`. Duplicates are absorbed by the inbox. |
| Messages accumulate in `{endpoint}-dlq` | Messages that are not CloudEvents, or a crash loop | The ingestor parks SourceFlow messages in the store; foreign messages stay hidden on the DLQ for inspection. |
| `MessageTooLargeException` when a command commits | An event body over 180 KiB | Send a reference to the data instead. |
| Throttling warnings | SQS or SNS request limits | The SDK retries (`MaxSdkRetries`) and the core backs off. Each empty dead-letter check is a call, so a longer `DeadLetterIngestInterval` reduces load on quiet services. |

## Reference

- Source: [`src/Cloud/SourceFlow.Cloud.Aws`](../../../src/Cloud/SourceFlow.Cloud.Aws)
- Architecture: [Cloud Transports](Architecture/07-Cloud-Transports.md)
- Design: [07 Cloud Transports](../Design/07-Cloud-Transports.md), [10 Security and Compliance](../Design/10-Security-and-Compliance.md)
- ADRs: [ADR-014](../ADRs/ADR-014-transport-spi-and-conformance-kit-parity-required.md), [ADR-017](../ADRs/ADR-017-provisioning-modes-and-topology-as-data.md), [ADR-033](../ADRs/ADR-033-final-transport-spi-pull-receivers-and-the-in-memory-reference-broker.md)
- Infrastructure as code: [`deploy/topology`](../../../deploy/topology/README.md)
- Testing: [Cloud Integration Testing](Cloud-Integration-Testing.md)
- Other transports: [Azure Service Bus](SourceFlow.Cloud.Azure-README.md), [Google Cloud Pub/Sub](SourceFlow.Cloud.Gcp-README.md)

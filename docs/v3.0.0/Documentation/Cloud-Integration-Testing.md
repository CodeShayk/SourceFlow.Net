# Cloud Integration Testing

This guide explains how SourceFlow.Net v3 tests its cloud transports, and how to run those tests yourself: the Transport Conformance Kit (TCK) that every transport must pass, the emulators used on pull requests, the end-to-end scenarios that run real services across a broker, the real-cloud nightly runs, the chaos tests, and the CI jobs that tie them together.

The principle comes from [ADR-014](../ADRs/ADR-014-transport-spi-and-conformance-kit-parity-required.md): behaviour the core can supply is written once in the core, and every transport must pass the same conformance cases before release. AWS SNS/SQS, Azure Service Bus, Google Pub/Sub and the in-memory reference broker all pass all 45 cases.

## Contents

- [Test layers](#test-layers)
- [Test projects and traits](#test-projects-and-traits)
- [The Transport Conformance Kit](#the-transport-conformance-kit)
- [Running a transport adapter through the TCK](#running-a-transport-adapter-through-the-tck)
- [Emulators](#emulators)
- [Running the cloud tests locally](#running-the-cloud-tests-locally)
- [End-to-end scenarios](#end-to-end-scenarios)
- [Real-cloud runs](#real-cloud-runs)
- [Chaos tests](#chaos-tests)
- [Infrastructure-as-code tests](#infrastructure-as-code-tests)
- [CI pipelines](#ci-pipelines)
- [Troubleshooting](#troubleshooting)
- [Reference](#reference)

## Test layers

| Layer | What it proves | Where it runs | Broker |
|-------|----------------|---------------|--------|
| Transport unit tests | Option mapping, wire encoding, error mapping, provisioning requests | Every PR | Mocked SDK clients (`AwsMocks`, `AzureMocks`, `GcpMocks`) |
| TCK, in memory | The reference behaviour, with a controllable clock and every fault | Every PR (unit job) | `InMemoryBroker` |
| TCK, emulators | Each cloud transport behaves like the reference | Every PR (transports job) | LocalStack, Pub/Sub emulator, Service Bus emulator |
| TCK, real cloud | The same, on the real services; fault-injection cases skipped | Nightly | AWS, Azure, Google Cloud |
| Topology and IaC | Exports are stable; Terraform and Bicep modules produce resources that pass `Validate` | Every PR (iac job) | LocalStack, Pub/Sub emulator; Bicep build |
| End to end | Real services on PostgreSQL talk across each broker: fan-out, remote commands, workflows, duplicates | Every PR (e2e job); real cloud nightly | In memory and the three emulators |
| Chaos | No loss and no duplicate effect under crashes, outages and stalls | Nightly | In memory, on PostgreSQL |
| Performance | Outbox-to-broker latency (PERF-07) | Nightly | The three emulators |

## Test projects and traits

| Project | Contents |
|---------|----------|
| [`tests/Core/SourceFlow.Core.Tests`](../../../tests/Core/SourceFlow.Core.Tests) | `Transport/InMemoryTransportConformanceTests.cs`: TCK-01..45 against the in-memory broker. |
| [`tests/Cloud/SourceFlow.Cloud.Tests`](../../../tests/Cloud/SourceFlow.Cloud.Tests) | `Aws/`, `Azure/`, `Gcp/`: unit tests, provisioning tests, the TCK on emulators and on real cloud, and the emulator servers (`LocalStackServer`, `PubSubEmulatorServer`, `ServiceBusEmulatorServer`). `Security/`: KMS key providers (fakes, LocalStack and real KMS). `Topology/`: export drift, `Describe`, and the Terraform/Bicep tests. |
| [`tests/EndToEnd/SourceFlow.EndToEnd.Tests`](../../../tests/EndToEnd/SourceFlow.EndToEnd.Tests) | Cross-service, OrderFulfilment, v2 bridge, payload protection, relay latency and chaos scenarios; `BrokerConnection`. |
| [`src/Tooling/SourceFlow.Testing`](../../../src/Tooling/SourceFlow.Testing) | The kits themselves (`Conformance/TransportConformance.cs`, `TopologyFixtures.cs`) and `ChaosInjector`. See [SourceFlow.Testing](SourceFlow.Testing-README.md). |

Tests are selected by xUnit traits, which the CI jobs filter on:

| Trait | Values used by the cloud tests |
|-------|--------------------------------|
| `Category` | `Unit`, `Integration`, `Conformance`, `EndToEnd`, `Workflow`, `Chaos`, `Performance` |
| `Cloud` | `Aws`, `Azure`, `Gcp` |
| `Requires` | `Docker`, `LocalStack`, `PubSubEmulator`, `ServiceBusEmulator`, `RealCloud`, `Terraform` |
| `Id` | The traceability id, for example `TCK-13`, `E2E-18`, `WF-E2E-11`, `CH-07` |
| `Covers` | The design items a test covers, for example `ADR-014;doc 07 7` |

## The Transport Conformance Kit

`SourceFlow.Testing.Conformance.TransportConformance.Cases` holds 45 cases, TCK-01 to TCK-45 ([ADR-033](../ADRs/ADR-033-final-transport-spi-pull-receivers-and-the-in-memory-reference-broker.md)). Each case provisions what it needs with `CreateIfMissing` under the fixture's prefix and drives the transport through the SPI only. Cases 18, 24, 25 and 45 also start real SourceFlow services (a small generated domain, `TckModule`) on the transport under test.

| Group | Cases | What is checked |
|-------|-------|-----------------|
| Envelope | TCK-01..06 | Binary mode round-trips every attribute, extension and header; a structured CloudEvent document round-trips untouched; Unicode survives; extensions beyond the attribute budget overflow into `sfext` and come back; a body of exactly `MaxBodyBytes` is delivered intact; an oversized body is refused before anything is sent. |
| Send and publish | TCK-07..12 | Send delivers once with a first delivery count of 1; a batch with a bad message reports exactly that message and sends the others; a send larger than the batch limit is split and delivered in order; publish reaches the subscribed endpoint; a missing topic fails `Validate` and publishing; fan-out to 2 services x 2 instances gives each service each event once, with instances competing. |
| Ordering | TCK-13..15 | 10,000 messages over 100 keys and 3 consumers keep per-key order on an ordered endpoint (checked on first deliveries: a redelivered duplicate is allowed, as the inbox absorbs it); an unordered endpoint has no head-of-line blocking; order survives a redelivery. On Azure, TCK-13 runs against a real namespace only (nightly): under CI load the Service Bus emulator delivered a session message for the first time after later ones of its session. |
| Deduplication | TCK-16..18 | Broker deduplication behaves as `TransportCapabilities` declares; distinct ids with identical bodies are never deduplicated; with the core, a producer retry with the same message id has one effect. |
| Settlement | TCK-19..22 | Complete removes; abandon with a delay redelivers after the delay, not before, with a higher delivery count; renew extends the lock; a crash before complete means a redelivery after the lock. |
| Dead letters | TCK-23..25 | After the maximum deliveries the broker dead-letters the message; the ingestor parks it with the envelope intact; a parked broker dead letter replays to its endpoint and takes effect. |
| Filters | TCK-26..28 | A type filter delivers only the listed types; a CESQL filter with `LIKE` and an extension delivers only matching events; filtering matches the reference CESQL evaluation exactly. |
| Correlation | TCK-29..31 | Correlation and causation, trace context, tenant and actor survive a chain of hops. |
| Resilience | TCK-32..34 | The same receiver recovers after an outage; throttling is transient with a retry-after; expired credentials are refreshed without failing the call. These need fault injection. |
| Provisioning | TCK-35..40 | `CreateIfMissing` is idempotent and leaves a valid topology; `Validate` reports every missing endpoint, topic and subscription at once, and detects an ordering mismatch, a missing dead-letter queue, a missing or misconfigured subscription, and filter drift. |
| Payload protection | TCK-41..43 | A protected envelope (marker and ciphertext) round-trips byte for byte; bodies are never transcoded; a tampered ciphertext is delivered as is for the core to reject. |
| Shutdown and end to end | TCK-44..45 | Completed messages stay completed and the rest are redelivered after shutdown; with the core, remote commands take effect once under duplicates and integration events reach each service once. |

A failed check throws `ConformanceException` (from the framework-agnostic `Check` helpers), so the kit runs under any test framework.

### The fixture contract

A transport is put under test through `ITransportFixture`:

| Member | Purpose |
|--------|---------|
| `ITransport Transport` | The transport under test. |
| `string Prefix` | A unique prefix for endpoint and topic names in this case, so cases and runs do not collide. |
| `bool SupportsClockControl` / `AdvanceClockAsync(TimeSpan)` | Whether the broker's clock can be moved (in memory only). Without it, timing cases use real time with shorter delays. |
| `TimeSpan ReceiveTimeout` | The longest wait for a message. |
| `TimeSpan QuietPeriod` | How long to wait to be sure nothing more arrives (default 300 ms). |
| `TimeSpan DelayTolerance` | How late a delayed redelivery may arrive (default 2 s). |
| `ITransportFaults? Faults` | Fault injection, or `null` on real cloud. |
| `InjectDefectAsync(TopologyProblemKind, TransportTopology, CancellationToken)` | Breaks a provisioned resource so the `Validate` cases can be checked; returns `false` when the broker cannot express that defect. |

`ITransportFaults` has `Unavailable`, `ThrottleNext(count, retryAfter)`, `ExpireCredentials()`, `CredentialRefreshes` and `DuplicateNext(count)`. The cloud fixtures implement it inside the SDK's own pipeline (for example the AWS fixture injects faults into the SDK's HTTP pipeline and counts credential refreshes), so the real client code is exercised.

Each case is a `ConformanceCase<ITransportFixture>` with `Id`, `Name`, `Run`, `RequiresClockControl` and `RequiresFaultInjection`. A runner skips a case it cannot drive; it never weakens it.

### Topology fixtures

`TopologyFixtures` (in the same namespace) names the topology shapes the provisioning cases and the infrastructure-as-code modules are tested with: `minimal`, `full` (the TCK provisioning topology: a filtered subscription, a publication and a non-default delivery limit), `ordered`, `filtered`, `publisher` and `subscriber`. Their exports for each transport are committed under [`deploy/topology/fixtures`](../../../deploy/topology/README.md), and a test fails when they drift; run the cloud tests with `SOURCEFLOW_APPROVE=1` to regenerate them.

## Running a transport adapter through the TCK

A new transport ([how to write one](Architecture/07-Cloud-Transports.md#writing-a-new-transport-adapter)) gets parity by passing the same cases. With xUnit v3, as the repository's own suites do:

```csharp
using SourceFlow.Testing.Conformance;
using SourceFlow.Transport;

internal sealed class MyBrokerFixture : ITransportFixture
{
    public MyBrokerFixture(MyBrokerServer server)
    {
        Transport = new MyBrokerTransport(server.ConnectionString, new MyBrokerOptions { NamePrefix = "" });
    }

    public ITransport Transport { get; }

    public string Prefix { get; } = "tck-" + Guid.CreateVersion7().ToString("N")[^10..];

    public bool SupportsClockControl => false;

    public TimeSpan ReceiveTimeout => TimeSpan.FromSeconds(20);

    public ITransportFaults? Faults => null;   // implement it to run TCK-32..34

    public ValueTask AdvanceClockAsync(TimeSpan by) => throw new NotSupportedException("The broker uses the real clock.");

    public ValueTask DisposeAsync()
    {
        // Delete every resource whose name starts with Prefix.
        return ValueTask.CompletedTask;
    }
}

[Trait("Category", "Conformance")]
public sealed class MyBrokerConformanceTests(MyBrokerServer server)
{
    public static TheoryData<string> CaseIds => new(TransportConformance.Cases.Select(c => c.Id));

    [Theory]
    [MemberData(nameof(CaseIds))]
    public async Task Tck(string id)
    {
        ConformanceCase<ITransportFixture> @case = TransportConformance.Cases.Single(c => c.Id == id);
        Assert.SkipWhen(@case.RequiresFaultInjection, "No fault injection for this broker.");
        await using var fixture = new MyBrokerFixture(server);

        await @case.Run(fixture, TestContext.Current.CancellationToken);
    }
}
```

Guidelines from the existing fixtures:

- Use a fresh fixture per case and a unique `Prefix`; delete what the prefix owns on dispose.
- Implement `InjectDefectAsync` for every `TopologyProblemKind` the broker can express, or the `Validate` cases TCK-37..40 fail with "the fixture can inject …".
- Give emulators generous `ReceiveTimeout`, `QuietPeriod` and `DelayTolerance` (the AWS fixture uses 20 s, 1.5 s and 3 s).
- Compare against the in-memory reference: [`InMemoryTransportConformanceTests.cs`](../../../tests/Core/SourceFlow.Core.Tests/Transport/InMemoryTransportConformanceTests.cs) is the smallest complete fixture, with a controllable clock and every fault.

## Emulators

The repository's root [`docker-compose.yml`](../../../docker-compose.yml) is the **demo** stack, the [TravelBooking sample](../../../samples/TravelBooking/README.md) (the Bookings service on 8080, the Hotels service on 8081, SourceFlow Studio on 5080, the Aspire dashboard on 18888, PostgreSQL on 55432 and LocalStack on 4566). The cloud emulators are separate compose files under [`.github/emulators`](../../../.github/emulators/README.md), pinned to exact images:

| Emulator | Compose file | Image | Ports | Health gate |
|----------|--------------|-------|-------|-------------|
| LocalStack (SQS, SNS, KMS) | `.github/emulators/localstack/docker-compose.yml` | `localstack/localstack:3.8` | 4566 | `GET /_localstack/health` reports `"sqs": "available"` |
| Google Pub/Sub emulator | `.github/emulators/pubsub/docker-compose.yml` | `gcr.io/google.com/cloudsdktool/google-cloud-cli:590.0.1-emulators` (project `sourceflow-emulator`) | 8085 | `PUT /v1/projects/sourceflow-emulator/topics/health` succeeds |
| Azure Service Bus emulator | `.github/emulators/servicebus/docker-compose.yml` | `mcr.microsoft.com/azure-messaging/servicebus-emulator:2.0.1` with `mcr.microsoft.com/azure-sql-edge:2.0.0` | 5672 (AMQP), 5300 (management) | `GET http://localhost:5300/health` |

```bash
docker compose -f .github/emulators/localstack/docker-compose.yml up -d
docker compose -f .github/emulators/pubsub/docker-compose.yml up -d
MSSQL_SA_PASSWORD='Your_strong_pw1' docker compose -f .github/emulators/servicebus/docker-compose.yml up -d
```

The tests use a running emulator when its environment variable is set, and otherwise start the same pinned image with Testcontainers (Docker required):

| Variable | Used by | Example |
|----------|---------|---------|
| `AWS_ENDPOINT_URL` | `LocalStackServer` | `http://localhost:4566` |
| `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_REGION` | AWS SDK | `test`, `test`, `eu-west-1` |
| `PUBSUB_EMULATOR_HOST` | `PubSubEmulatorServer`, Google client libraries | `localhost:8085` |
| `SERVICEBUS_EMULATOR_CONNECTION` | `ServiceBusEmulatorServer` | `Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;` |
| `SERVICEBUS_EMULATOR_ADMIN_CONNECTION` | `ServiceBusEmulatorServer` | `Endpoint=sb://localhost:5300;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;` |
| `MSSQL_SA_PASSWORD` | The Service Bus emulator compose file | Any strong password |

Emulator limits that shape the tests:

- **Service Bus emulator.** `servicebus/Config.json` declares the namespace only; every test provisions its own queues and topics through the management endpoint, as on Azure. Time to live is at most 1 hour (the end-to-end tests set `MessageTimeToLive = TimeSpan.FromHours(1)`), `MaxDeliveryCount` at most 10, a duplicate-detection window at most 5 minutes, and a busy namespace is throttled, so the fixture keeps the client's retries on.
- **Pub/Sub emulator.** It redelivers a nacked ordered message after its successor; the transport uses a one-second ack deadline for a zero-delay abandon on ordered subscriptions so order holds on both. The `-emulators` image tags are pruned after about a year, so the pin must be bumped at least yearly.
- **LocalStack.** It does not enforce SQS queue policies; the real-cloud nightly run covers them. Image bumps need a manual licence review.

## Running the cloud tests locally

The repository uses Microsoft.Testing.Platform filters, as the CI jobs do:

```bash
dotnet build SourceFlow.slnx -c Release

# In-memory TCK and unit tests only (no Docker)
dotnet test --project tests/Core/SourceFlow.Core.Tests/SourceFlow.Core.Tests.csproj -c Release --no-build

# One cloud: unit tests + TCK on the emulator (started by Testcontainers if the variables are unset)
dotnet test --project tests/Cloud/SourceFlow.Cloud.Tests/SourceFlow.Cloud.Tests.csproj -c Release --no-build \
  --filter-trait "Cloud=Aws" --filter-not-trait "Requires=RealCloud" --filter-not-trait "Requires=Terraform"

# End-to-end scenarios over the emulators (PostgreSQL through Testcontainers)
dotnet test --project tests/EndToEnd/SourceFlow.EndToEnd.Tests/SourceFlow.EndToEnd.Tests.csproj -c Release --no-build \
  --filter-not-trait "Category=Chaos" --filter-not-trait "Category=Performance" --filter-not-trait "Requires=RealCloud"
```

Use `Cloud=Azure` or `Cloud=Gcp` for the other transports. Add `--ignore-exit-code 8` when a filter may select no tests in a project.

## End-to-end scenarios

The end-to-end suite starts real SourceFlow services, each with its own PostgreSQL schema and its own transport client, and drives them across a broker.

### `BrokerConnection`

[`BrokerConnection`](../../../tests/EndToEnd/SourceFlow.EndToEnd.Tests/BrokerConnection.cs) is one run's connection to a broker (`Broker.InMemory`, `Aws`, `Gcp` or `Azure`), shared by the services of a system:

- `EnsureStartedAsync(servers, broker)` starts the emulator the leg needs, unless the leg runs on the real cloud.
- `CreateTransport()` returns a new transport client per service host: an `InMemoryBroker` transport, an emulator client (LocalStack with `test`/`test` credentials and `ReceiveWaitTime = 1 s`, the Pub/Sub emulator over an insecure gRPC channel, the Service Bus emulator with both connection strings), or a real-cloud client.
- Physical names carry a per-run prefix, so runs on a shared emulator or account do not collide. Disposal deletes every queue, topic and subscription with that prefix.
- `DuplicatingTransport` wraps a transport so that every send and publish happens twice: the duplicate delivery the receiving services must absorb.

### Scenarios

| Ids | Scenario | Brokers |
|-----|----------|---------|
| E2E-17..20 | An integration event reaches every subscribing service once (one subscription per service; Notifications runs two competing instances, Loyalty is a second subscriber). | In memory, AWS, Google Cloud, Azure |
| E2E-21..24 | A command routed by Accounts is decided once in its owner's (Notifications') stream although every message is delivered twice. | In memory, AWS, Google Cloud, Azure |
| WF-E2E-01 | OrderFulfilment happy path: reserved, charged, shipped, fulfilled. | In memory, AWS, Google Cloud, Azure (PostgreSQL) |
| WF-E2E-02 | Payment declined twice, then captured. | All four |
| WF-E2E-03 | Payment declined four times, compensated in reverse order. | All four |
| WF-E2E-04 | Out of stock: compensated with nothing to undo. | In-memory broker; in-memory store and PostgreSQL |
| WF-E2E-05 | Payment times out three times: compensated. | In-memory broker; both stores |
| WF-E2E-06 | A failing compensation needs an operator. | In-memory broker; both stores |
| WF-E2E-07 | An operator resume finishes the compensation. | In-memory broker; both stores |
| WF-E2E-08 | A large order is reviewed, then shipped. | In-memory broker; both stores |
| WF-E2E-09 | An invalid address after the pivot is recovered forward. | In-memory broker; both stores |
| WF-E2E-10 | Workflow version 2 starts new orders while version 1 finishes; version 1 cannot be removed until then. | PostgreSQL |
| WF-E2E-11 | Duplicate delivery everywhere: every command, reply and awaited event arrives twice, and each transition happens once. | AWS, Google Cloud, Azure |
| WF-E2E-12 | A crash at each fault point; the next node finishes the workflow once (chaos, nightly). | PostgreSQL |
| MIG-10..12 | The v2 wire bridge: a mapped v2 command is decided with a deterministic id and an unmapped one parked; without `AcceptV2Messages`, v2 messages are neither handled nor parked. | LocalStack |
| TCK-41, TCK-43 (with protection) | Accounts publishes a protected integration event; Notifications and Loyalty decrypt it; a tampered message is parked as `DecryptionFailed`. | In memory with the local key provider; LocalStack with AWS KMS |
| PERF-07 | Outbox relay latency, commit to broker acknowledgement, target p95 at most 250 ms. | AWS, Google Cloud, Azure emulators (nightly) |

The other scenarios in the project (the Bank scenarios E2E-01..15, read-model rebuilds and the query API) run on the in-memory broker; they test the core and storage rather than the transports.

## Real-cloud runs

Setting `SOURCEFLOW_REAL_CLOUD` to `aws`, `azure` or `gcp` moves that cloud's tests from the emulator to the real service:

- The `*RealCloudConformanceTests` classes run TCK-01..45 and skip the fault-injection cases (TCK-32..34); without the variable they are skipped.
- In the end-to-end suite, `BrokerConnection.RealCloud(broker)` puts the matching leg (for example every `_Aws` scenario) on the real cloud instead of the emulator.

| Cloud | Credentials | Required variables |
|-------|-------------|--------------------|
| AWS | The SDK credential chain | `SOURCEFLOW_REAL_CLOUD=aws`, `AWS_REGION` |
| Azure | `DefaultAzureCredential` | `SOURCEFLOW_REAL_CLOUD=azure`, `AZURE_SERVICEBUS_NAMESPACE` (a host name) |
| Google Cloud | Application Default Credentials | `SOURCEFLOW_REAL_CLOUD=gcp`, `GOOGLE_CLOUD_PROJECT` |

The real KMS round trips (`RealKmsTests`) run when their key variable is set and are skipped otherwise:

| Variable | Value |
|----------|-------|
| `SOURCEFLOW_KMS_AWS_KEY` | An AWS KMS key id, ARN or alias (with `AWS_REGION`) |
| `SOURCEFLOW_KMS_AZURE_KEY` | A Key Vault key id, `https://{vault}.vault.azure.net/keys/{name}` |
| `SOURCEFLOW_KMS_GCP_KEY` | `projects/{p}/locations/{l}/keyRings/{r}/cryptoKeys/{k}` |

Every real-cloud run creates its resources under a per-run prefix and deletes them at the end. If a run is killed, leftovers carry that prefix (`tck-…` for TCK fixtures) and can be deleted by it. The identity needs create and delete permissions in the sandbox, because the tests provision with `CreateIfMissing`.

```bash
SOURCEFLOW_REAL_CLOUD=aws AWS_REGION=eu-west-1 \
dotnet test --project tests/Cloud/SourceFlow.Cloud.Tests/SourceFlow.Cloud.Tests.csproj -c Release --filter-trait "Cloud=Aws"
```

## Chaos tests

Chaos tests carry `Category=Chaos` and run nightly. `ChaosScenarios` (CH-01..10) run Accounts and Notifications on PostgreSQL over the in-memory broker, using `ChaosInjector` (from `SourceFlow.Testing`) to crash or freeze a node at a fault point and the broker's fault injection for outages. Each checks the same outcome: no command or event is lost, every duplicate is absorbed, and the read models converge.

| Id | Scenario |
|----|----------|
| CH-01 | Crash before commit: the retry is the only commit. |
| CH-02 | Crash after commit, before the acknowledgement: the retry is a duplicate. |
| CH-03 | Crash after publish, before the outbox row is deleted: the republish is absorbed. |
| CH-04 | Crash after a projection batch is applied, before its checkpoint commits: the batch is applied again, once. |
| CH-05 | Crash before lease renewal: another node takes over. |
| CH-06 | Crash before completing a received message: the redelivery is absorbed. |
| CH-07 | Broker outage: commands keep succeeding, the outbox health check degrades, and the outbox drains afterwards. |
| CH-08 | Database restart: fails fast, then resumes without duplicates. |
| CH-09 | A stalled node loses its lease, and its late commit is fenced. |
| CH-10 | A long write transaction holds the horizon, and nothing is skipped afterwards. |

WF-E2E-12 applies the same crash points to the OrderFulfilment workflow.

## Infrastructure-as-code tests

`Topology/` in the cloud test project checks the topology exports and the modules in [`deploy/topology`](../../../deploy/topology/README.md):

- `TopologyFixtureDriftTests` fail when a committed fixture export differs from what the libraries produce (`SOURCEFLOW_APPROVE=1` rewrites them).
- `TopologyDescribeTests` check each transport's `Describe` output.
- `TopologyIacTests` (`Requires=Terraform`) apply the Terraform modules to LocalStack and the Pub/Sub emulator and check the result with `ProvisioningMode.Validate`, run each module's `terraform test` suite, and build the Bicep module for every fixture. Azure has no ARM emulator, so its modules are checked with mock providers and `bicep build`.

## CI pipelines

### Pull requests and pushes to `v3.0.0` (`.github/workflows/v3-pr.yml`)

| Job | What runs | Emulators |
|-----|-----------|-----------|
| `build` | Build on Ubuntu and Windows with warnings as errors; AOT publish check of the Bank API | — |
| `unit` | Every test except `Requires=Docker`, `LocalStack`, `PubSubEmulator`, `ServiceBusEmulator`, `RealCloud`, `Terraform` and `Category=Chaos`, `Performance`. Includes the in-memory TCK. | — |
| `storage` | Storage conformance kit (Testcontainers) | — |
| `transports (aws)` | `SourceFlow.Cloud.Tests` with `Cloud=Aws`, excluding `RealCloud` and `Terraform` | LocalStack |
| `transports (gcp)` | The same with `Cloud=Gcp` | Pub/Sub emulator |
| `transports (azure)` | The same with `Cloud=Azure` | Service Bus emulator |
| `iac` | `terraform fmt`, Prometheus rules check, then `Requires=Terraform` tests | LocalStack, Pub/Sub emulator; Bicep CLI |
| `e2e` | `SourceFlow.EndToEnd.Tests` excluding `Chaos`, `Performance` and `RealCloud` | All three |
| `coverage` | Merges coverage from unit, storage, transports and e2e and applies the coverage gate | — |
| `api-traceability`, `pack` | Public API approval, manifest drift, package layout | — |

Each transports leg starts its emulator with `docker compose -f .github/emulators/{name}/docker-compose.yml up -d`, waits on the health gate, and sets the environment variables listed in [Emulators](#emulators). `MSSQL_SA_PASSWORD` comes from the `EMULATOR_SA_PASSWORD` secret, with a fixed fallback for forks.

### Nightly (`.github/workflows/v3-nightly.yml`, 02:00 UTC and on demand)

| Job | What runs |
|-----|-----------|
| `real cloud (aws / azure / gcp)` | Runs only when the repository variable `SOURCEFLOW_REAL_CLOUD` is `true`. Authenticates with OIDC federation (no long-lived secrets): `aws-actions/configure-aws-credentials` with `AWS_TEST_ROLE_ARN` and `AWS_TEST_REGION`; `azure/login` with `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`; `google-github-actions/auth` with `GCP_WIF_PROVIDER` and `GCP_TEST_SERVICE_ACCOUNT`. Then runs `SourceFlow.Cloud.Tests` and `SourceFlow.EndToEnd.Tests` filtered to `Cloud={Aws|Azure|Gcp}` with `SOURCEFLOW_REAL_CLOUD` set to the cloud, plus `AZURE_SERVICEBUS_NAMESPACE` and the three `SOURCEFLOW_KMS_*` keys from repository variables. |
| `chaos` | `SourceFlow.EndToEnd.Tests` with `Category=Chaos` |
| `relay latency (emulators)` | `Category=Performance` (PERF-07) on all three emulators |
| `database-matrix`, `benchmarks`, `load-tier-m`, `dast`, `long-properties` | Storage matrix, benchmarks, load test, OWASP ZAP and long property runs (not transport-specific) |

The weekly workflow (`v3-weekly.yml`) runs the fuzzers. See [GitHub Actions Setup](GitHub-Actions-Setup.md) for configuring the secrets, variables and cloud identities these jobs need.

## Troubleshooting

| Symptom | Likely cause | What to do |
|---------|--------------|------------|
| Cloud tests hang at start, then fail on Docker | No Docker and no emulator variable set | Start Docker, or start the emulator and set its variable. |
| `transports (azure)` fails the health gate | SQL Edge did not start (weak `MSSQL_SA_PASSWORD`) | Use a password that meets SQL Server's complexity rules. |
| Service Bus emulator tests throttled or timing out | A busy emulator namespace | Keep the client's retries on; run one Azure suite at a time. |
| `MessageTimeToLive` errors on the Service Bus emulator | A time to live over 1 hour | Set `MessageTimeToLive = TimeSpan.FromHours(1)` for emulator runs. |
| Pub/Sub image pull fails | The pinned `-emulators` tag was pruned | Bump the pin in `.github/emulators/pubsub` and `PubSubEmulatorServer.Image`. |
| `TopologyFixtureDriftTests` fails after a naming or provisioning change | The committed exports are stale | Rerun with `SOURCEFLOW_APPROVE=1` and review the diff. |
| Real-cloud cases all skipped | `SOURCEFLOW_REAL_CLOUD` unset or set to another cloud | Set it to `aws`, `azure` or `gcp` with that cloud's variables. |
| A TCK case fails only on one emulator with a timing message | Emulator latency above the fixture's tolerances | Compare with the in-memory run; adjust `ReceiveTimeout`, `QuietPeriod` or `DelayTolerance` in the fixture only when the broker's documented behaviour allows it. |

## Reference

- Architecture: [Cloud Transports](Architecture/07-Cloud-Transports.md)
- Transports: [AWS](SourceFlow.Cloud.Aws-README.md), [Azure](SourceFlow.Cloud.Azure-README.md), [Google Cloud](SourceFlow.Cloud.Gcp-README.md)
- Toolkit: [SourceFlow.Testing](SourceFlow.Testing-README.md)
- Design: [07 Cloud Transports §7](../Design/07-Cloud-Transports.md#7-transport-conformance-kit-tck), [11 Testing and Quality](../Design/11-Testing-and-Quality.md)
- ADRs: [ADR-014](../ADRs/ADR-014-transport-spi-and-conformance-kit-parity-required.md), [ADR-033](../ADRs/ADR-033-final-transport-spi-pull-receivers-and-the-in-memory-reference-broker.md)
- Emulator pins: [`.github/emulators/README.md`](../../../.github/emulators/README.md)

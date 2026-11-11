# Emulators for v3 transport tests

| Emulator | Folder | Image (pinned) | Health gate |
|----------|--------|----------------|-------------|
| LocalStack (SQS, SNS, KMS) | `localstack/` | `localstack/localstack:3.8` | `/_localstack/health` reports sqs, sns and kms `available` |
| Google Pub/Sub emulator | `pubsub/` | `gcr.io/google.com/cloudsdktool/google-cloud-cli:590.0.1-emulators` | topic create succeeds |
| Azure Service Bus emulator | `servicebus/` | `mcr.microsoft.com/azure-messaging/servicebus-emulator:2.0.1` + `mcr.microsoft.com/azure-sql-edge:2.0.0` | emulator health endpoint + queue round trip |

Tests prefer Testcontainers modules and fall back to these compose files (plan 08 §5). The AWS
tests (`tests/Cloud/SourceFlow.Cloud.Tests/Aws`) use the LocalStack in `AWS_ENDPOINT_URL` when it
is set, as the PR transports job does, and otherwise start the pinned image with Testcontainers.
The GCP tests (`tests/Cloud/SourceFlow.Cloud.Tests/Gcp`) likewise use the emulator in
`PUBSUB_EMULATOR_HOST` when it is set, and otherwise start the pinned image. Renovate PRs that bump an
image need a manual licence review (LocalStack moved to a licensed `latest` image, which broke v2 CI).
The `google-cloud-cli` registry keeps about a year of `-emulators` tags, so the Pub/Sub pin must be
bumped at least yearly (`502.0.0` had been pruned when WP-3.3 re-pinned it).

The Service Bus emulator (2.x) creates entities at runtime through its management endpoint (port 5300), so
`servicebus/Config.json` declares the namespace only and every test provisions its own queues and topics, as
on Azure. The Azure tests use `SERVICEBUS_EMULATOR_CONNECTION` (AMQP) and
`SERVICEBUS_EMULATOR_ADMIN_CONNECTION` (management) when set, and otherwise start the pinned images with
Testcontainers. The emulator's limits are tighter than Azure's: a time to live of at most 1 hour, a
`MaxDeliveryCount` of at most 10, a duplicate-detection window of at most 5 minutes, and throttling of
a busy namespace (so the fixture keeps the client's retries on).

```bash
docker compose -f .github/emulators/localstack/docker-compose.yml up -d
docker compose -f .github/emulators/pubsub/docker-compose.yml up -d
MSSQL_SA_PASSWORD='Your_strong_pw1' docker compose -f .github/emulators/servicebus/docker-compose.yml up -d
```

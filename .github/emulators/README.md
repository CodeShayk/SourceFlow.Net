# Emulators for v3 transport tests

| Emulator | Folder | Image (pinned) | Health gate |
|----------|--------|----------------|-------------|
| LocalStack (SQS, SNS, KMS) | `localstack/` | `localstack/localstack:3.8` | `/_localstack/health` reports sqs, sns and kms `available` |
| Google Pub/Sub emulator | `pubsub/` | `gcr.io/google.com/cloudsdktool/google-cloud-cli:590.0.1-emulators` | topic create succeeds |
| Azure Service Bus emulator | `servicebus/` | `mcr.microsoft.com/azure-messaging/servicebus-emulator:1.1.2` + `mcr.microsoft.com/azure-sql-edge:2.0.0` | emulator health endpoint + queue round trip |

Tests prefer Testcontainers modules and fall back to these compose files (plan 08 §5). The AWS
tests (`tests/Cloud/SourceFlow.Cloud.Tests/Aws`) use the LocalStack in `AWS_ENDPOINT_URL` when it
is set, as the PR transports job does, and otherwise start the pinned image with Testcontainers.
The GCP tests (`tests/Cloud/SourceFlow.Cloud.Tests/Gcp`) likewise use the emulator in
`PUBSUB_EMULATOR_HOST` when it is set, and otherwise start the pinned image. Renovate PRs that bump an
image need a manual licence review (LocalStack moved to a licensed `latest` image, which broke v2 CI).
The `google-cloud-cli` registry keeps about a year of `-emulators` tags, so the Pub/Sub pin must be
bumped at least yearly (`502.0.0` had been pruned when WP-3.3 re-pinned it).

The Service Bus emulator cannot create entities at runtime, so every queue, topic and
subscription the transport conformance kit uses is declared in `servicebus/Config.json`, and the
Azure TCK fixture runs with `ProvisioningMode.Validate` (WP-3.4).

```bash
docker compose -f .github/emulators/localstack/docker-compose.yml up -d
docker compose -f .github/emulators/pubsub/docker-compose.yml up -d
MSSQL_SA_PASSWORD='Your_strong_pw1' docker compose -f .github/emulators/servicebus/docker-compose.yml up -d
```

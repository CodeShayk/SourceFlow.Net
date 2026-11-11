# Emulators for v3 transport tests

| Emulator | Folder | Image (pinned) | Health gate |
|----------|--------|----------------|-------------|
| LocalStack (SQS, SNS, KMS) | `localstack/` | `localstack/localstack:3.8` | `/_localstack/health` reports sqs, sns and kms `available` |
| Google Pub/Sub emulator | `pubsub/` | `gcr.io/google.com/cloudsdktool/google-cloud-cli:502.0.0-emulators` | topic create succeeds |
| Azure Service Bus emulator | `servicebus/` | `mcr.microsoft.com/azure-messaging/servicebus-emulator:1.1.2` + `mcr.microsoft.com/azure-sql-edge:2.0.0` | emulator health endpoint + queue round trip |

Tests prefer Testcontainers modules and fall back to these compose files (plan 08 §5).
Renovate PRs that bump an image need a manual licence review (LocalStack moved to a licensed
`latest` image, which broke v2 CI).

The Service Bus emulator cannot create entities at runtime, so every queue, topic and
subscription the transport conformance kit uses is declared in `servicebus/Config.json`, and the
Azure TCK fixture runs with `ProvisioningMode.Validate` (WP-3.4).

```bash
docker compose -f .github/emulators/localstack/docker-compose.yml up -d
docker compose -f .github/emulators/pubsub/docker-compose.yml up -d
MSSQL_SA_PASSWORD='Your_strong_pw1' docker compose -f .github/emulators/servicebus/docker-compose.yml up -d
```

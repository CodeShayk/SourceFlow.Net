# Bugfix Requirements Document

## Introduction

The AWS cloud integration tests in `SourceFlow.Cloud.AWS.Tests` are failing in the GitHub Actions CI environment due to LocalStack container startup timeouts. Tests that work successfully in local development environments consistently fail in CI with "LocalStack services did not become ready within 00:00:30" errors. Additionally, parallel test execution causes port conflicts when multiple tests attempt to start LocalStack containers simultaneously on the same port (4566).

This bug prevents the CI pipeline from validating AWS integration functionality and blocks the v2.0.0 release preparation. The issue is specific to the containerized GitHub Actions environment and does not occur in local development.

## Bug Analysis

### Current Behavior (Defect)

1.1 WHEN LocalStack containers start in GitHub Actions CI THEN the health check endpoint `/_localstack/health` does not return "available" status for services (sqs, sns, kms, iam) within the 30-second timeout window

1.2 WHEN multiple integration tests run in parallel in GitHub Actions THEN port 4566 allocation conflicts occur with error "port is already allocated"

1.3 WHEN the health check timeout expires (30 seconds) THEN tests fail with `TimeoutException` stating "LocalStack services did not become ready within 00:00:30"

1.4 WHEN tests use the `[Collection("AWS Integration Tests")]` attribute THEN they still attempt to start separate LocalStack instances instead of sharing a single instance

1.5 WHEN LocalStack containers start in GitHub Actions THEN the container startup wait strategy may not account for slower container initialization in CI environments compared to local development

### Expected Behavior (Correct)

2.1 WHEN LocalStack containers start in GitHub Actions CI THEN all configured services (sqs, sns, kms, iam) SHALL report "available" status within a reasonable timeout period appropriate for CI environments

2.2 WHEN multiple integration tests run in parallel THEN they SHALL share a single LocalStack container instance to avoid port conflicts

2.3 WHEN health checks are performed THEN the timeout and retry configuration SHALL be sufficient for GitHub Actions container startup times

2.4 WHEN tests use the `[Collection("AWS Integration Tests")]` attribute THEN xUnit SHALL enforce sequential execution or shared fixture usage to prevent resource conflicts

2.5 WHEN LocalStack services are slow to initialize THEN the wait strategy SHALL include appropriate delays and retry logic to accommodate CI environment performance characteristics

2.6 WHEN a LocalStack container is already running (external instance) THEN tests SHALL detect and reuse it instead of attempting to start a new container

### Unchanged Behavior (Regression Prevention)

3.1 WHEN integration tests run in local development environments THEN they SHALL CONTINUE TO pass with existing timeout configurations

3.2 WHEN LocalStack containers start successfully THEN service validation (SQS ListQueues, SNS ListTopics, KMS ListKeys, IAM ListRoles) SHALL CONTINUE TO execute correctly

3.3 WHEN tests complete THEN LocalStack containers SHALL CONTINUE TO be properly cleaned up with `AutoRemove = true`

3.4 WHEN port conflicts are detected THEN the `FindAvailablePortAsync` method SHALL CONTINUE TO find alternative ports

3.5 WHEN tests use `IAsyncLifetime` initialization THEN the test lifecycle management SHALL CONTINUE TO function correctly

3.6 WHEN LocalStack health endpoint returns service status THEN the JSON deserialization and status parsing SHALL CONTINUE TO work correctly

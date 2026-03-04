# Running AWS Cloud Integration Tests

## Overview

The AWS integration tests are categorized to allow flexible test execution based on available infrastructure. Tests can be run with or without AWS services.

## Test Categories

### Unit Tests (`Category=Unit`)
Tests with no external dependencies. These use mocked services and run quickly without requiring any AWS infrastructure.

**Examples:**
- `AwsBusBootstrapperTests` - Mocked SQS/SNS clients
- `AwsSqsCommandDispatcherTests` - Mocked SQS client
- `AwsSnsEventDispatcherTests` - Mocked SNS client
- `PropertyBasedTests` - Pure logic validation
- `BusConfigurationTests` - Configuration validation only

### Integration Tests (`Category=Integration`)
Tests that require external AWS services (LocalStack emulator or real AWS).

**Subcategories:**
- `RequiresLocalStack` - Tests designed for LocalStack emulator
- `RequiresAWS` - Tests requiring real AWS services

## Running Tests

### Run Only Unit Tests (Recommended for Quick Validation)
```bash
dotnet test --filter "Category=Unit"
```

**Benefits:**
- No AWS infrastructure required
- Fast execution (< 10 seconds)
- Perfect for CI/CD pipelines
- Validates code logic and structure

### Run All Tests (Requires AWS Infrastructure)
```bash
dotnet test
```

**Note:** Integration tests will fail with clear error messages if AWS services are unavailable.

### Skip Integration Tests
```bash
dotnet test --filter "Category!=Integration"
```

### Skip LocalStack-Dependent Tests
```bash
dotnet test --filter "Category!=RequiresLocalStack"
```

### Skip Real AWS-Dependent Tests
```bash
dotnet test --filter "Category!=RequiresAWS"
```

## Test Behavior Without AWS Services

When AWS services are unavailable, integration tests will:

1. **Check connectivity** with a 5-second timeout
2. **Fail fast** with a clear error message
3. **Provide actionable guidance** on how to fix the issue

### Example Error Message

```
Test skipped: LocalStack emulator is not available.

Options:
1. Start LocalStack:
   docker run -d -p 4566:4566 localstack/localstack
   OR
   localstack start

2. Skip integration tests:
   dotnet test --filter "Category!=Integration"

For more information, see: tests/SourceFlow.Cloud.AWS.Tests/README.md
```

## Setting Up AWS Services

### Option 1: LocalStack Emulator (Local Development - Recommended)

LocalStack provides a fully functional local AWS cloud stack for development and testing.

```bash
# Option A: Docker (Recommended)
docker run -d -p 4566:4566 localstack/localstack

# Option B: LocalStack CLI
pip install localstack
localstack start
```

**LocalStack Features:**
- Full SQS support (standard and FIFO queues)
- Full SNS support (topics and subscriptions)
- KMS support for encryption
- No AWS account required
- No costs
- Fast local execution

### Option 2: Real AWS Services

Configure environment variables to point to real AWS resources:

```bash
# AWS Credentials
set AWS_ACCESS_KEY_ID=your-access-key
set AWS_SECRET_ACCESS_KEY=your-secret-key
set AWS_REGION=us-east-1

# Optional: Custom endpoint for LocalStack
set AWS_ENDPOINT_URL=http://localhost:4566
```

**Required AWS Resources:**
1. SQS queues (standard and FIFO)
2. SNS topics
3. KMS keys for encryption
4. IAM permissions for SQS, SNS, and KMS operations

## CI/CD Integration

### GitHub Actions Example

```yaml
- name: Start LocalStack
  run: docker run -d -p 4566:4566 localstack/localstack

- name: Wait for LocalStack
  run: |
    timeout 30 bash -c 'until curl -s http://localhost:4566/_localstack/health; do sleep 1; done'

- name: Run Unit Tests
  run: dotnet test --filter "Category=Unit" --logger "trx"

- name: Run Integration Tests
  run: dotnet test --filter "Category=Integration" --logger "trx"
  env:
    AWS_ENDPOINT_URL: http://localhost:4566
    AWS_ACCESS_KEY_ID: test
    AWS_SECRET_ACCESS_KEY: test
    AWS_REGION: us-east-1
```

### Azure DevOps Example

```yaml
- script: docker run -d -p 4566:4566 localstack/localstack
  displayName: 'Start LocalStack'

- task: DotNetCoreCLI@2
  displayName: 'Run Unit Tests'
  inputs:
    command: 'test'
    arguments: '--filter "Category=Unit" --logger trx'

- task: DotNetCoreCLI@2
  displayName: 'Run Integration Tests'
  inputs:
    command: 'test'
    arguments: '--filter "Category=Integration" --logger trx'
  env:
    AWS_ENDPOINT_URL: http://localhost:4566
    AWS_ACCESS_KEY_ID: test
    AWS_SECRET_ACCESS_KEY: test
    AWS_REGION: us-east-1
```

## Performance Characteristics

### Unit Tests
- **Duration:** ~5-10 seconds
- **Tests:** 40+ tests
- **Infrastructure:** None required

### Integration Tests (with LocalStack)
- **Duration:** ~2-5 minutes
- **Tests:** 60+ tests
- **Infrastructure:** LocalStack required

### Integration Tests (with Real AWS)
- **Duration:** ~5-10 minutes (depends on AWS latency)
- **Tests:** 60+ tests
- **Infrastructure:** Real AWS services required

## Troubleshooting

### Tests Hang Indefinitely
**Cause:** Old behavior before timeout fix was implemented.

**Solution:** 
1. Kill any hanging test processes: `taskkill /F /IM testhost.exe`
2. Rebuild the project: `dotnet build --no-restore`
3. Run unit tests only: `dotnet test --filter "Category=Unit"`

### Connection Timeout Errors
**Cause:** AWS services are not available or not configured.

**Solution:**
- For local development: Start LocalStack or skip integration tests with `--filter "Category!=Integration"`
- For CI/CD: Configure LocalStack or real AWS services
- For full testing: Set up LocalStack (recommended) or real AWS services

### LocalStack Not Starting
**Cause:** Port 4566 already in use or Docker not running.

**Solution:**
```bash
# Check if port is in use
netstat -ano | findstr :4566

# Stop existing LocalStack
docker stop $(docker ps -q --filter ancestor=localstack/localstack)

# Start fresh LocalStack
docker run -d -p 4566:4566 localstack/localstack
```

### Compilation Errors
**Cause:** Missing dependencies or outdated packages.

**Solution:**
```bash
dotnet restore
dotnet build
```

## Best Practices

1. **Local Development:** Run unit tests frequently (`dotnet test --filter "Category=Unit"`)
2. **Pre-Commit:** Run all unit tests to ensure code quality
3. **CI/CD Pipeline:** Run unit tests on every commit, integration tests with LocalStack
4. **Integration Testing:** Use LocalStack for most testing, real AWS for final validation
5. **Cost Optimization:** Use LocalStack to avoid AWS costs during development

## LocalStack vs Real AWS

### Use LocalStack When:
- ✅ Developing locally
- ✅ Running CI/CD pipelines
- ✅ Testing basic functionality
- ✅ Avoiding AWS costs
- ✅ Need fast feedback loops

### Use Real AWS When:
- ✅ Testing production-like scenarios
- ✅ Validating IAM permissions
- ✅ Testing cross-region functionality
- ✅ Performance testing at scale
- ✅ Final validation before deployment

## Summary

The test categorization system allows you to:
- ✅ Run fast unit tests without any infrastructure
- ✅ Skip integration tests when AWS is unavailable
- ✅ Get clear error messages with actionable guidance
- ✅ Integrate easily with CI/CD pipelines
- ✅ Avoid indefinite hangs with 5-second connection timeouts
- ✅ Use LocalStack for cost-effective local testing

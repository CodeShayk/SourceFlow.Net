# SourceFlow Cloud Integration Tests

This project provides comprehensive cross-cloud integration testing for SourceFlow's AWS and Azure cloud extensions. It validates multi-cloud scenarios, hybrid processing, and cross-cloud message routing.

## Overview

The integration test suite covers:

- **Cross-Cloud Messaging**: Commands sent from AWS to Azure and vice versa
- **Hybrid Cloud Processing**: Local processing with cloud persistence and messaging
- **Multi-Cloud Failover**: Automatic failover between AWS and Azure services
- **Security Integration**: End-to-end encryption across cloud providers
- **Performance Benchmarking**: Throughput and latency across cloud boundaries
- **Resilience Testing**: Circuit breakers, dead letter handling, and retry policies

## Test Categories

### CrossCloud Tests
- `AwsToAzureTests.cs` - AWS SQS to Azure Service Bus message routing
- `AzureToAwsTests.cs` - Azure Service Bus to AWS SNS message routing
- `MultiCloudFailoverTests.cs` - Failover scenarios between cloud providers

### Performance Tests
- `ThroughputBenchmarks.cs` - Message throughput across cloud boundaries
- `LatencyBenchmarks.cs` - End-to-end latency measurements
- `ScalabilityTests.cs` - Performance under increasing load

### Security Tests
- `EncryptionComparisonTests.cs` - AWS KMS vs Azure Key Vault encryption
- `AccessControlTests.cs` - Cross-cloud authentication and authorization
- `SensitiveDataTests.cs` - Sensitive data masking across providers

## Test Infrastructure

### Test Fixtures
- `CrossCloudTestFixture` - Manages both AWS and Azure test environments
- `PerformanceMeasurement` - Standardized performance metrics collection
- `SecurityTestHelpers` - Cross-cloud security validation utilities

### Configuration
Tests support multiple execution modes:
- **Local Emulators**: LocalStack + Azurite for development
- **Cloud Integration**: Real AWS and Azure services
- **Hybrid Mode**: Mix of local and cloud services

## Prerequisites

### Local Development
- Docker Desktop (for LocalStack and Azurite containers)
- .NET 9.0 SDK
- Visual Studio 2022 or VS Code

### Cloud Testing
- AWS Account with SQS, SNS, and KMS permissions
- Azure Subscription with Service Bus and Key Vault access
- Appropriate IAM roles and managed identities configured

## Configuration

### appsettings.json
```json
{
  "CloudIntegrationTests": {
    "UseEmulators": true,
    "RunPerformanceTests": false,
    "Aws": {
      "UseLocalStack": true,
      "Region": "us-east-1"
    },
    "Azure": {
      "UseAzurite": true,
      "FullyQualifiedNamespace": "test.servicebus.windows.net"
    }
  }
}
```

### Environment Variables
- `AWS_ACCESS_KEY_ID` - AWS access key (for cloud testing)
- `AWS_SECRET_ACCESS_KEY` - AWS secret key (for cloud testing)
- `AZURE_CLIENT_ID` - Azure managed identity client ID
- `AZURE_TENANT_ID` - Azure tenant ID

## Running Tests

### All Tests
```bash
dotnet test
```

### Specific Categories
```bash
# Cross-cloud integration tests only
dotnet test --filter Category=CrossCloud

# Performance tests only
dotnet test --filter Category=Performance

# Security tests only
dotnet test --filter Category=Security
```

### Local Development
```bash
# Run with emulators (default)
dotnet test --configuration Debug

# Skip performance tests for faster execution
dotnet test --filter "Category!=Performance"
```

### CI/CD Pipeline
```bash
# Full test suite with cloud services
dotnet test --configuration Release --logger trx --collect:"XPlat Code Coverage"
```

## Test Scenarios

### Cross-Cloud Message Flow
1. **AWS to Azure**: Command dispatched via AWS SQS → Processed locally → Event published to Azure Service Bus
2. **Azure to AWS**: Command dispatched via Azure Service Bus → Processed locally → Event published to AWS SNS
3. **Hybrid Processing**: Local command processing with cloud persistence and event distribution

### Failover Scenarios
1. **Primary Cloud Failure**: Automatic failover from AWS to Azure when AWS services are unavailable
2. **Secondary Cloud Recovery**: Automatic failback when primary cloud services recover
3. **Partial Service Failure**: Graceful degradation when specific services (SQS, Service Bus) fail

### Security Scenarios
1. **Cross-Cloud Encryption**: Messages encrypted with AWS KMS, decrypted in Azure environment
2. **Key Rotation**: Seamless key rotation across cloud providers
3. **Access Control**: Proper authentication using IAM roles and managed identities

### Performance Scenarios
1. **Throughput Testing**: Maximum messages per second across cloud boundaries
2. **Latency Testing**: End-to-end message processing times
3. **Scalability Testing**: Performance under increasing concurrent load

## Troubleshooting

### Common Issues

#### LocalStack Connection Issues
```bash
# Check LocalStack status
docker ps | grep localstack

# View LocalStack logs
docker logs <localstack-container-id>

# Restart LocalStack
docker restart <localstack-container-id>
```

#### Azurite Connection Issues
```bash
# Check Azurite status
docker ps | grep azurite

# View Azurite logs
docker logs <azurite-container-id>
```

#### Cloud Service Authentication
- Verify AWS credentials: `aws sts get-caller-identity`
- Verify Azure authentication: `az account show`
- Check IAM roles and managed identity permissions

### Performance Test Issues
- Ensure adequate system resources for load testing
- Monitor container resource limits
- Check network connectivity and bandwidth

### Test Data Cleanup
Tests automatically clean up resources, but manual cleanup may be needed:

```bash
# AWS cleanup (LocalStack)
aws --endpoint-url=http://localhost:4566 sqs list-queues
aws --endpoint-url=http://localhost:4566 sns list-topics

# Azure cleanup (Azurite)
# Resources are automatically cleaned up when containers stop
```

## Contributing

When adding new cross-cloud test scenarios:

1. Follow the existing test patterns and naming conventions
2. Use the shared test fixtures and utilities
3. Include both unit tests and property-based tests
4. Add appropriate test categories and documentation
5. Ensure tests work with both emulators and cloud services
6. Include performance benchmarks for new scenarios

## Architecture

The test project follows SourceFlow's testing patterns:

```
tests/SourceFlow.Cloud.Integration.Tests/
├── CrossCloud/           # Cross-cloud messaging tests
├── Performance/          # Performance and scalability tests
├── Security/            # Security and encryption tests
├── TestHelpers/         # Shared test utilities and fixtures
├── appsettings.json     # Test configuration
└── README.md           # This file
```

Each test category includes:
- **Unit Tests**: Specific scenarios with mocked dependencies
- **Integration Tests**: End-to-end tests with real/emulated services
- **Property Tests**: Randomized testing of universal properties
- **Performance Tests**: Benchmarking and load testing
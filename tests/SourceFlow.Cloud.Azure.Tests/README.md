# SourceFlow.Cloud.Azure.Tests

Comprehensive test suite for SourceFlow Azure cloud integration, providing validation for Service Bus messaging, Key Vault encryption, managed identity authentication, and performance characteristics.

## Test Categories

### Unit Tests (`Unit/`)
- **Service Bus Dispatchers**: Command and event dispatcher functionality
- **Configuration**: Routing configuration and options validation
- **Dependency Verification**: Ensures all testing dependencies are properly installed

### Integration Tests (`Integration/`)
- **Service Bus Integration**: End-to-end messaging with Azure Service Bus
- **Key Vault Integration**: Message encryption and decryption workflows
- **Managed Identity**: Authentication and authorization testing
- **Performance Integration**: Real-world performance validation

### Test Helpers (`TestHelpers/`)
- **Azure Test Environment**: Test environment management and configuration
- **Azurite Test Fixture**: Local Azure emulator setup and management
- **Service Bus Test Helpers**: Utilities for Service Bus testing scenarios

## Testing Dependencies

### Core Testing Framework
- **xUnit 2.9.2**: Primary testing framework with analyzers
- **Moq 4.20.72**: Mocking framework for unit tests
- **Microsoft.NET.Test.Sdk 17.12.0**: Test SDK and runner
- **coverlet.collector 6.0.2**: Code coverage collection

### Property-Based Testing
- **FsCheck 2.16.6**: Property-based testing library
- **FsCheck.Xunit 2.16.6**: xUnit integration for FsCheck
- Minimum 100 iterations per property test for comprehensive coverage

### Performance Testing
- **BenchmarkDotNet 0.14.0**: Performance benchmarking and profiling
- Throughput, latency, and resource utilization measurements
- Baseline establishment and regression detection

### Azure Integration Testing
- **TestContainers 4.0.0**: Container-based testing infrastructure
- **Testcontainers.Azurite 4.0.0**: Azure emulator for local development
- **Azure.Messaging.ServiceBus 7.18.1**: Service Bus client library
- **Azure.Security.KeyVault.Keys 4.6.0**: Key Vault key management
- **Azure.Security.KeyVault.Secrets 4.6.0**: Key Vault secret management
- **Azure.Identity 1.12.1**: Azure authentication and managed identity
- **Azure.ResourceManager 1.13.0**: Azure resource management
- **Azure.ResourceManager.ServiceBus 1.1.0**: Service Bus resource management

### Additional Utilities
- **Microsoft.Extensions.Configuration.Json 9.0.0**: Configuration management
- **Microsoft.Extensions.Hosting 9.0.0**: Hosted service testing
- **Microsoft.Extensions.Logging.Console 9.0.0**: Logging infrastructure

## Running Tests

### All Tests
```bash
dotnet test
```

### Specific Test Categories
```bash
# Unit tests only
dotnet test --filter "Category=Unit"

# Integration tests only
dotnet test --filter "Category=Integration"

# Property-based tests only
dotnet test --filter "Property"

# Performance tests only
dotnet test --filter "Category=Performance"
```

### With Coverage
```bash
dotnet test --collect:"XPlat Code Coverage"
```

## Test Configuration

### Local Development
Tests use Azurite emulator by default for local development:
- Service Bus emulation for messaging tests
- Key Vault emulation for encryption tests
- No Azure subscription required for basic testing

### Integration Testing
For full integration testing against real Azure services:
1. Configure Azure Service Bus connection string
2. Set up Key Vault with appropriate permissions
3. Configure managed identity or service principal
4. Set environment variables or update test configuration

### Environment Variables
```bash
# Azure Service Bus
AZURE_SERVICEBUS_CONNECTION_STRING="Endpoint=sb://..."
AZURE_SERVICEBUS_NAMESPACE="your-namespace.servicebus.windows.net"

# Azure Key Vault
AZURE_KEYVAULT_URL="https://your-vault.vault.azure.net/"

# Authentication
AZURE_CLIENT_ID="your-client-id"
AZURE_CLIENT_SECRET="your-client-secret"
AZURE_TENANT_ID="your-tenant-id"
```

## Test Patterns

### Property-Based Testing
```csharp
[Property]
public bool ServiceBus_Message_RoundTrip_Preserves_Content(string messageContent)
{
    // Property: Any message sent through Service Bus should be received unchanged
    var result = SendAndReceiveMessage(messageContent);
    return result.Content == messageContent;
}
```

### Performance Testing
```csharp
[Benchmark]
public async Task ServiceBus_Send_Command_Throughput()
{
    // Benchmark: Measure command sending throughput
    await _commandDispatcher.DispatchAsync(testCommand);
}
```

### Integration Testing
```csharp
[Fact]
public async Task ServiceBus_Integration_End_To_End_Message_Flow()
{
    // Integration: Complete message flow validation
    using var fixture = new AzureTestEnvironment();
    await fixture.InitializeAsync();
    
    // Test complete message flow
    var result = await fixture.SendCommandAndWaitForEvent();
    Assert.True(result.Success);
}
```

## Troubleshooting

### Common Issues

#### Azurite Connection Failures
- Ensure Azurite container is running
- Check port availability (default: 10000-10002)
- Verify container health status

#### Authentication Failures
- Verify managed identity configuration
- Check service principal permissions
- Validate Key Vault access policies

#### Performance Test Variations
- Run tests multiple times for baseline
- Consider system load and resource availability
- Use dedicated test environments for consistent results

### Debug Configuration
```json
{
  "Logging": {
    "LogLevel": {
      "SourceFlow.Cloud.Azure": "Debug",
      "Azure.Messaging.ServiceBus": "Information"
    }
  },
  "SourceFlow": {
    "Azure": {
      "UseAzurite": true,
      "EnableDetailedLogging": true
    }
  }
}
```

## Contributing

When adding new tests:
1. Follow existing test patterns and naming conventions
2. Include both unit and integration test coverage
3. Add property-based tests for universal behaviors
4. Document any new test dependencies or configuration
5. Ensure tests work in both local and CI/CD environments

## Requirements Validation

This test suite validates the following requirements from the cloud-integration-testing specification:
- **2.1**: Azure Service Bus command dispatching validation
- **2.2**: Azure Service Bus event publishing validation  
- **2.3**: Azure Key Vault encryption validation
- **2.4**: Azure health checks validation
- **2.5**: Azure performance testing validation
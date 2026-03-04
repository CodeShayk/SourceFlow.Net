# Running Azure Cloud Integration Tests

## Overview

The Azure integration tests are categorized to allow flexible test execution based on available infrastructure. Tests can be run with or without Azure services.

## Test Categories

### Unit Tests (`Category=Unit`)
Tests with no external dependencies. These use mocked services and run quickly without requiring any Azure infrastructure.

**Examples:**
- `AzureBusBootstrapperTests` - Mocked Service Bus administration
- `AzureServiceBusCommandDispatcherTests` - Mocked Service Bus client
- `AzureCircuitBreakerTests` - In-memory circuit breaker logic
- `DependencyVerificationTests` - Assembly scanning only

### Integration Tests (`Category=Integration`)
Tests that require external Azure services (Azurite emulator or real Azure).

**Subcategories:**
- `RequiresAzurite` - Tests designed for Azurite emulator
- `RequiresAzure` - Tests requiring real Azure services

## Running Tests

### Run Only Unit Tests (Recommended for Quick Validation)
```bash
dotnet test --filter "Category=Unit"
```

**Benefits:**
- No Azure infrastructure required
- Fast execution (< 10 seconds)
- Perfect for CI/CD pipelines
- Validates code logic and structure

### Run All Tests (Requires Azure Infrastructure)
```bash
dotnet test
```

**Note:** Integration tests will fail with clear error messages if Azure services are unavailable.

### Skip Integration Tests
```bash
dotnet test --filter "Category!=Integration"
```

### Skip Azurite-Dependent Tests
```bash
dotnet test --filter "Category!=RequiresAzurite"
```

### Skip Real Azure-Dependent Tests
```bash
dotnet test --filter "Category!=RequiresAzure"
```

## Test Behavior Without Azure Services

When Azure services are unavailable, integration tests will:

1. **Check connectivity** with a 5-second timeout
2. **Fail fast** with a clear error message
3. **Provide actionable guidance** on how to fix the issue

### Example Error Message

```
Test skipped: Azure Service Bus is not available.

Options:
1. Start Azurite emulator:
   npm install -g azurite
   azurite --silent --location c:\azurite

2. Configure real Azure Service Bus:
   set AZURE_SERVICEBUS_NAMESPACE=myservicebus.servicebus.windows.net
   OR
   set AZURE_SERVICEBUS_CONNECTION_STRING=Endpoint=sb://...

3. Skip integration tests:
   dotnet test --filter "Category!=Integration"

For more information, see: tests/SourceFlow.Cloud.Azure.Tests/README.md
```

## Setting Up Azure Services

### Option 1: Azurite Emulator (Local Development)

**Note:** Azurite currently does NOT support Service Bus or Key Vault emulation. Most integration tests require these services and will fail until Microsoft adds support.

```bash
# Install Azurite
npm install -g azurite

# Start Azurite
azurite --silent --location c:\azurite
```

### Option 2: Real Azure Services

Configure environment variables to point to real Azure resources:

```bash
# Service Bus (managed identity - recommended)
set AZURE_SERVICEBUS_NAMESPACE=myservicebus.servicebus.windows.net

# Service Bus (connection string)
set AZURE_SERVICEBUS_CONNECTION_STRING=Endpoint=sb://myservicebus.servicebus.windows.net/;SharedAccessKeyName=...

# Key Vault
set AZURE_KEYVAULT_URL=https://mykeyvault.vault.azure.net/
```

**Required Azure Resources:**
1. Service Bus Namespace with queues and topics
2. Key Vault with encryption keys
3. Managed Identity with appropriate RBAC roles

## CI/CD Integration

### GitHub Actions Example

```yaml
- name: Run Unit Tests
  run: dotnet test --filter "Category=Unit" --logger "trx"

- name: Run Integration Tests (if Azure configured)
  if: env.AZURE_SERVICEBUS_NAMESPACE != ''
  run: dotnet test --filter "Category=Integration" --logger "trx"
```

### Azure DevOps Example

```yaml
- task: DotNetCoreCLI@2
  displayName: 'Run Unit Tests'
  inputs:
    command: 'test'
    arguments: '--filter "Category=Unit" --logger trx'

- task: DotNetCoreCLI@2
  displayName: 'Run Integration Tests'
  condition: ne(variables['AZURE_SERVICEBUS_NAMESPACE'], '')
  inputs:
    command: 'test'
    arguments: '--filter "Category=Integration" --logger trx'
```

## Performance Characteristics

### Unit Tests
- **Duration:** ~5-10 seconds
- **Tests:** 31 tests
- **Infrastructure:** None required

### Integration Tests (with Azure)
- **Duration:** ~5-10 minutes (depends on Azure latency)
- **Tests:** 177 tests
- **Infrastructure:** Azurite or real Azure services required

## Troubleshooting

### Tests Hang Indefinitely
**Cause:** Old behavior before timeout fix was implemented.

**Solution:** 
1. Kill any hanging test processes: `taskkill /F /IM testhost.exe`
2. Rebuild the project: `dotnet build --no-restore`
3. Run unit tests only: `dotnet test --filter "Category=Unit"`

### Connection Timeout Errors
**Cause:** Azure services are not available or not configured.

**Solution:**
- For local development: Skip integration tests with `--filter "Category!=Integration"`
- For CI/CD: Configure Azure services or skip integration tests
- For full testing: Set up Azurite or real Azure services

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
3. **CI/CD Pipeline:** Run unit tests on every commit, integration tests on main branch only
4. **Integration Testing:** Use real Azure services in staging/test environments
5. **Cost Optimization:** Skip integration tests when not needed to avoid Azure costs

## Summary

The test categorization system allows you to:
- ✅ Run fast unit tests without any infrastructure
- ✅ Skip integration tests when Azure is unavailable
- ✅ Get clear error messages with actionable guidance
- ✅ Integrate easily with CI/CD pipelines
- ✅ Avoid indefinite hangs with 5-second connection timeouts

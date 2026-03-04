# Design: Azure Test Timeout and Categorization Fix

## 1. Overview

This design addresses the issue of Azure integration tests hanging indefinitely when Azure services are unavailable. The solution adds proper test categorization, connection timeout handling, and fast-fail behavior.

## 2. Architecture

### 2.1 Test Categorization Strategy

```
Test Categories:
├── Unit Tests (no traits) - No external dependencies
├── Integration Tests [Trait("Category", "Integration")] - Requires external services
│   ├── RequiresAzurite [Trait("Category", "RequiresAzurite")] - Needs Azurite emulator
│   └── RequiresAzure [Trait("Category", "RequiresAzure")] - Needs real Azure services
```

### 2.2 Connection Validation Flow

```
Test Initialization
    ↓
Check Service Availability (5s timeout)
    ↓
    ├─→ Available → Run Test
    └─→ Unavailable → Skip Test with Clear Message
```

## 3. Component Design

### 3.1 AzureTestConfiguration Enhancement

Add connection validation with timeout:

```csharp
public class AzureTestConfiguration
{
    public async Task<bool> IsServiceBusAvailableAsync(TimeSpan timeout);
    public async Task<bool> IsKeyVaultAvailableAsync(TimeSpan timeout);
    public async Task<bool> IsAzuriteAvailableAsync(TimeSpan timeout);
}
```

### 3.2 Test Base Class Pattern

Create base classes for different test categories:

```csharp
public abstract class AzureIntegrationTestBase : IAsyncLifetime
{
    protected async Task InitializeAsync()
    {
        // Validate service availability with timeout
        // Skip test if unavailable
    }
}

public abstract class AzuriteRequiredTestBase : AzureIntegrationTestBase
{
    // Specific to Azurite tests
}

public abstract class AzureRequiredTestBase : AzureIntegrationTestBase
{
    // Specific to real Azure tests
}
```

### 3.3 Test Trait Constants

```csharp
public static class TestCategories
{
    public const string Integration = "Integration";
    public const string RequiresAzurite = "RequiresAzurite";
    public const string RequiresAzure = "RequiresAzure";
    public const string Unit = "Unit";
}
```

## 4. Implementation Details

### 4.1 Service Availability Check

```csharp
public async Task<bool> IsServiceBusAvailableAsync(TimeSpan timeout)
{
    try
    {
        using var cts = new CancellationTokenSource(timeout);
        var client = CreateServiceBusClient();
        
        // Quick connectivity check
        await client.CreateSender("test-queue")
            .SendMessageAsync(new ServiceBusMessage("ping"), cts.Token);
        
        return true;
    }
    catch (OperationCanceledException)
    {
        return false; // Timeout
    }
    catch (Exception)
    {
        return false; // Connection failed
    }
}
```

### 4.2 Test Categorization Pattern

```csharp
[Trait("Category", "Integration")]
[Trait("Category", "RequiresAzurite")]
public class ServiceBusCommandDispatchingTests : AzuriteRequiredTestBase
{
    [Fact]
    public async Task Test_CommandDispatching()
    {
        // Test implementation
    }
}
```

### 4.3 Skip Test on Unavailable Service

```csharp
public async Task InitializeAsync()
{
    var isAvailable = await _config.IsServiceBusAvailableAsync(TimeSpan.FromSeconds(5));
    
    if (!isAvailable)
    {
        Skip.If(true, "Azure Service Bus is not available. " +
            "Start Azurite or configure real Azure services. " +
            "To skip integration tests, run: dotnet test --filter \"Category!=Integration\"");
    }
}
```

## 5. Test Categories Mapping

### 5.1 Unit Tests (No External Dependencies)
- `AzureBusBootstrapperTests` - Mocked dependencies
- `AzureIocExtensionsTests` - Service registration only
- `AzureServiceBusCommandDispatcherTests` - Mocked Service Bus client
- `AzureServiceBusEventDispatcherTests` - Mocked Service Bus client
- `DependencyVerificationTests` - Assembly scanning only
- `AzureCircuitBreakerTests` - In-memory circuit breaker logic

### 5.2 Integration Tests Requiring Azurite
- `ServiceBusCommandDispatchingTests`
- `ServiceBusCommandDispatchingPropertyTests`
- `ServiceBusEventPublishingTests`
- `ServiceBusSubscriptionFilteringTests`
- `ServiceBusSubscriptionFilteringPropertyTests`
- `ServiceBusEventSessionHandlingTests`
- `AzureConcurrentProcessingTests`
- `AzureConcurrentProcessingPropertyTests`
- `AzureAutoScalingTests`
- `AzureAutoScalingPropertyTests`

### 5.3 Integration Tests Requiring Real Azure
- `KeyVaultEncryptionTests`
- `KeyVaultEncryptionPropertyTests`
- `KeyVaultHealthCheckTests`
- `ManagedIdentityAuthenticationTests`
- `ServiceBusHealthCheckTests`
- `AzureHealthCheckPropertyTests`
- `AzureMonitorIntegrationTests`
- `AzureTelemetryCollectionPropertyTests`
- `AzurePerformanceBenchmarkTests`
- `AzurePerformanceMeasurementPropertyTests`

### 5.4 Emulator Equivalence Tests
- `AzuriteEmulatorEquivalencePropertyTests` - Requires both Azurite and Azure
- `AzureTestResourceManagementPropertyTests` - Requires Azure for ARM templates

## 6. Configuration

### 6.1 Default Timeout Values

```csharp
public static class AzureTestDefaults
{
    public static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
}
```

### 6.2 Environment Variables

```bash
# Override default timeouts
AZURE_TEST_CONNECTION_TIMEOUT=5
AZURE_TEST_OPERATION_TIMEOUT=30

# Skip integration tests automatically
SKIP_INTEGRATION_TESTS=true
```

## 7. Error Messages

### 7.1 Service Bus Unavailable

```
Azure Service Bus is not available at localhost:8080.

Options:
1. Start Azurite emulator: azurite --silent --location c:\azurite
2. Configure real Azure Service Bus: set AZURE_SERVICEBUS_NAMESPACE=myservicebus.servicebus.windows.net
3. Skip integration tests: dotnet test --filter "Category!=Integration"

For more information, see: tests/SourceFlow.Cloud.Azure.Tests/README.md
```

### 7.2 Key Vault Unavailable

```
Azure Key Vault is not available at https://localhost:8080.

Options:
1. Configure real Azure Key Vault: set AZURE_KEYVAULT_URL=https://mykeyvault.vault.azure.net/
2. Skip integration tests: dotnet test --filter "Category!=RequiresAzure"

Note: Azurite does not currently support Key Vault emulation.

For more information, see: tests/SourceFlow.Cloud.Azure.Tests/README.md
```

## 8. CI/CD Integration

### 8.1 GitHub Actions Example

```yaml
- name: Run Unit Tests
  run: dotnet test --filter "Category!=Integration" --logger "trx"

- name: Run Integration Tests (if Azure configured)
  if: env.AZURE_SERVICEBUS_NAMESPACE != ''
  run: dotnet test --filter "Category=Integration" --logger "trx"
```

### 8.2 Azure DevOps Example

```yaml
- task: DotNetCoreCLI@2
  displayName: 'Run Unit Tests'
  inputs:
    command: 'test'
    arguments: '--filter "Category!=Integration" --logger trx'

- task: DotNetCoreCLI@2
  displayName: 'Run Integration Tests'
  condition: ne(variables['AZURE_SERVICEBUS_NAMESPACE'], '')
  inputs:
    command: 'test'
    arguments: '--filter "Category=Integration" --logger trx'
```

## 9. Migration Strategy

### 9.1 Phase 1: Add Test Categories
- Add `[Trait]` attributes to all test classes
- No behavior changes yet

### 9.2 Phase 2: Add Connection Validation
- Implement service availability checks
- Add timeout handling
- Tests still run but fail fast

### 9.3 Phase 3: Add Test Skipping
- Implement Skip.If logic
- Tests skip gracefully when services unavailable

## 10. Testing Strategy

### 10.1 Validation Tests
- Verify all test classes have appropriate traits
- Verify connection timeouts work correctly
- Verify skip logic works as expected

### 10.2 Manual Testing
- Run tests without Azure services (should skip gracefully)
- Run tests with Azurite (should run Azurite tests)
- Run tests with real Azure (should run all tests)

## 11. Correctness Properties

### Property 1: Test Categorization Completeness
**Statement**: All integration tests that require external services must have the "Integration" trait.

**Validation**: Scan all test classes and verify trait presence.

### Property 2: Connection Timeout Enforcement
**Statement**: All Azure service connections must timeout within the configured duration.

**Validation**: Measure actual timeout duration and verify it's ≤ configured timeout + small buffer.

### Property 3: Skip Message Clarity
**Statement**: When tests are skipped, the skip message must contain actionable guidance.

**Validation**: Verify skip messages contain at least one of: service name, how to fix, how to skip.

### Property 4: Test Execution Consistency
**Statement**: Running tests with `--filter "Category!=Integration"` must never attempt to connect to external services.

**Validation**: Monitor network connections during unit test execution.

## 12. Performance Impact

### 12.1 Unit Tests
- No impact (no connection attempts)

### 12.2 Integration Tests
- Initial connection check: +5 seconds per test class (one-time per class)
- Skip overhead: <1ms per test
- Overall: Minimal impact when services are available, significant time savings when unavailable

## 13. Backward Compatibility

### 13.1 Existing Behavior
- Running `dotnet test` without filters will still run all tests
- Tests will still fail if Azure services are unavailable (but fail fast)

### 13.2 New Behavior
- Tests can be filtered by category
- Tests skip gracefully with clear messages
- Connection timeouts prevent indefinite hangs

## 14. Documentation Updates

### 14.1 README.md Updates
- Add section on test categories
- Add section on running specific test categories
- Add troubleshooting guide for connection issues

### 14.2 TEST_EXECUTION_STATUS.md Updates
- Update with new test categorization information
- Add examples of filtered test execution
- Update error message examples

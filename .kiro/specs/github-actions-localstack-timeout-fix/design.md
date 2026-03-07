# GitHub Actions LocalStack Timeout Fix - Bugfix Design

## Overview

This bugfix addresses LocalStack container startup timeout failures in GitHub Actions CI environments. The core issue is that LocalStack services (sqs, sns, kms, iam) do not report "available" status within the current 30-second timeout window in containerized CI environments, despite working correctly in local development. Additionally, parallel test execution causes port conflicts when multiple tests attempt to start LocalStack containers simultaneously on port 4566.

The fix strategy involves:
1. Increasing health check timeouts and retry logic for CI environments
2. Implementing external LocalStack instance detection to reuse existing containers
3. Enhancing xUnit collection fixtures to enforce proper container sharing
4. Adding CI-specific configuration with longer timeouts and more retries
5. Improving wait strategies to account for slower container initialization in GitHub Actions

## Glossary

- **Bug_Condition (C)**: The condition that triggers the bug - when LocalStack containers start in GitHub Actions CI and health checks timeout before services report "available" status
- **Property (P)**: The desired behavior when LocalStack starts in CI - all services should report "available" within a reasonable timeout appropriate for CI environments
- **Preservation**: Existing local development test behavior that must remain unchanged by the fix
- **LocalStackManager**: The class in `tests/SourceFlow.Cloud.AWS.Tests/TestHelpers/LocalStackManager.cs` that manages LocalStack container lifecycle
- **LocalStackTestFixture**: The xUnit fixture in `tests/SourceFlow.Cloud.AWS.Tests/TestHelpers/LocalStackTestFixture.cs` that provides shared LocalStack instances for tests
- **Health Check Endpoint**: The `/_localstack/health` endpoint that returns service status information
- **Service Ready State**: When a LocalStack service reports "available" or "running" status in the health check response
- **CI Environment**: GitHub Actions containerized environment with different performance characteristics than local development
- **Port Conflict**: When multiple containers attempt to bind to the same port (4566) simultaneously

## Bug Details

### Fault Condition

The bug manifests when LocalStack containers start in GitHub Actions CI environments and the health check endpoint `/_localstack/health` does not return "available" status for all configured services (sqs, sns, kms, iam) within the 30-second timeout window. The `LocalStackManager.WaitForServicesAsync` method times out before services are ready, causing test failures.

**Formal Specification:**
```
FUNCTION isBugCondition(input)
  INPUT: input of type LocalStackStartupContext
  OUTPUT: boolean
  
  RETURN input.environment == "GitHub Actions CI"
         AND input.containerStarted == true
         AND input.healthCheckTimeout == 30 seconds
         AND NOT allServicesReportAvailable(input.services, input.healthCheckTimeout)
         AND (input.portConflict == true OR input.parallelTestExecution == true)
END FUNCTION
```

### Examples

- **Example 1**: LocalStack container starts in GitHub Actions, health check polls for 30 seconds, services still report "initializing" status, test fails with `TimeoutException: LocalStack services did not become ready within 00:00:30`

- **Example 2**: Two integration tests run in parallel in GitHub Actions, both attempt to start LocalStack on port 4566, second test fails with "port is already allocated" error

- **Example 3**: LocalStack container starts in GitHub Actions, SQS and SNS report "available" after 25 seconds, but KMS and IAM report "available" after 45 seconds, test fails before all services are ready

- **Edge Case**: External LocalStack instance is already running in GitHub Actions (pre-started service container), test attempts to start new container on same port, fails with port conflict instead of reusing existing instance

## Expected Behavior

### Preservation Requirements

**Unchanged Behaviors:**
- Local development tests must continue to pass with existing timeout configurations (30 seconds is sufficient locally)
- Service validation logic (SQS ListQueues, SNS ListTopics, KMS ListKeys, IAM ListRoles) must continue to work correctly
- Container cleanup with `AutoRemove = true` must continue to function properly
- Port conflict detection via `FindAvailablePortAsync` must continue to find alternative ports
- Test lifecycle management with `IAsyncLifetime` must continue to work correctly
- Health endpoint JSON deserialization and status parsing must continue to work correctly

**Scope:**
All inputs that do NOT involve GitHub Actions CI environments should be completely unaffected by this fix. This includes:
- Local development test execution
- Tests running against real AWS services (not LocalStack)
- Unit tests that don't require LocalStack
- Tests that successfully complete within 30 seconds

## Hypothesized Root Cause

Based on the bug description and code analysis, the most likely issues are:

1. **Insufficient Timeout for CI Environments**: The current 30-second `HealthCheckTimeout` is adequate for local development but insufficient for GitHub Actions containerized environments where container startup and service initialization are slower due to:
   - Shared compute resources in CI runners
   - Network latency for pulling container images
   - Slower disk I/O in virtualized environments
   - Cold start overhead for LocalStack services

2. **Missing External Instance Detection**: The `LocalStackManager.StartAsync` method checks for external LocalStack instances with a 3-second timeout in `LocalStackTestFixture`, but this check may be:
   - Too short to reliably detect running instances
   - Not consistently applied across all test entry points
   - Not properly handling the case where an instance is starting but not yet ready

3. **Inadequate xUnit Collection Sharing**: Tests use `[Collection("AWS Integration Tests")]` attribute but may not be properly configured with a collection fixture, causing xUnit to:
   - Create separate fixture instances per test class
   - Not enforce sequential execution within the collection
   - Allow parallel execution that triggers port conflicts

4. **CI Environment Container Limitations**: GitHub Actions CI environments have Docker-in-Docker limitations that prevent tests from starting their own containers:
   - Tests attempting to start LocalStack containers in CI will fail
   - Service containers must be pre-configured in workflow YAML
   - Tests need fail-fast behavior when service container is not detected

4. **Insufficient Health Check Retry Logic**: The current retry configuration (`MaxHealthCheckRetries = 10`, `HealthCheckRetryDelay = 2 seconds`) provides only 20 seconds of actual retry time, which is:
   - Less than the 30-second timeout (due to HTTP request overhead)
   - Insufficient for services that take 40-60 seconds to initialize in CI
   - Not adaptive to CI environment performance characteristics

5. **Wait Strategy Limitations**: The Testcontainers wait strategy checks for HTTP 200 OK on health endpoints but doesn't:
   - Parse the JSON response to verify service "available" status
   - Distinguish between "initializing" and "available" states
   - Provide sufficient delay after container start before health checks

## Correctness Properties

Property 1: Fault Condition - LocalStack Services Ready in CI

_For any_ LocalStack container startup in GitHub Actions CI where the bug condition holds (services do not report "available" within 30 seconds), the fixed `LocalStackManager` SHALL wait up to 90 seconds with enhanced retry logic, allowing sufficient time for all configured services (sqs, sns, kms, iam) to report "available" status, and tests SHALL pass successfully.

**Validates: Requirements 2.1, 2.3, 2.5**

Property 2: Fault Condition - External Instance Detection

_For any_ test execution where an external LocalStack instance is already running (detected via health endpoint check), the fixed `LocalStackManager` SHALL detect and reuse the existing instance instead of attempting to start a new container, preventing port conflicts and reducing startup time.

**Validates: Requirements 2.2, 2.6**

Property 3: Fault Condition - xUnit Collection Fixture Sharing

_For any_ parallel test execution using the `[Collection("AWS Integration Tests")]` attribute, the fixed xUnit configuration SHALL enforce shared fixture usage across all tests in the collection, ensuring only one LocalStack container instance is started and preventing port conflicts.

**Validates: Requirements 2.2, 2.4**

Property 4: Preservation - Local Development Behavior

_For any_ test execution in local development environments where the bug condition does NOT hold (services report "available" within 30 seconds), the fixed code SHALL produce exactly the same behavior as the original code, preserving fast test execution and existing timeout configurations.

**Validates: Requirements 3.1, 3.2, 3.3, 3.4, 3.5, 3.6**

## Fix Implementation

### Changes Required

Assuming our root cause analysis is correct:

**File**: `tests/SourceFlow.Cloud.AWS.Tests/TestHelpers/LocalStackConfiguration.cs`

**Function**: Configuration factory methods

**Specific Changes**:
1. **Increase CI Timeout Values**: Modify `CreateForIntegrationTesting` method to use 90-second `HealthCheckTimeout` and 30 retry attempts
   - Change `HealthCheckTimeout = TimeSpan.FromMinutes(1)` to `TimeSpan.FromSeconds(90)`
   - Change `MaxHealthCheckRetries = 15` to `30`
   - Change `HealthCheckRetryDelay = TimeSpan.FromSeconds(2)` to `TimeSpan.FromSeconds(3)`

2. **Add CI-Specific Configuration**: Create new `CreateForGitHubActions` factory method with CI-optimized settings
   - 90-second health check timeout
   - 30 retry attempts with 3-second delays
   - Enhanced diagnostics enabled
   - Longer startup timeout (3 minutes)

**File**: `tests/SourceFlow.Cloud.AWS.Tests/TestHelpers/LocalStackManager.cs`

**Function**: `StartAsync`, `WaitForServicesAsync`, `IsExternalLocalStackAvailableAsync`

**Specific Changes**:
1. **Enhance External Instance Detection**: Improve `IsExternalLocalStackAvailableAsync` method
   - Increase timeout from 3 seconds to 10 seconds for CI environments
   - Add retry logic (3 attempts with 2-second delays)
   - **Lenient Detection**: Accept HTTP 200 from health endpoint even if services aren't fully initialized
   - Defer full service readiness validation to `WaitForServicesAsync`
   - Log detection results for diagnostics with service status details when available

2. **Improve Wait Strategy**: Modify `StartAsync` to add initial delay after container start
   - Add 5-second delay after `_container.StartAsync()` completes
   - This allows LocalStack initialization scripts to run before health checks begin
   - Only apply delay when starting new container (not for external instances)

3. **Enhanced Health Check Logging**: Improve `WaitForServicesAsync` diagnostics
   - Log individual service status on each retry (not just "not ready")
   - Include response time metrics in logs
   - Log health endpoint JSON response for failed checks
   - Add structured logging with service names and status values

4. **Adaptive Retry Logic**: Modify `WaitForServicesAsync` to detect CI environments
   - Check for `GITHUB_ACTIONS` environment variable
   - Use longer timeouts and more retries when in CI
   - Fall back to original behavior for local development

**File**: `tests/SourceFlow.Cloud.AWS.Tests/TestHelpers/LocalStackTestFixture.cs`

**Function**: `InitializeAsync`

**Specific Changes**:
1. **Increase External Check Timeout**: Change external instance check timeout from 3 seconds to 10 seconds
   - Modify `using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3))` to `TimeSpan.FromSeconds(10)`
   - Add retry logic (3 attempts) for external instance detection

2. **Use CI-Specific Configuration**: Detect GitHub Actions environment and use appropriate configuration
   - Check for `GITHUB_ACTIONS` environment variable
   - Use `LocalStackConfiguration.CreateForGitHubActions()` when in CI
   - Use existing configuration for local development

3. **Enhanced Wait After Start**: Add longer delay after container start in CI
   - Change `await Task.Delay(2000)` to `await Task.Delay(5000)` when in CI
   - Keep 2-second delay for local development

**File**: `tests/SourceFlow.Cloud.AWS.Tests/TestHelpers/AwsIntegrationTestCollection.cs` (NEW FILE)

**Function**: xUnit collection definition

**Specific Changes**:
1. **Create Collection Definition**: Define xUnit collection with shared fixture
   - Create `[CollectionDefinition("AWS Integration Tests")]` attribute
   - Implement `ICollectionFixture<LocalStackTestFixture>` interface
   - This ensures xUnit creates only one fixture instance for all tests in the collection

**File**: Multiple integration test files

**Function**: Test class declarations

**Specific Changes**:
1. **Verify Collection Attribute**: Ensure all integration tests use `[Collection("AWS Integration Tests")]`
   - Audit all test classes in `tests/SourceFlow.Cloud.AWS.Tests/Integration/`
   - Verify they have the collection attribute
   - Add attribute to any tests missing it

## Testing Strategy

### Validation Approach

The testing strategy follows a two-phase approach: first, surface counterexamples that demonstrate the bug on unfixed code in GitHub Actions CI, then verify the fix works correctly and preserves existing local development behavior.

### Exploratory Fault Condition Checking

**Goal**: Surface counterexamples that demonstrate the bug BEFORE implementing the fix. Confirm or refute the root cause analysis. If we refute, we will need to re-hypothesize.

**Test Plan**: Run existing integration tests in GitHub Actions CI without the fix and capture detailed diagnostics. Add enhanced logging to observe actual service startup times, health check responses, and port conflict scenarios. Run tests on UNFIXED code to observe failures and understand the root cause.

**Test Cases**:
1. **CI Timeout Test**: Run `LocalStackIntegrationTests` in GitHub Actions with current 30-second timeout (will fail on unfixed code)
   - Expected: Timeout after 30 seconds with services still "initializing"
   - Observe: Actual time required for services to become "available"

2. **Parallel Execution Test**: Run multiple integration tests in parallel in GitHub Actions (will fail on unfixed code)
   - Expected: Port conflict errors on second and subsequent tests
   - Observe: Whether xUnit collection fixture is properly shared

3. **External Instance Test**: Pre-start LocalStack container in GitHub Actions, then run tests (may fail on unfixed code)
   - Expected: Tests attempt to start new container, fail with port conflict
   - Observe: Whether external instance detection works reliably

4. **Service Timing Test**: Add diagnostic logging to measure individual service ready times in CI (will provide data on unfixed code)
   - Expected: Some services take 40-60 seconds to report "available"
   - Observe: Actual timing distribution for sqs, sns, kms, iam services

**Expected Counterexamples**:
- Health checks timeout after 30 seconds with services still in "initializing" state
- Port conflicts occur when multiple tests run in parallel
- External LocalStack instances are not detected within 3-second timeout
- Possible causes: insufficient timeout, inadequate retry logic, missing collection fixture, slow CI environment

### Fix Checking

**Goal**: Verify that for all inputs where the bug condition holds, the fixed code produces the expected behavior.

**Pseudocode:**
```
FOR ALL input WHERE isBugCondition(input) DO
  result := LocalStackManager_fixed.StartAsync(input)
  ASSERT allServicesReady(result, 90 seconds)
  ASSERT noPortConflicts(result)
  ASSERT externalInstanceDetected(result) IF externalInstanceExists(input)
END FOR
```

**Test Plan**: Run integration tests in GitHub Actions CI with the fix applied. Verify all tests pass consistently across multiple CI runs.

**Test Cases**:
1. **CI Timeout Resolution**: Run all integration tests in GitHub Actions with 90-second timeout
   - Assert: All tests pass without timeout exceptions
   - Assert: Services report "available" within 90 seconds
   - Verify: Logs show actual ready times for each service

2. **External Instance Detection**: Pre-start LocalStack in GitHub Actions, run tests
   - Assert: Tests detect and reuse existing instance
   - Assert: No port conflicts occur
   - Verify: Logs show "Detected existing LocalStack instance" message

3. **Collection Fixture Sharing**: Run multiple tests in parallel with collection fixture
   - Assert: Only one LocalStack container is started
   - Assert: All tests share the same fixture instance
   - Verify: Container logs show single startup sequence

4. **Enhanced Retry Logic**: Monitor health check retry behavior in CI
   - Assert: Retries continue until services are ready or timeout
   - Assert: Individual service status is logged on each retry
   - Verify: Logs show progressive service initialization

### Preservation Checking

**Goal**: Verify that for all inputs where the bug condition does NOT hold, the fixed code produces the same result as the original code.

**Pseudocode:**
```
FOR ALL input WHERE NOT isBugCondition(input) DO
  ASSERT LocalStackManager_original.StartAsync(input) = LocalStackManager_fixed.StartAsync(input)
  ASSERT testExecutionTime_fixed <= testExecutionTime_original + 5 seconds
END FOR
```

**Testing Approach**: Property-based testing is recommended for preservation checking because:
- It generates many test cases automatically across the input domain
- It catches edge cases that manual unit tests might miss
- It provides strong guarantees that behavior is unchanged for all non-buggy inputs

**Test Plan**: Observe behavior on UNFIXED code first for local development scenarios, then write property-based tests capturing that behavior.

**Test Cases**:
1. **Local Development Preservation**: Run all integration tests locally with fixed code
   - Observe: Tests on unfixed code pass within 30 seconds
   - Assert: Tests on fixed code pass within same time window (±5 seconds)
   - Verify: No behavioral changes in local development

2. **Service Validation Preservation**: Verify AWS service validation continues to work
   - Observe: SQS ListQueues, SNS ListTopics, KMS ListKeys, IAM ListRoles work on unfixed code
   - Assert: Same operations work identically on fixed code
   - Verify: No changes to validation logic

3. **Container Cleanup Preservation**: Verify container disposal works correctly
   - Observe: Containers are removed with `AutoRemove = true` on unfixed code
   - Assert: Same cleanup behavior on fixed code
   - Verify: No container leaks in local or CI environments

4. **Port Conflict Detection Preservation**: Verify `FindAvailablePortAsync` still works
   - Observe: Method finds alternative ports when 4566 is occupied on unfixed code
   - Assert: Same behavior on fixed code
   - Verify: Port selection logic unchanged

### Unit Tests

- Test `LocalStackConfiguration.CreateForGitHubActions` returns correct timeout values
- Test `LocalStackManager.IsExternalLocalStackAvailableAsync` with retry logic
- Test `LocalStackManager.WaitForServicesAsync` with CI environment detection
- Test xUnit collection fixture creation and sharing
- Test health check timeout calculation for CI vs local environments

### Property-Based Tests

- Generate random service combinations and verify all report "available" within timeout
- Generate random retry configurations and verify convergence to ready state
- Test that external instance detection works across various timing scenarios
- Verify container cleanup works correctly regardless of startup path (new vs external)

### Integration Tests

- Test full LocalStack startup flow in GitHub Actions CI environment
- Test parallel test execution with shared collection fixture
- Test external instance detection and reuse in CI
- Test that all AWS service validations pass after enhanced startup
- Test that diagnostic logging provides useful troubleshooting information

# Implementation Tasks: Azure Test Timeout and Categorization Fix

## Overview
This implementation adds proper test categorization, connection timeout handling, and fast-fail behavior to Azure integration tests to prevent indefinite hanging when Azure services are unavailable.

## Tasks

- [x] 1. Create test infrastructure for timeout and categorization
  - [x] 1.1 Create TestCategories constants class
    - Define constants for Integration, RequiresAzurite, RequiresAzure, Unit
    - Add to TestHelpers namespace
    - _Requirements: 3.1_

  - [x] 1.2 Enhance AzureTestConfiguration with availability checks
    - Add IsServiceBusAvailableAsync with timeout parameter
    - Add IsKeyVaultAvailableAsync with timeout parameter
    - Add IsAzuriteAvailableAsync with timeout parameter
    - Implement 5-second timeout for connection attempts
    - _Requirements: 3.2, 4.1_

  - [x] 1.3 Create AzureTestDefaults configuration class
    - Define default ConnectionTimeout (5 seconds)
    - Define default OperationTimeout (30 seconds)
    - Add to TestHelpers namespace
    - _Requirements: 4.1_

  - [x] 1.4 Create base test classes for different categories
    - Create AzureIntegrationTestBase with service validation
    - Create AzuriteRequiredTestBase extending integration base
    - Create AzureRequiredTestBase extending integration base
    - Implement IAsyncLifetime for setup/teardown
    - Add Skip.If logic for unavailable services
    - _Requirements: 3.2, 3.4_

- [x] 2. Add test categorization to unit tests
  - [x] 2.1 Add traits to AzureBusBootstrapperTests
    - Add [Trait("Category", "Unit")]
    - Verify no external dependencies
    - _Requirements: 3.1_

  - [x] 2.2 Add traits to AzureIocExtensionsTests
    - Add [Trait("Category", "Unit")]
    - Verify no external dependencies
    - _Requirements: 3.1_

  - [x] 2.3 Add traits to AzureServiceBusCommandDispatcherTests
    - Add [Trait("Category", "Unit")]
    - Verify mocked dependencies
    - _Requirements: 3.1_

  - [x] 2.4 Add traits to AzureServiceBusEventDispatcherTests
    - Add [Trait("Category", "Unit")]
    - Verify mocked dependencies
    - _Requirements: 3.1_

  - [x] 2.5 Add traits to DependencyVerificationTests
    - Add [Trait("Category", "Unit")]
    - Verify no external dependencies
    - _Requirements: 3.1_

  - [x] 2.6 Add traits to AzureCircuitBreakerTests
    - Add [Trait("Category", "Unit")]
    - Verify in-memory logic only
    - _Requirements: 3.1_

- [ ] 3. Add test categorization to Azurite-dependent integration tests
  - [ ] 3.1 Add traits to ServiceBusCommandDispatchingTests
    - Add [Trait("Category", "Integration")]
    - Add [Trait("Category", "RequiresAzurite")]
    - Inherit from AzuriteRequiredTestBase
    - _Requirements: 3.1, 3.2_

  - [ ] 3.2 Add traits to ServiceBusCommandDispatchingPropertyTests
    - Add [Trait("Category", "Integration")]
    - Add [Trait("Category", "RequiresAzurite")]
    - Inherit from AzuriteRequiredTestBase
    - _Requirements: 3.1, 3.2_

  - [ ] 3.3 Add traits to ServiceBusEventPublishingTests
    - Add [Trait("Category", "Integration")]
    - Add [Trait("Category", "RequiresAzurite")]
    - Inherit from AzuriteRequiredTestBase
    - _Requirements: 3.1, 3.2_

  - [ ] 3.4 Add traits to ServiceBusSubscriptionFilteringTests
    - Add [Trait("Category", "Integration")]
    - Add [Trait("Category", "RequiresAzurite")]
    - Inherit from AzuriteRequiredTestBase
    - _Requirements: 3.1, 3.2_

  - [ ] 3.5 Add traits to ServiceBusSubscriptionFilteringPropertyTests
    - Add [Trait("Category", "Integration")]
    - Add [Trait("Category", "RequiresAzurite")]
    - Inherit from AzuriteRequiredTestBase
    - _Requirements: 3.1, 3.2_

  - [ ] 3.6 Add traits to ServiceBusEventSessionHandlingTests
    - Add [Trait("Category", "Integration")]
    - Add [Trait("Category", "RequiresAzurite")]
    - Inherit from AzuriteRequiredTestBase
    - _Requirements: 3.1, 3.2_

  - [ ] 3.7 Add traits to AzureConcurrentProcessingTests
    - Add [Trait("Category", "Integration")]
    - Add [Trait("Category", "RequiresAzurite")]
    - Inherit from AzuriteRequiredTestBase
    - _Requirements: 3.1, 3.2_

  - [ ] 3.8 Add traits to AzureConcurrentProcessingPropertyTests
    - Add [Trait("Category", "Integration")]
    - Add [Trait("Category", "RequiresAzurite")]
    - Inherit from AzuriteRequiredTestBase
    - _Requirements: 3.1, 3.2_

  - [ ] 3.9 Add traits to AzureAutoScalingTests
    - Add [Trait("Category", "Integration")]
    - Add [Trait("Category", "RequiresAzurite")]
    - Inherit from AzuriteRequiredTestBase
    - _Requirements: 3.1, 3.2_

  - [ ] 3.10 Add traits to AzureAutoScalingPropertyTests
    - Add [Trait("Category", "Integration")]
    - Add [Trait("Category", "RequiresAzurite")]
    - Inherit from AzuriteRequiredTestBase
    - _Requirements: 3.1, 3.2_

- [ ] 4. Add test categorization to Azure-dependent integration tests
  - [ ] 4.1 Add traits to KeyVaultEncryptionTests
    - Add [Trait("Category", "Integration")]
    - Add [Trait("Category", "RequiresAzure")]
    - Inherit from AzureRequiredTestBase
    - _Requirements: 3.1, 3.2_

  - [ ] 4.2 Add traits to KeyVaultEncryptionPropertyTests
    - Add [Trait("Category", "Integration")]
    - Add [Trait("Category", "RequiresAzure")]
    - Inherit from AzureRequiredTestBase
    - _Requirements: 3.1, 3.2_

  - [ ] 4.3 Add traits to KeyVaultHealthCheckTests
    - Add [Trait("Category", "Integration")]
    - Add [Trait("Category", "RequiresAzure")]
    - Inherit from AzureRequiredTestBase
    - _Requirements: 3.1, 3.2_

  - [ ] 4.4 Add traits to ManagedIdentityAuthenticationTests
    - Add [Trait("Category", "Integration")]
    - Add [Trait("Category", "RequiresAzure")]
    - Inherit from AzureRequiredTestBase
    - _Requirements: 3.1, 3.2_

  - [ ] 4.5 Add traits to ServiceBusHealthCheckTests
    - Add [Trait("Category", "Integration")]
    - Add [Trait("Category", "RequiresAzure")]
    - Inherit from AzureRequiredTestBase
    - _Requirements: 3.1, 3.2_

  - [ ] 4.6 Add traits to AzureHealthCheckPropertyTests
    - Add [Trait("Category", "Integration")]
    - Add [Trait("Category", "RequiresAzure")]
    - Inherit from AzureRequiredTestBase
    - _Requirements: 3.1, 3.2_

  - [ ] 4.7 Add traits to AzureMonitorIntegrationTests
    - Add [Trait("Category", "Integration")]
    - Add [Trait("Category", "RequiresAzure")]
    - Inherit from AzureRequiredTestBase
    - _Requirements: 3.1, 3.2_

  - [ ] 4.8 Add traits to AzureTelemetryCollectionPropertyTests
    - Add [Trait("Category", "Integration")]
    - Add [Trait("Category", "RequiresAzure")]
    - Inherit from AzureRequiredTestBase
    - _Requirements: 3.1, 3.2_

  - [ ] 4.9 Add traits to AzurePerformanceBenchmarkTests
    - Add [Trait("Category", "Integration")]
    - Add [Trait("Category", "RequiresAzure")]
    - Inherit from AzureRequiredTestBase
    - _Requirements: 3.1, 3.2_

  - [ ] 4.10 Add traits to AzurePerformanceMeasurementPropertyTests
    - Add [Trait("Category", "Integration")]
    - Add [Trait("Category", "RequiresAzure")]
    - Inherit from AzureRequiredTestBase
    - _Requirements: 3.1, 3.2_

  - [ ] 4.11 Add traits to AzuriteEmulatorEquivalencePropertyTests
    - Add [Trait("Category", "Integration")]
    - Add [Trait("Category", "RequiresAzurite")]
    - Add [Trait("Category", "RequiresAzure")]
    - Inherit from AzureRequiredTestBase (needs both)
    - _Requirements: 3.1, 3.2_

  - [ ] 4.12 Add traits to AzureTestResourceManagementPropertyTests
    - Add [Trait("Category", "Integration")]
    - Add [Trait("Category", "RequiresAzure")]
    - Inherit from AzureRequiredTestBase
    - _Requirements: 3.1, 3.2_

- [ ] 5. Update documentation
  - [ ] 5.1 Update README.md with test categorization
    - Add section on test categories
    - Add examples of filtered test execution
    - Add troubleshooting guide for connection issues
    - _Requirements: 3.3, 3.4_

  - [ ] 5.2 Update TEST_EXECUTION_STATUS.md
    - Add test categorization information
    - Add filtered execution examples
    - Update error message examples
    - _Requirements: 3.3, 3.4_

  - [ ] 5.3 Create RUNNING_TESTS.md guide
    - Document how to run unit tests only
    - Document how to run integration tests
    - Document how to run specific categories
    - Document environment variable configuration
    - _Requirements: 3.3, 3.4_

- [ ] 6. Validation and testing
  - [ ] 6.1 Test unit test execution without Azure
    - Run: dotnet test --filter "Category!=Integration"
    - Verify no connection attempts
    - Verify all unit tests pass
    - _Requirements: 3.3_

  - [ ] 6.2 Test integration test skipping
    - Run: dotnet test (without Azure services)
    - Verify tests skip gracefully
    - Verify skip messages are clear
    - _Requirements: 3.2, 3.4_

  - [ ] 6.3 Test connection timeout enforcement
    - Verify connection attempts timeout within 5 seconds
    - Verify no indefinite hangs
    - _Requirements: 3.2, 4.1_

  - [ ] 6.4 Verify all test files have appropriate traits
    - Scan all test classes
    - Verify trait presence
    - Verify trait accuracy
    - _Requirements: 3.1_

## Notes
- All tasks focus on adding categorization and timeout handling without changing test logic
- Tests will skip gracefully when services are unavailable instead of hanging
- Developers can easily run subsets of tests based on available infrastructure
- CI/CD pipelines can run unit tests without Azure infrastructure

# Azure Cloud Integration Testing Spec

This spec defines and tracks the comprehensive testing framework for SourceFlow's Azure cloud integrations, including Azure Service Bus messaging, Azure Key Vault encryption, managed identity authentication, and resilience patterns.

## Status: 🚧 IN PROGRESS

Implementation has progressed significantly. Tasks 1-3 are complete. Task 4 (Azure Service Bus command dispatching tests) is currently in progress.

## Current Progress

### Completed
- ✅ **Task 1**: Enhanced Azure test project structure and dependencies
  - Added comprehensive testing dependencies (TestContainers.Azurite, Azure.ResourceManager, Azure.Monitor.Query)
  - Property test for Azure test environment management (Property 24)

- ✅ **Task 2**: Implemented Azure test environment management infrastructure
  - Created Azure-specific test environment abstractions (IAzureTestEnvironment, IAzureResourceManager, IAzurePerformanceTestRunner)
  - Implemented AzureTestEnvironment with Azurite integration
  - Property tests for Azurite emulator equivalence (Properties 21 & 22)
  - Created ServiceBusTestHelpers with session and duplicate detection support
  - Created KeyVaultTestHelpers with managed identity authentication

- ✅ **Task 3**: Checkpoint - Azure test infrastructure validated and working

### In Progress
- 🚧 **Task 5**: Azure Service Bus event publishing tests (ACTIVE)
  - ✅ Integration tests for event publishing to topics with metadata (Task 5.1)
  - ⏳ Property tests for event publishing patterns (Task 5.2)
  - ⏳ Subscription filtering tests (Task 5.3)
  - ⏳ Property tests for subscription filtering (Task 5.4)
  - ⏳ Session-based event handling tests (Task 5.5)

### Recently Completed
- ✅ **Task 4**: Azure Service Bus command dispatching tests
  - ✅ Integration tests for command routing with correlation IDs
  - ✅ Property test for message routing correctness (Property 1)
  - ✅ Session handling tests with concurrent sessions
  - ✅ Property test for session ordering preservation (Property 2)
  - ✅ Duplicate detection tests with deduplication window
  - ✅ Property test for duplicate detection effectiveness (Property 3)
  - ✅ Dead letter queue tests with metadata and resubmission
  - ✅ Property test for dead letter queue handling (Property 12)

### Next Steps
- Complete Task 5 (Azure Service Bus event publishing tests)
- Begin Task 6 (Azure Key Vault encryption and security tests)
- Continue with performance and resilience testing phases

## Quick Links

- **[Requirements](requirements.md)** - User stories and acceptance criteria
- **[Design](design.md)** - Testing architecture and approach
- **[Tasks](tasks.md)** - Implementation checklist

## What Will Be Tested

### Azure Service Bus Messaging
Comprehensive testing of Azure Service Bus for distributed command and event processing with session-based ordering, duplicate detection, and dead letter handling.

**Key Features:**
- Command routing to queues with correlation IDs
- Session-based message ordering per entity
- Automatic duplicate detection
- Dead letter queue processing
- Event publishing to topics with fan-out
- Subscription filtering

### Azure Key Vault Encryption
End-to-end encryption testing with Azure Key Vault integration and managed identity authentication.

**Key Features:**
- Message encryption and decryption
- Managed identity authentication (system and user-assigned)
- Key rotation without service interruption
- Sensitive data masking in logs
- RBAC permission validation

### Performance and Scalability
Performance benchmarking and load testing for Azure Service Bus under various conditions.

**Key Features:**
- Message throughput (messages/second)
- End-to-end latency (P50/P95/P99)
- Concurrent processing validation
- Auto-scaling behavior testing
- Resource utilization monitoring

### Resilience and Error Handling
Comprehensive resilience testing for Azure-specific failure scenarios.

**Key Features:**
- Circuit breaker patterns for Azure services
- Retry policies with exponential backoff
- Graceful degradation when services unavailable
- Throttling and rate limiting handling
- Network partition recovery

### Local Development Support
Testing framework supports both local development with Azurite emulators and cloud-based testing with real Azure services.

**Key Features:**
- Azurite emulator integration
- Functional equivalence validation
- Fast feedback during development
- No Azure costs for local testing

## Test Project Structure

The testing framework enhances the existing `SourceFlow.Cloud.Azure.Tests` project:

```
tests/SourceFlow.Cloud.Azure.Tests/
├── Integration/          # Azure Service Bus and Key Vault integration tests
├── E2E/                 # End-to-end message flow scenarios
├── Resilience/          # Circuit breaker and retry policy tests
├── Security/            # Managed identity and encryption tests
├── Performance/         # Throughput and latency benchmarks
├── TestHelpers/         # Azure test utilities and fixtures
└── Unit/               # Existing unit tests
```

## Test Categories

- **Unit Tests** - Mock-based tests with fast execution
- **Integration Tests** - Tests with real or emulated Azure services
- **End-to-End Tests** - Complete message flow validation
- **Performance Tests** - Throughput, latency, and resource utilization
- **Security Tests** - Authentication, authorization, and encryption
- **Resilience Tests** - Circuit breakers, retries, and failure handling

## Requirements Summary

All 10 main requirements and 50 acceptance criteria:

1. ✅ Azure Service Bus Command Dispatching Testing
2. ✅ Azure Service Bus Event Publishing Testing
3. ✅ Azure Key Vault Encryption Testing
4. ✅ Azure Health Checks and Monitoring Testing
5. ✅ Azure Performance and Scalability Testing
6. ✅ Azure Resilience and Error Handling Testing
7. ✅ Azurite Local Development Testing
8. ✅ Azure CI/CD Integration Testing
9. ✅ Azure Security Testing
10. ✅ Azure Test Documentation and Troubleshooting

## Key Testing Features

### For Developers
- **Local Testing** - Azurite emulators for rapid feedback
- **Cloud Testing** - Real Azure services for production validation
- **Comprehensive Coverage** - All Azure-specific scenarios tested
- **Performance Insights** - Benchmarks and optimization guidance
- **Security Validation** - Managed identity and encryption testing

### For CI/CD
- **Automated Provisioning** - ARM templates for test resources
- **Environment Isolation** - Separate test environments
- **Automatic Cleanup** - Cost control through resource deletion
- **Detailed Reporting** - Azure-specific metrics and analysis
- **Actionable Errors** - Troubleshooting guidance in failures

## Test Environments

### Azurite Local Environment
- Fast feedback during development
- No Azure costs
- Service Bus and Key Vault emulation
- Functional equivalence with Azure

### Azure Development Environment
- Real Azure services
- Isolated development subscription
- Resource tagging for cost tracking
- Managed identity testing

### Azure CI/CD Environment
- Automated provisioning with ARM templates
- Automatic resource cleanup
- Parallel test execution
- Performance benchmarking

## Property-Based Testing

The framework uses FsCheck for property-based testing to validate universal correctness properties:

- **29 Properties** covering all Azure-specific scenarios
- **Minimum 100 iterations** per property test
- **Shrinking** to find minimal failing examples
- **Azure-specific generators** for realistic test data

## Getting Started

### Prerequisites
- .NET 10.0 SDK
- Azure subscription (for cloud testing)
- Azurite emulator (for local testing)
- Azure CLI (for resource provisioning)

### Running Tests Locally
```bash
# Start Azurite emulator
azurite --silent --location azurite-data

# Run all tests
dotnet test tests/SourceFlow.Cloud.Azure.Tests/

# Run specific category
dotnet test --filter Category=Integration
```

### Running Tests Against Azure
```bash
# Set Azure credentials
az login

# Configure test environment
export AZURE_SERVICEBUS_NAMESPACE="myservicebus.servicebus.windows.net"
export AZURE_KEYVAULT_URL="https://mykeyvault.vault.azure.net/"

# Run tests
dotnet test tests/SourceFlow.Cloud.Azure.Tests/ --filter Category=CloudIntegration
```

## Implementation Approach

### Phase 1: Infrastructure (Tasks 1-3) - ✅ COMPLETE
- ✅ Enhanced test project dependencies (Task 1)
- ✅ Implemented test environment management (Task 2)
  - ✅ Azure-specific test environment abstractions
  - ✅ Azure test environment with Azurite integration
  - ✅ Property tests for Azurite emulator equivalence
  - ✅ Azure Service Bus test helpers
  - ✅ Azure Key Vault test helpers
- ✅ Checkpoint validation (Task 3)

### Phase 2: Core Testing (Tasks 4-7) - 🚧 IN PROGRESS
- ✅ Azure Service Bus command dispatching tests (Task 4 - Complete)
  - ✅ Command routing integration tests
  - ✅ Property tests for routing, sessions, duplicate detection, and dead letter handling
- 🚧 Azure Service Bus event publishing tests (Task 5 - In Progress)
  - ✅ Event publishing integration tests (Task 5.1)
  - ⏳ Property tests and subscription filtering (Tasks 5.2-5.5)
- ⏳ Azure Key Vault encryption and security tests (Task 6 - Pending)
- ⏳ Checkpoint validation (Task 7 - Pending)

### Phase 3: Advanced Testing (Tasks 8-12)
- Health checks and monitoring tests
- Performance testing infrastructure
- Resilience and error handling tests
- Additional security testing

### Phase 4: Documentation and Integration (Tasks 13-15)
- Comprehensive test documentation
- Final integration and validation
- Full test suite execution

## Success Criteria

The testing framework will be considered complete when:

1. **Comprehensive Coverage** - All 10 requirements and 50 acceptance criteria validated
2. **Property Tests Pass** - All 29 property-based tests pass with 100+ iterations
3. **Performance Validated** - Benchmarks meet expected thresholds
4. **Documentation Complete** - Setup, execution, and troubleshooting guides available
5. **CI/CD Integration** - Automated testing in pipelines
6. **Local and Cloud** - Tests work with both Azurite and real Azure services

## Benefits

1. **Confidence** - Comprehensive testing ensures Azure integrations work correctly
2. **Fast Feedback** - Local testing with Azurite accelerates development
3. **Performance Insights** - Benchmarks guide optimization efforts
4. **Security Validation** - Managed identity and encryption properly tested
5. **Resilience Assurance** - Failure scenarios validated before production
6. **Cost Control** - Automated cleanup prevents runaway Azure costs

## Future Enhancements (Optional)

- Chaos engineering tests for Azure services
- Multi-region failover testing
- Azure Monitor dashboard templates
- Performance regression detection
- Automated capacity planning recommendations

## Contributing

When implementing tasks from this spec:

1. Follow the task order in tasks.md
2. Complete checkpoints before proceeding
3. Write both unit and property-based tests
4. Update documentation as you implement
5. Validate with both Azurite and Azure services
6. Run full test suite before marking tasks complete

## Questions?

For questions about this spec:
- Review the [Design Document](design.md) for architecture details
- Check the [Requirements Document](requirements.md) for acceptance criteria
- See the [Tasks Document](tasks.md) for implementation steps

---

**Spec Version**: 1.0  
**Status**: 📋 Ready for Implementation  
**Created**: 2025-02-14

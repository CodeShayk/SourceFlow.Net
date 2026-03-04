# Requirements Document

## Introduction

This document specifies the requirements for preparing the v2.0.0 release of SourceFlow.Net packages. The release focuses on removing all Azure-related content from documentation while maintaining comprehensive AWS cloud integration documentation. This is a documentation-only release preparation with no code changes required.

## Glossary

- **Documentation_System**: The collection of markdown files in the docs/ directory that provide user-facing documentation for SourceFlow.Net
- **Azure_Content**: Any references, examples, configuration instructions, or testing documentation related to Azure Service Bus, Key Vault, or other Azure services
- **AWS_Content**: References, examples, configuration instructions, and testing documentation related to AWS SQS, SNS, KMS, and other AWS services
- **Status_Files**: Markdown files with STATUS, COMPLETE, or VALIDATION in their filenames used for tracking implementation progress
- **Release_Package**: The SourceFlow.Net core package and its extensions (SourceFlow.Cloud.AWS, SourceFlow.Stores.EntityFramework)

## Requirements

### Requirement 1: Remove Azure Testing Documentation

**User Story:** As a documentation maintainer, I want to remove all Azure testing content from Cloud-Integration-Testing.md, so that the documentation reflects only AWS cloud integration testing.

#### Acceptance Criteria

1. THE Documentation_System SHALL remove all Azure-specific testing sections from Cloud-Integration-Testing.md
2. THE Documentation_System SHALL remove Azure property-based tests (Properties 1-29) from the property testing section
3. THE Documentation_System SHALL remove Azure Service Bus integration test descriptions
4. THE Documentation_System SHALL remove Azure Key Vault integration test descriptions
5. THE Documentation_System SHALL remove Azure health check test descriptions
6. THE Documentation_System SHALL remove Azure performance testing sections
7. THE Documentation_System SHALL remove Azure resilience testing sections
8. THE Documentation_System SHALL remove Azure CI/CD integration sections
9. THE Documentation_System SHALL remove Azure security testing sections
10. THE Documentation_System SHALL remove Azurite emulator references and setup instructions
11. THE Documentation_System SHALL remove cross-cloud integration testing sections that reference Azure
12. THE Documentation_System SHALL preserve all AWS testing documentation sections
13. THE Documentation_System SHALL preserve AWS property-based tests (Properties 1-16)
14. THE Documentation_System SHALL preserve LocalStack integration test documentation
15. THE Documentation_System SHALL update the overview section to reference only AWS cloud integration

### Requirement 2: Remove Azure Configuration Examples

**User Story:** As a developer, I want to see only AWS configuration examples in the idempotency guide, so that I can configure idempotency for AWS deployments without confusion.

#### Acceptance Criteria

1. THE Documentation_System SHALL remove all Azure configuration examples from Idempotency-Configuration-Guide.md
2. THE Documentation_System SHALL remove Azure Service Bus connection string examples
3. THE Documentation_System SHALL remove Azure managed identity configuration examples
4. THE Documentation_System SHALL remove Azure-specific idempotency setup instructions
5. THE Documentation_System SHALL preserve all AWS configuration examples
6. THE Documentation_System SHALL preserve AWS SQS/SNS configuration examples
7. THE Documentation_System SHALL preserve AWS IAM configuration examples
8. THE Documentation_System SHALL update the default behavior section to reference only AWS
9. THE Documentation_System SHALL update the multi-instance deployment section to reference only AWS
10. THE Documentation_System SHALL preserve the fluent builder API documentation

### Requirement 3: Remove Azure Integration from Main README

**User Story:** As a new user, I want to see only AWS cloud integration options in the main README, so that I understand the available cloud integration options for v2.0.0.

#### Acceptance Criteria

1. THE Documentation_System SHALL remove all Azure configuration sections from SourceFlow.Net-README.md
2. THE Documentation_System SHALL remove Azure Service Bus setup examples
3. THE Documentation_System SHALL remove Azure Key Vault encryption examples
4. THE Documentation_System SHALL remove Azure managed identity authentication examples
5. THE Documentation_System SHALL remove Azure health check configuration examples
6. THE Documentation_System SHALL preserve all AWS configuration sections
7. THE Documentation_System SHALL preserve AWS SQS/SNS setup examples
8. THE Documentation_System SHALL preserve AWS KMS encryption examples
9. THE Documentation_System SHALL preserve AWS IAM authentication examples
10. THE Documentation_System SHALL update the cloud configuration overview to reference only AWS
11. THE Documentation_System SHALL update the bus configuration system examples to show only AWS

### Requirement 4: Update CHANGELOG for AWS-Only Release

**User Story:** As a release manager, I want the CHANGELOG to reflect that v2.0.0 is an AWS-only release, so that users understand the scope of cloud integration support.

#### Acceptance Criteria

1. THE Documentation_System SHALL remove all Azure-related sections from docs/Versions/v2.0.0/CHANGELOG.md
2. THE Documentation_System SHALL remove Azure cloud extension breaking changes
3. THE Documentation_System SHALL remove Azure namespace change documentation
4. THE Documentation_System SHALL remove Azure migration guide sections
5. THE Documentation_System SHALL remove Azure integration feature descriptions
6. THE Documentation_System SHALL preserve all AWS-related sections
7. THE Documentation_System SHALL preserve AWS cloud extension documentation
8. THE Documentation_System SHALL preserve AWS namespace change documentation
9. THE Documentation_System SHALL preserve AWS migration guide sections
10. THE Documentation_System SHALL add a note indicating v2.0.0 supports AWS cloud integration only
11. THE Documentation_System SHALL update package dependencies to list only AWS extension

### Requirement 5: Clean Up Entity Framework Documentation

**User Story:** As a developer, I want the Entity Framework documentation to focus on core persistence without Azure-specific examples, so that I can use the stores with AWS deployments.

#### Acceptance Criteria

1. THE Documentation_System SHALL review SourceFlow.Stores.EntityFramework-README.md for Azure references
2. IF Azure-specific configuration examples exist, THEN THE Documentation_System SHALL remove them
3. THE Documentation_System SHALL preserve all database provider examples (SQL Server, PostgreSQL, MySQL, SQLite)
4. THE Documentation_System SHALL preserve AWS-compatible configuration examples
5. THE Documentation_System SHALL ensure all examples are cloud-agnostic or AWS-specific

### Requirement 6: Remove Status and Validation Files

**User Story:** As a repository maintainer, I want to remove all status tracking files, so that the repository contains only production documentation.

#### Acceptance Criteria

1. THE Documentation_System SHALL search for all Status_Files in the repository
2. WHEN Status_Files are found, THE Documentation_System SHALL delete them
3. THE Documentation_System SHALL search for files matching patterns: *STATUS*.md, *COMPLETE*.md, *VALIDATION*.md
4. THE Documentation_System SHALL verify no status tracking files remain after cleanup
5. THE Documentation_System SHALL preserve all production documentation files

### Requirement 7: Validate Documentation Completeness

**User Story:** As a quality assurance reviewer, I want to verify that all AWS documentation is complete and accurate, so that users have comprehensive guidance for AWS deployments.

#### Acceptance Criteria

1. THE Documentation_System SHALL verify Cloud-Integration-Testing.md contains complete AWS testing documentation
2. THE Documentation_System SHALL verify Idempotency-Configuration-Guide.md contains complete AWS configuration examples
3. THE Documentation_System SHALL verify SourceFlow.Net-README.md contains complete AWS integration guide
4. THE Documentation_System SHALL verify CHANGELOG.md accurately describes v2.0.0 changes
5. THE Documentation_System SHALL verify all AWS code examples are syntactically correct
6. THE Documentation_System SHALL verify all AWS configuration examples reference valid AWS services
7. THE Documentation_System SHALL verify all internal documentation links are valid
8. THE Documentation_System SHALL verify no broken references to removed Azure content exist

### Requirement 8: Maintain Documentation Quality Standards

**User Story:** As a documentation reader, I want the documentation to maintain professional quality standards, so that I can trust the accuracy and completeness of the information.

#### Acceptance Criteria

1. THE Documentation_System SHALL maintain consistent formatting across all updated files
2. THE Documentation_System SHALL maintain consistent terminology for AWS services
3. THE Documentation_System SHALL preserve all code block syntax highlighting
4. THE Documentation_System SHALL preserve all markdown table formatting
5. THE Documentation_System SHALL preserve all diagram references and links
6. THE Documentation_System SHALL ensure proper heading hierarchy in all files
7. THE Documentation_System SHALL ensure proper list formatting in all files
8. THE Documentation_System SHALL verify no orphaned sections or incomplete sentences exist

### Requirement 9: Update Cloud.Core Namespace References

**User Story:** As a developer, I want documentation to reflect the Cloud.Core consolidation into the main SourceFlow package, so that I understand the correct namespaces and package dependencies for v2.0.0.

#### Acceptance Criteria

1. THE Documentation_System SHALL remove all references to SourceFlow.Cloud.Core as a separate package
2. THE Documentation_System SHALL update namespace references from SourceFlow.Cloud.Core.* to SourceFlow.Cloud.*
3. THE Documentation_System SHALL update package dependency documentation to show cloud extensions depend only on SourceFlow
4. THE Documentation_System SHALL update using statements in code examples to use SourceFlow.Cloud.* namespaces
5. THE Documentation_System SHALL update project reference examples to show only SourceFlow dependency
6. THE Documentation_System SHALL verify all Cloud-Integration-Testing.md namespace references are updated
7. THE Documentation_System SHALL verify all Idempotency-Configuration-Guide.md namespace references are updated
8. THE Documentation_System SHALL verify all SourceFlow.Net-README.md namespace references are updated
9. THE Documentation_System SHALL verify all CHANGELOG.md namespace references are updated
10. THE Documentation_System SHALL ensure migration guide reflects Cloud.Core consolidation
11. THE Documentation_System SHALL update any architecture diagrams or references to show consolidated structure

### Requirement 10: Update Architecture Documentation

**User Story:** As a developer, I want comprehensive architecture documentation for cloud integration, so that I understand the design and implementation patterns for AWS cloud messaging.

#### Acceptance Criteria

1. THE Documentation_System SHALL create or update architecture documentation for AWS cloud integration
2. THE Documentation_System SHALL document the bus configuration system architecture
3. THE Documentation_System SHALL document the command and event routing patterns
4. THE Documentation_System SHALL document the idempotency service architecture
5. THE Documentation_System SHALL document the bootstrapper resource provisioning process
6. THE Documentation_System SHALL evaluate whether docs/Architecture/06-Cloud-Core-Consolidation.md should be retained or consolidated
7. IF 06-Cloud-Core-Consolidation.md is retained, THEN THE Documentation_System SHALL update it to reflect AWS-only release
8. IF 06-Cloud-Core-Consolidation.md is not needed, THEN THE Documentation_System SHALL consolidate its content into other architecture documents
9. THE Documentation_System SHALL ensure architecture documentation is consistent with v2.0.0 changes

### Requirement 11: Consolidate Idempotency Documentation

**User Story:** As a developer, I want unified idempotency documentation, so that I understand all idempotency approaches (in-memory and SQL-based) for cloud message handling in one place.

#### Acceptance Criteria

1. THE Documentation_System SHALL consolidate SQL-Based-Idempotency-Service.md into Idempotency-Configuration-Guide.md
2. THE Documentation_System SHALL document both in-memory and SQL-based idempotency approaches
3. THE Documentation_System SHALL document when to use each idempotency approach
4. THE Documentation_System SHALL document the fluent builder API for idempotency configuration
5. THE Documentation_System SHALL document cloud message handling idempotency patterns
6. THE Documentation_System SHALL document multi-instance deployment considerations
7. THE Documentation_System SHALL preserve all SQL-based implementation details
8. THE Documentation_System SHALL preserve all configuration examples
9. THE Documentation_System SHALL delete SQL-Based-Idempotency-Service.md after consolidation
10. THE Documentation_System SHALL ensure consolidated documentation is comprehensive and well-organized

### Requirement 12: Create AWS Cloud Extension Package README

**User Story:** As a developer, I want dedicated documentation for the AWS cloud extension package, so that I can understand how to use AWS SQS, SNS, and KMS integration with SourceFlow.

#### Acceptance Criteria

1. THE Documentation_System SHALL create docs/SourceFlow.Cloud.AWS-README.md
2. THE Documentation_System SHALL document AWS cloud extension installation and setup
3. THE Documentation_System SHALL document AWS SQS command dispatching
4. THE Documentation_System SHALL document AWS SNS event publishing
5. THE Documentation_System SHALL document AWS KMS message encryption
6. THE Documentation_System SHALL document the bus configuration system for AWS
7. THE Documentation_System SHALL document the bootstrapper resource provisioning
8. THE Documentation_System SHALL document IAM permission requirements
9. THE Documentation_System SHALL document LocalStack integration for local development
10. THE Documentation_System SHALL document health checks and monitoring
11. THE Documentation_System SHALL provide complete code examples for common scenarios
12. THE Documentation_System SHALL follow the same structure and quality as SourceFlow.Net-README.md

### Requirement 13: Update CI/CD for LocalStack Integration Testing

**User Story:** As a CI/CD maintainer, I want GitHub Actions workflows to run AWS integration tests against LocalStack containers, so that we can validate AWS cloud integration functionality in the CI pipeline.

#### Acceptance Criteria

1. THE CI_System SHALL update GitHub Actions workflows to support LocalStack container testing
2. THE CI_System SHALL configure LocalStack container service in workflow files
3. THE CI_System SHALL configure AWS SDK to connect to LocalStack endpoints
4. THE CI_System SHALL run unit tests with filter "Category=Unit"
5. THE CI_System SHALL run integration tests with filter "Category=Integration&Category=RequiresLocalStack"
6. THE CI_System SHALL ensure LocalStack container is started before integration tests
7. THE CI_System SHALL ensure LocalStack container is stopped after integration tests
8. THE CI_System SHALL configure appropriate timeouts for container startup
9. THE CI_System SHALL update PR-CI.yml workflow to include LocalStack testing
10. THE CI_System SHALL update Master-Build.yml workflow to include LocalStack testing
11. THE CI_System SHALL preserve existing test execution for non-cloud tests
12. THE CI_System SHALL document LocalStack configuration in workflow comments


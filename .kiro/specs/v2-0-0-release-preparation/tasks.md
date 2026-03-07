# Implementation Plan: v2.0.0 Release Preparation

## Overview

This implementation plan removes all Azure-related content from SourceFlow.Net documentation and updates namespace references to reflect the Cloud.Core consolidation into the main SourceFlow package. This prepares the documentation for the v2.0.0 AWS-only release. The plan follows a three-phase approach: Discovery → Removal → Validation.

This is a documentation-only update with no code changes required. All tasks focus on updating markdown files in the docs/ directory.

## Tasks

- [x] 1. Discovery Phase - Identify Azure References
  - Search all documentation files for Azure-specific content
  - Identify status tracking files for deletion
  - Create inventory of files requiring updates
  - _Requirements: 6.1, 6.2, 6.3_

- [x] 2. Update Cloud-Integration-Testing.md
  - [x] 2.1 Remove Azure testing overview sections
    - Remove Azure Service Bus integration test descriptions
    - Remove Azure Key Vault integration test descriptions
    - Remove Azure health check test descriptions
    - Update overview to reference only AWS cloud integration
    - _Requirements: 1.1, 1.3, 1.4, 1.5, 1.15_
  
  - [x] 2.2 Remove Azure property-based tests
    - Remove Properties 1-29 (Azure-specific properties)
    - Preserve Properties 1-16 (AWS properties)
    - Update property test section header
    - _Requirements: 1.2, 1.13_
  
  - [x] 2.3 Remove Azure integration test sections
    - Remove Azure Service Bus message routing tests
    - Remove Azure Key Vault encryption tests
    - Remove Azurite emulator setup instructions
    - Preserve all LocalStack integration documentation
    - _Requirements: 1.10, 1.14_
  
  - [x] 2.4 Remove Azure performance and resilience testing
    - Remove Azure performance testing sections
    - Remove Azure resilience testing sections
    - Remove Azure CI/CD integration sections
    - Remove Azure security testing sections
    - _Requirements: 1.6, 1.7, 1.8, 1.9_
  
  - [x] 2.5 Remove cross-cloud integration testing
    - Remove sections referencing Azure in cross-cloud scenarios
    - Preserve AWS testing documentation
    - _Requirements: 1.11, 1.12_

- [x] 3. Update Idempotency-Configuration-Guide.md
  - [x] 3.1 Remove Azure configuration examples
    - Remove Azure Service Bus connection string examples
    - Remove Azure managed identity configuration examples
    - Remove Azure-specific idempotency setup instructions
    - _Requirements: 2.1, 2.2, 2.3, 2.4_
  
  - [x] 3.2 Update configuration sections
    - Update default behavior section to reference only AWS
    - Update multi-instance deployment section to reference only AWS
    - Preserve fluent builder API documentation
    - _Requirements: 2.8, 2.9, 2.10_
  
  - [x] 3.3 Preserve AWS configuration examples
    - Verify AWS SQS/SNS configuration examples are complete
    - Verify AWS IAM configuration examples are complete
    - _Requirements: 2.5, 2.6, 2.7_

- [x] 4. Update SourceFlow.Net-README.md
  - [x] 4.1 Remove Azure integration sections
    - Remove Azure Service Bus setup examples
    - Remove Azure Key Vault encryption examples
    - Remove Azure managed identity authentication examples
    - Remove Azure health check configuration examples
    - _Requirements: 3.1, 3.2, 3.3, 3.4, 3.5_
  
  - [x] 4.2 Update cloud configuration overview
    - Update overview to reference only AWS cloud integration
    - Update bus configuration system examples to show only AWS
    - _Requirements: 3.10, 3.11_
  
  - [x] 4.3 Preserve AWS integration sections
    - Verify AWS SQS/SNS setup examples are complete
    - Verify AWS KMS encryption examples are complete
    - Verify AWS IAM authentication examples are complete
    - _Requirements: 3.6, 3.7, 3.8, 3.9_

- [x] 5. Update CHANGELOG.md
  - [x] 5.1 Remove Azure-related sections
    - Remove Azure cloud extension breaking changes
    - Remove Azure namespace change documentation
    - Remove Azure migration guide sections
    - Remove Azure integration feature descriptions
    - _Requirements: 4.1, 4.2, 4.3, 4.4, 4.5_
  
  - [x] 5.2 Add AWS-only release note
    - Add note indicating v2.0.0 supports AWS cloud integration only
    - Update package dependencies to list only AWS extension
    - _Requirements: 4.10, 4.11_
  
  - [x] 5.3 Preserve AWS-related sections
    - Verify AWS cloud extension documentation is complete
    - Verify AWS namespace change documentation is complete
    - Verify AWS migration guide sections are complete
    - _Requirements: 4.6, 4.7, 4.8, 4.9_

- [x] 6. Review SourceFlow.Stores.EntityFramework-README.md
  - [x] 6.1 Search for Azure-specific references
    - Identify any Azure-specific configuration examples
    - Identify any Azure service references
    - _Requirements: 5.1, 5.2_
  
  - [x] 6.2 Remove Azure content if found
    - Remove Azure-specific configuration examples
    - Preserve database provider examples (SQL Server, PostgreSQL, MySQL, SQLite)
    - Preserve AWS-compatible configuration examples
    - _Requirements: 5.2, 5.3, 5.4, 5.5_

- [x] 7. Checkpoint - Review documentation updates
  - Ensure all Azure content has been removed
  - Ensure all AWS content is preserved and complete
  - Ask the user if questions arise

- [x] 8. Update Cloud.Core Namespace References
  - [x] 8.1 Update Cloud-Integration-Testing.md namespace references
    - Replace SourceFlow.Cloud.Core.* with SourceFlow.Cloud.* in all code examples
    - Update using statements to use consolidated namespaces
    - Update package dependency references to show only SourceFlow dependency
    - _Requirements: 9.1, 9.2, 9.3, 9.4, 9.6_
  
  - [x] 8.2 Update Idempotency-Configuration-Guide.md namespace references
    - Replace SourceFlow.Cloud.Core.* with SourceFlow.Cloud.* in all code examples
    - Update using statements to use consolidated namespaces
    - Update package dependency references
    - _Requirements: 9.1, 9.2, 9.3, 9.4, 9.7_
  
  - [x] 8.3 Update SourceFlow.Net-README.md namespace references
    - Replace SourceFlow.Cloud.Core.* with SourceFlow.Cloud.* in all code examples
    - Update using statements to use consolidated namespaces
    - Update package dependency documentation
    - Update project reference examples to show only SourceFlow dependency
    - Update architecture diagrams or references to show consolidated structure
    - _Requirements: 9.1, 9.2, 9.3, 9.4, 9.5, 9.8, 9.11_
  
  - [x] 8.4 Update CHANGELOG.md namespace references
    - Update breaking changes section to document Cloud.Core consolidation
    - Update migration guide to show namespace changes
    - Update package dependency changes
    - Ensure Cloud.Core consolidation is clearly documented
    - _Requirements: 9.1, 9.2, 9.3, 9.9, 9.10_

- [x] 9. Update Architecture Documentation
  - [x] 9.1 Evaluate Cloud-Core-Consolidation.md retention
    - Review docs/Architecture/06-Cloud-Core-Consolidation.md content
    - Determine if document should be retained or consolidated
    - If retained, update to reflect AWS-only release (remove Azure references)
    - If not needed, identify where content should be consolidated
    - _Requirements: 10.1, 10.6, 10.7, 10.8_
  
  - [x] 9.2 Create or update AWS cloud architecture documentation
    - Document bus configuration system architecture
    - Document command and event routing patterns
    - Document idempotency service architecture
    - Document bootstrapper resource provisioning process
    - Document AWS-specific implementation details
    - _Requirements: 10.1, 10.2, 10.3, 10.4, 10.5, 10.9_
  
  - [x] 9.3 Update Architecture README
    - Update docs/Architecture/README.md to reference cloud architecture documentation
    - Ensure architecture index is complete and accurate
    - _Requirements: 10.9_

- [x] 10. Consolidate Idempotency Documentation
  - [x] 10.1 Merge SQL-Based-Idempotency-Service.md into Idempotency-Configuration-Guide.md
    - Add SQL-based idempotency service section
    - Document both in-memory and SQL-based approaches
    - Document when to use each approach (single-instance vs multi-instance)
    - Document fluent builder API for idempotency configuration
    - Document cloud message handling idempotency patterns
    - Document multi-instance deployment considerations
    - _Requirements: 11.1, 11.2, 11.3, 11.4, 11.5, 11.6_
  
  - [x] 10.2 Preserve implementation details
    - Preserve all SQL-based implementation details
    - Preserve all configuration examples
    - Preserve database schema documentation
    - Preserve performance considerations
    - Preserve troubleshooting guidance
    - _Requirements: 11.7, 11.8, 11.10_
  
  - [x] 10.3 Delete SQL-Based-Idempotency-Service.md
    - Verify all content has been consolidated
    - Delete docs/SQL-Based-Idempotency-Service.md
    - _Requirements: 11.9_

- [x] 11. Create AWS Cloud Extension Package README
  - [x] 11.1 Create docs/SourceFlow.Cloud.AWS-README.md
    - Create new README file for AWS cloud extension package
    - Follow structure similar to SourceFlow.Net-README.md
    - _Requirements: 12.1, 12.12_
  
  - [x] 11.2 Document installation and setup
    - Document NuGet package installation
    - Document service registration with UseSourceFlowAws
    - Document AWS SDK configuration
    - Document IAM permission requirements
    - _Requirements: 12.2, 12.8_
  
  - [x] 11.3 Document AWS services integration
    - Document AWS SQS command dispatching
    - Document AWS SNS event publishing
    - Document AWS KMS message encryption
    - Document queue and topic configuration
    - _Requirements: 12.3, 12.4, 12.5_
  
  - [x] 11.4 Document bus configuration system
    - Document fluent API for routing configuration
    - Document short name resolution to URLs/ARNs
    - Document FIFO queue configuration
    - Document bootstrapper resource provisioning
    - _Requirements: 12.6, 12.7_
  
  - [x] 11.5 Document development and testing
    - Document LocalStack integration for local development
    - Document health checks and monitoring
    - Document troubleshooting guidance
    - _Requirements: 12.9, 12.10_
  
  - [x] 11.6 Add code examples
    - Provide complete code examples for common scenarios
    - Include command dispatching examples
    - Include event publishing examples
    - Include encryption configuration examples
    - _Requirements: 12.11_

- [x] 12. Delete status tracking files
  - [x] 12.1 Search for status files
    - Search for files matching pattern: *STATUS*.md
    - Search for files matching pattern: *COMPLETE*.md
    - Search for files matching pattern: *VALIDATION*.md
    - _Requirements: 6.1, 6.2, 6.3_
  
  - [x] 12.2 Delete identified status files
    - Delete all status tracking files found
    - Verify no status files remain
    - Preserve all production documentation files
    - _Requirements: 6.2, 6.4, 6.5_

- [x] 13. Update CI/CD for LocalStack Integration Testing
  - [x] 13.1 Update PR-CI.yml workflow
    - Add LocalStack container service configuration
    - Configure AWS SDK environment variables for LocalStack endpoints
    - Add step to run unit tests with filter "Category=Unit"
    - Add step to run integration tests with filter "Category=Integration&Category=RequiresLocalStack"
    - Configure container startup timeouts
    - Add workflow comments documenting LocalStack configuration
    - _Requirements: 13.1, 13.2, 13.3, 13.4, 13.5, 13.6, 13.7, 13.8, 13.9, 13.12_
  
  - [x] 13.2 Update Master-Build.yml workflow
    - Add LocalStack container service configuration
    - Configure AWS SDK environment variables for LocalStack endpoints
    - Add step to run unit tests with filter "Category=Unit"
    - Add step to run integration tests with filter "Category=Integration&Category=RequiresLocalStack"
    - Configure container startup timeouts
    - Add workflow comments documenting LocalStack configuration
    - _Requirements: 13.1, 13.2, 13.3, 13.4, 13.5, 13.6, 13.7, 13.8, 13.10, 13.12_
  
  - [x] 13.3 Preserve existing test execution
    - Ensure non-cloud tests continue to run as before
    - Verify unit tests run independently of LocalStack
    - Verify test execution order is correct
    - _Requirements: 13.11_

- [x] 14. Validation Phase - Verify Documentation Completeness
  - [x] 14.1 Validate AWS documentation completeness
    - Verify Cloud-Integration-Testing.md contains complete AWS testing documentation
    - Verify Idempotency-Configuration-Guide.md contains complete AWS configuration examples
    - Verify SourceFlow.Net-README.md contains complete AWS integration guide
    - Verify SourceFlow.Cloud.AWS-README.md is complete and comprehensive
    - Verify CHANGELOG.md accurately describes v2.0.0 changes
    - _Requirements: 7.1, 7.2, 7.3, 7.4, 12.1, 12.12_
  
  - [x] 14.2 Validate code examples and references
    - Verify all AWS code examples are syntactically correct
    - Verify all AWS configuration examples reference valid AWS services
    - Verify all internal documentation links are valid
    - Verify no broken references to removed Azure content exist
    - _Requirements: 7.5, 7.6, 7.7, 7.8_
  
  - [x] 14.3 Validate documentation quality standards
    - Verify consistent formatting across all updated files
    - Verify consistent terminology for AWS services
    - Verify code block syntax highlighting is preserved
    - Verify markdown table formatting is preserved
    - Verify diagram references and links are preserved
    - Verify proper heading hierarchy in all files
    - Verify proper list formatting in all files
    - Verify no orphaned sections or incomplete sentences exist
    - _Requirements: 8.1, 8.2, 8.3, 8.4, 8.5, 8.6, 8.7, 8.8_
  
  - [x] 14.4 Validate Cloud.Core namespace consolidation
    - Verify all SourceFlow.Cloud.Core.* references have been updated to SourceFlow.Cloud.*
    - Verify all package dependency documentation reflects consolidated structure
    - Verify all using statements use correct namespaces
    - Verify migration guide accurately documents namespace changes
    - _Requirements: 9.1, 9.2, 9.3, 9.4, 9.5, 9.6, 9.7, 9.8, 9.9, 9.10, 9.11_
  
  - [x] 14.5 Validate architecture documentation
    - Verify architecture documentation is complete and accurate
    - Verify idempotency documentation consolidation is successful
    - Verify AWS cloud extension README is comprehensive
    - _Requirements: 10.1, 10.9, 11.10, 12.12_
  
  - [x] 14.6 Validate CI/CD LocalStack integration
    - Verify GitHub Actions workflows include LocalStack container configuration
    - Verify unit tests run with correct filter
    - Verify integration tests run with correct filter
    - Verify LocalStack container starts and stops correctly
    - _Requirements: 13.1, 13.2, 13.4, 13.5, 13.6, 13.7_

- [x] 15. Add test categorization to Core and EntityFramework tests
  - [x] 15.1 Add Category traits to SourceFlow.Core.Tests
    - Add `[Trait("Category", "Unit")]` to all unit test classes
    - Ensure tests can be filtered with `--filter "Category=Unit"`
    - _Requirements: 13.4, 13.11_
  
  - [x] 15.2 Add Category traits to SourceFlow.Stores.EntityFramework.Tests
    - Add `[Trait("Category", "Unit")]` to unit test classes in Unit/ folder
    - Add `[Trait("Category", "Integration")]` to integration test classes in E2E/ folder
    - Ensure tests can be filtered appropriately
    - _Requirements: 13.4, 13.11_
  
  - [x] 15.3 Verify test filtering works
    - Run `dotnet test --filter "Category=Unit"` and verify all unit tests execute
    - Verify Core and EntityFramework tests are now included in filtered results
    - _Requirements: 13.4, 13.5_

- [x] 17. Fix package vulnerabilities
  - [x] 17.1 Audit NuGet packages for vulnerabilities
    - Run `dotnet list package --vulnerable` to identify vulnerable packages
    - Document all vulnerabilities found with severity levels
    - _Requirements: 14.1_
  
  - [x] 17.2 Update vulnerable packages
    - Update all packages with known vulnerabilities to latest secure versions
    - Verify compatibility with existing code after updates
    - Test that all unit tests still pass after package updates
    - _Requirements: 14.2, 14.3_
  
  - [x] 17.3 Verify no vulnerabilities remain
    - Run `dotnet list package --vulnerable` again to confirm all vulnerabilities resolved
    - Document any remaining vulnerabilities that cannot be fixed
    - _Requirements: 14.4_

- [x] 18. Fix build warnings
  - [x] 18.1 Fix Microsoft.Extensions.Options version conflicts
    - Resolve version conflicts between Microsoft.Extensions.Options 9.0.0 and 10.0.0
    - Update package references to use consistent versions across all projects
    - _Requirements: 15.1_
  
  - [x] 18.2 Fix AWS SDK version warnings
    - Update AWSSDK.CloudFormation to version 3.7.401 or later
    - Update AWSSDK.CloudWatchLogs to version 3.7.401 or later
    - Update AWSSDK.IdentityManagement to version 3.7.401 or later
    - _Requirements: 15.2_
  
  - [x] 18.3 Fix nullable reference warnings
    - Review and fix CS8600 warnings (null literal to non-nullable type)
    - Review and fix CS8602 warnings (dereference of possibly null reference)
    - Add null checks or nullable annotations as appropriate
    - _Requirements: 15.3_
  
  - [x] 18.4 Verify clean build
    - Run `dotnet build --configuration Release` and verify zero warnings
    - Document any warnings that cannot be fixed with justification
    - _Requirements: 15.4_

- [x] 19. Add multi-targeting support to AWS cloud extension
  - [x] 19.1 Validate dependency compatibility
    - Verify AWS SDK supports .NET Standard 2.1, net8.0, net9.0, net10.0
    - Verify Microsoft.Extensions packages support all target frameworks
    - Document compatibility findings
    - _Requirements: 16.1_
  
  - [x] 19.2 Update AWS project file for multi-targeting
    - Change TargetFramework to TargetFrameworks with netstandard2.1;net8.0;net9.0;net10.0
    - Add LangVersion property set to "latest"
    - Update Microsoft.Extensions.Options.ConfigurationExtensions to 10.0.0
    - _Requirements: 16.2_
  
  - [x] 19.3 Fix .NET Standard 2.1 compatibility issues
    - Fix ArgumentNullException.ThrowIfNull usage (not available in .NET Standard 2.1)
    - Add conditional compilation for .NET Standard 2.1 vs modern .NET
    - Use traditional null checks for .NET Standard 2.1
    - _Requirements: 16.3_
  
  - [x] 19.4 Verify multi-targeting build
    - Run `dotnet build` for AWS project and verify all target frameworks compile
    - Verify netstandard2.1, net8.0, net9.0, net10.0 all build successfully
    - Run unit tests to ensure functionality works across all targets
    - _Requirements: 16.4_

- [x] 20. Replace package icon
  - [x] 20.1 Update SourceFlow.csproj package icon reference
    - Change PackageIcon from ninja-icon-16.png to simple-logo.png
    - Update ItemGroup to include simple-logo.png instead of ninja-icon-16.png
    - Verify the simple-logo.png file exists in Images/ directory
  
  - [x] 20.2 Verify package icon in all projects
    - Check if any other project files reference ninja-icon-16.png
    - Update all references to use simple-logo.png
    - Ensure consistent branding across all packages

- [x] 21. Fix GitVersion pull-request configuration
  - [x] 21.1 Update pull-request branch configuration
    - Change tag from "beta" to "PullRequest" for pull requests
    - Add tag-number-pattern to extract PR number from branch name
    - Add increment: Inherit to inherit versioning from source branch
    - Ensure PRs from release branches don't get beta tag
  
  - [x] 21.2 Verify version generation
    - Push changes and verify GitHub Actions generates correct version
    - Ensure PRs from release/v2.0.0-aws branch generate 2.0.0-PullRequest.X versions
    - Verify no beta tag appears in version string

- [ ] 22. Final checkpoint - Complete validation
  - Ensure all validation checks pass
  - Ensure documentation is ready for v2.0.0 release
  - Ask the user if questions arise

## Notes

- This update includes documentation changes and CI/CD workflow updates
- Most tasks focus on markdown files in the docs/ directory
- Task 13 updates GitHub Actions workflows for LocalStack integration testing
- AWS documentation must remain complete and accurate
- Validation ensures no broken links or incomplete sections
- Status tracking files are temporary and should be deleted
- Each task references specific requirements for traceability

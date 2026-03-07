# Bugfix Requirements Document

## Introduction

The GitHub Actions Release-CI workflow on the release/v2.0.0-aws branch is failing with two distinct issues that prevent successful continuous integration builds:

1. **NuGet Package Restore Error**: The workflow fails during dependency restoration, attempting to locate a non-existent `sourceflow.cloud.core` package. This package was removed as part of the v2.0.0 Cloud Core consolidation (documented in `docs/Architecture/06-Cloud-Core-Consolidation.md`), where all Cloud.Core functionality was integrated into the main SourceFlow package. All project files have been verified and contain no references to this removed package.

2. **CodeQL Configuration Conflict**: The CodeQL analysis fails with the error "Advanced setup is currently configured but default setup would like to take over". This indicates that GitHub's repository settings have default CodeQL setup enabled, which conflicts with the existing advanced CodeQL workflow files (`.github/workflows/Release-CodeQL.yml` and `.github/workflows/Master-CodeQL.yml`).

These failures block the release pipeline and prevent validation of the v2.0.0 architectural changes.

## Bug Analysis

### Current Behavior (Defect)

1.1 WHEN the Release-CI workflow executes the "Step-07 Restore dependencies" step THEN the system fails with a NuGet package not found error for `sourceflow.cloud.core`

1.2 WHEN the Release-CodeQL workflow executes THEN the system fails with "Advanced setup is currently configured but default setup would like to take over" error

1.3 WHEN NuGet attempts to restore packages THEN the system searches for the removed `sourceflow.cloud.core` package despite no project references existing in any .csproj files

1.4 WHEN CodeQL workflows run THEN the system encounters a configuration conflict between GitHub's default CodeQL setup and the advanced workflow configuration

### Expected Behavior (Correct)

2.1 WHEN the Release-CI workflow executes the "Step-07 Restore dependencies" step THEN the system SHALL successfully restore all packages without attempting to locate the removed `sourceflow.cloud.core` package

2.2 WHEN the Release-CodeQL workflow executes THEN the system SHALL complete CodeQL analysis successfully without configuration conflicts

2.3 WHEN NuGet attempts to restore packages THEN the system SHALL only restore packages referenced in current .csproj files (SourceFlow, SourceFlow.Cloud.AWS, SourceFlow.Cloud.Azure, SourceFlow.Stores.EntityFramework)

2.4 WHEN CodeQL workflows run THEN the system SHALL use the advanced workflow configuration without interference from default setup

2.5 WHEN the Release-CI workflow completes THEN the system SHALL successfully build, test, and package all projects

### Unchanged Behavior (Regression Prevention)

3.1 WHEN the Release-CI workflow builds projects THEN the system SHALL CONTINUE TO use GitVersion for semantic versioning

3.2 WHEN the Release-CI workflow runs tests THEN the system SHALL CONTINUE TO execute all unit and integration tests including LocalStack-based AWS integration tests

3.3 WHEN the Release-CI workflow creates NuGet packages THEN the system SHALL CONTINUE TO generate packages for all SourceFlow projects with correct version numbers

3.4 WHEN the Release-CI workflow is triggered by a release-packages tag THEN the system SHALL CONTINUE TO publish packages to GitHub Packages

3.5 WHEN the Master-CodeQL workflow runs on the master branch THEN the system SHALL CONTINUE TO perform security analysis without being affected by release branch fixes

3.6 WHEN LocalStack services are initialized THEN the system SHALL CONTINUE TO provide SQS, SNS, KMS, and IAM services for integration testing

3.7 WHEN the workflow calculates versions for pre-release builds THEN the system SHALL CONTINUE TO use NuGetVersion format with branch and commit metadata

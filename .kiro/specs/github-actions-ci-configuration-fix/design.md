# GitHub Actions CI Configuration Fix Design

## Overview

This design addresses two critical CI pipeline failures in the GitHub Actions Release-CI workflow: (1) NuGet package restore attempting to locate the removed `sourceflow.cloud.core` package despite no project references, and (2) CodeQL configuration conflicts between advanced workflow files and GitHub's default setup. The fix involves clearing NuGet caches, documenting CodeQL setup requirements, and optionally filtering integration tests to improve CI reliability.

## Glossary

- **Bug_Condition (C)**: The condition that triggers the bug - when GitHub Actions CI workflows execute and encounter cached package metadata or configuration conflicts
- **Property (P)**: The desired behavior when CI workflows run - successful package restoration, CodeQL analysis, and test execution
- **Preservation**: Existing CI workflow behavior that must remain unchanged (GitVersion, test execution, package publishing)
- **NuGet Cache**: GitHub Actions cache containing package metadata that may reference removed packages
- **CodeQL Default Setup**: GitHub repository setting that conflicts with advanced CodeQL workflow files
- **LocalStack**: Docker container providing AWS service emulation for integration tests
- **Integration Tests**: Tests requiring external dependencies (LocalStack, Docker) that may cause CI timeouts

## Bug Details

### Fault Condition

The bug manifests when the GitHub Actions Release-CI workflow executes on the release/v2.0.0-aws branch. The workflow fails at two distinct points: (1) during NuGet package restoration (Step-07), the system attempts to locate the removed `sourceflow.cloud.core` package despite no references in any .csproj files, and (2) during CodeQL analysis, the system encounters a configuration conflict between advanced workflow files and GitHub's default setup.

**Formal Specification:**
```
FUNCTION isBugCondition(input)
  INPUT: input of type GitHubActionsWorkflowExecution
  OUTPUT: boolean
  
  RETURN (input.step == "Step-07 Restore dependencies" 
         AND input.nugetRestore.searchesForPackage("sourceflow.cloud.core")
         AND NOT projectFilesReferencePackage("sourceflow.cloud.core"))
         OR
         (input.workflow == "Release-CodeQL" 
         AND input.error.contains("Advanced setup is currently configured but default setup would like to take over"))
END FUNCTION
```

### Examples

- **NuGet Cache Issue**: Release-CI workflow Step-07 fails with "Package 'sourceflow.cloud.core' not found" despite all .csproj files containing no references to this package
- **CodeQL Conflict**: Release-CodeQL workflow fails with "Advanced setup is currently configured but default setup would like to take over" error
- **Integration Test Timeout**: LocalStack-based integration tests cause CI timeouts and reliability issues (documented in separate bugfix spec)
- **Edge Case**: Manual `dotnet restore --no-cache` succeeds locally but GitHub Actions cache persists the issue

## Expected Behavior

### Preservation Requirements

**Unchanged Behaviors:**
- GitVersion semantic versioning must continue to work exactly as before
- All unit tests must continue to execute and pass
- NuGet package creation with correct version numbers must remain unchanged
- Package publishing to GitHub Packages on release-packages tags must remain unchanged
- Master-CodeQL workflow must remain unaffected by release branch fixes
- LocalStack service initialization must continue to provide SQS, SNS, KMS, IAM services
- Pre-release version calculation using NuGetVersion format must remain unchanged

**Scope:**
All workflow steps that do NOT involve NuGet package restoration, CodeQL analysis, or test filtering should be completely unaffected by this fix. This includes:
- GitVersion installation and version calculation
- .NET SDK installation
- Build steps (both pre-release and release)
- Package creation steps
- Package publishing steps
- LocalStack health check verification

## Hypothesized Root Cause

Based on the bug description and workflow analysis, the most likely issues are:

1. **NuGet Cache Persistence**: GitHub Actions caches NuGet package metadata across workflow runs. When `sourceflow.cloud.core` was removed and consolidated into the main SourceFlow package, the cache retained references to the old package structure, causing restore operations to search for the non-existent package.

2. **CodeQL Configuration Conflict**: The repository has both advanced CodeQL workflow files (`.github/workflows/Release-CodeQL.yml` and `.github/workflows/Master-CodeQL.yml`) AND GitHub's default CodeQL setup enabled in repository settings (Settings > Code security and analysis > Code scanning). These two configurations conflict, causing the workflow to fail.

3. **Missing Cache Invalidation**: The `dotnet restore` command in Step-07 does not include cache-clearing flags, allowing stale cache entries to persist and cause failures.

4. **Lack of Documentation**: There is no documentation explaining that default CodeQL setup must be disabled when using advanced workflow files, leading to configuration conflicts.

## Correctness Properties

Property 1: Fault Condition - NuGet Restore Success

_For any_ GitHub Actions workflow execution where Step-07 runs `dotnet restore` and no project files reference the removed `sourceflow.cloud.core` package, the fixed workflow SHALL successfully restore all packages without attempting to locate the removed package, completing the restore step without errors.

**Validates: Requirements 2.1, 2.3**

Property 2: Fault Condition - CodeQL Analysis Success

_For any_ GitHub Actions workflow execution where CodeQL analysis runs with advanced workflow files present, the fixed workflow SHALL complete CodeQL analysis successfully without configuration conflicts, provided that default CodeQL setup is disabled in repository settings.

**Validates: Requirements 2.2, 2.4**

Property 3: Preservation - Build and Test Behavior

_For any_ workflow step that is NOT Step-07 (Restore dependencies) or CodeQL analysis, the fixed workflow SHALL produce exactly the same behavior as the original workflow, preserving GitVersion calculation, build steps, test execution, package creation, and package publishing.

**Validates: Requirements 3.1, 3.2, 3.3, 3.4, 3.5, 3.6, 3.7**

## Fix Implementation

### Changes Required

Assuming our root cause analysis is correct:

**File**: `.github/workflows/Release-CI.yml`

**Step**: `Step-07 Restore dependencies`

**Specific Changes**:
1. **Add Cache Clearing**: Add `--no-cache` flag to `dotnet restore` command to prevent using stale cached package metadata
   - Change: `dotnet restore` → `dotnet restore --no-cache`
   - Rationale: Forces NuGet to fetch fresh package metadata from sources

2. **Add Explicit Force Flag**: Add `--force` flag to ensure complete package re-evaluation
   - Change: `dotnet restore --no-cache` → `dotnet restore --no-cache --force`
   - Rationale: Forces re-evaluation of all package dependencies

3. **Optional: Add Cache Cleanup Step**: Insert a new step before Step-07 to explicitly clear NuGet caches
   - New Step: `Step-06b Clear NuGet Cache`
   - Command: `dotnet nuget locals all --clear`
   - Rationale: Ensures no stale cache entries exist before restore

**File**: `.github/workflows/Release-CodeQL.yml` and `.github/workflows/Master-CodeQL.yml`

**Changes**: No code changes required, but documentation is needed

**File**: `docs/GitHub-Actions-Setup.md` (new file)

**Specific Changes**:
1. **Create Documentation File**: Document CodeQL setup requirements
   - Section: "CodeQL Configuration Requirements"
   - Content: Explain that default CodeQL setup must be disabled in GitHub repository settings
   - Instructions: Navigate to Settings > Code security and analysis > Code scanning > Disable default setup
   - Rationale: Prevents future configuration conflicts

2. **Add Troubleshooting Section**: Document common CI issues and solutions
   - NuGet cache issues and resolution steps
   - CodeQL configuration conflicts
   - LocalStack timeout issues (reference separate bugfix)

**File**: `.github/workflows/Release-CI.yml` (Optional Enhancement)

**Step**: `Step-09 Test Solution`

**Specific Changes** (based on user requirement to exclude integration tests):
1. **Add Test Filter**: Modify test command to exclude integration tests
   - Change: `dotnet test --configuration Release --no-build --no-restore --verbosity normal`
   - To: `dotnet test --configuration Release --no-build --no-restore --verbosity normal --filter "FullyQualifiedName!~Integration"`
   - Rationale: Excludes tests in Integration folders/namespaces to avoid LocalStack timeout issues

2. **Alternative Filter Approach**: Use test trait filtering if tests are marked with categories
   - Alternative: `dotnet test --filter "Category!=Integration"`
   - Rationale: More explicit categorization if tests use [Trait("Category", "Integration")]

3. **Document Test Categories**: Add comments explaining test filtering strategy
   - Unit tests: Fast, no external dependencies, always run in CI
   - Integration tests: Require LocalStack/Docker, excluded from CI, run manually or on-demand

## Testing Strategy

### Validation Approach

The testing strategy follows a two-phase approach: first, surface counterexamples that demonstrate the bug on unfixed workflows, then verify the fix works correctly and preserves existing behavior.

### Exploratory Fault Condition Checking

**Goal**: Surface counterexamples that demonstrate the bug BEFORE implementing the fix. Confirm or refute the root cause analysis. If we refute, we will need to re-hypothesize.

**Test Plan**: Trigger the Release-CI workflow on the release/v2.0.0-aws branch with the current configuration. Observe the failure at Step-07 and capture the exact error message. Verify that no .csproj files reference `sourceflow.cloud.core`. Run these tests on the UNFIXED workflow to observe failures and understand the root cause.

**Test Cases**:
1. **NuGet Restore Failure Test**: Trigger Release-CI workflow and observe Step-07 failure with "Package 'sourceflow.cloud.core' not found" error (will fail on unfixed workflow)
2. **Project File Verification Test**: Search all .csproj files for references to `sourceflow.cloud.core` and confirm none exist (should pass, confirming cache issue)
3. **CodeQL Conflict Test**: Trigger Release-CodeQL workflow and observe "Advanced setup is currently configured but default setup would like to take over" error (will fail on unfixed workflow)
4. **Local Restore Test**: Run `dotnet restore --no-cache` locally and confirm it succeeds (should pass, confirming GitHub Actions cache issue)

**Expected Counterexamples**:
- Step-07 fails with NuGet package not found error despite no project references
- CodeQL workflow fails with configuration conflict error
- Possible causes: GitHub Actions cache persistence, CodeQL default setup enabled, missing cache invalidation flags

### Fix Checking

**Goal**: Verify that for all inputs where the bug condition holds, the fixed workflow produces the expected behavior.

**Pseudocode:**
```
FOR ALL workflowExecution WHERE isBugCondition(workflowExecution) DO
  result := executeWorkflow_fixed(workflowExecution)
  ASSERT result.step07.status == "success"
  ASSERT result.codeql.status == "success"
  ASSERT NOT result.nugetRestore.searchesForPackage("sourceflow.cloud.core")
END FOR
```

### Preservation Checking

**Goal**: Verify that for all inputs where the bug condition does NOT hold, the fixed workflow produces the same result as the original workflow.

**Pseudocode:**
```
FOR ALL workflowStep WHERE NOT isBugCondition(workflowStep) DO
  ASSERT executeWorkflow_original(workflowStep) = executeWorkflow_fixed(workflowStep)
END FOR
```

**Testing Approach**: Property-based testing is recommended for preservation checking because:
- It generates many test cases automatically across the workflow execution domain
- It catches edge cases that manual workflow tests might miss
- It provides strong guarantees that behavior is unchanged for all non-buggy workflow steps

**Test Plan**: Observe behavior on UNFIXED workflow first for GitVersion, build, test, and package steps, then verify the fixed workflow produces identical results for these steps.

**Test Cases**:
1. **GitVersion Preservation**: Observe that GitVersion calculation produces correct NuGetVersion on unfixed workflow, then verify fixed workflow produces identical version numbers
2. **Build Preservation**: Observe that build steps succeed on unfixed workflow (when restore is manually fixed), then verify fixed workflow builds identically
3. **Test Execution Preservation**: Observe that all tests execute on unfixed workflow, then verify fixed workflow executes the same tests (or filtered subset if integration tests are excluded)
4. **Package Creation Preservation**: Observe that NuGet packages are created with correct versions on unfixed workflow, then verify fixed workflow creates identical packages

### Unit Tests

- Test that `dotnet restore --no-cache --force` successfully restores packages without searching for removed packages
- Test that CodeQL workflows complete successfully when default setup is disabled
- Test that test filtering correctly excludes integration tests when filter is applied
- Test that GitVersion calculation remains unchanged after workflow modifications

### Property-Based Tests

- Generate random workflow execution scenarios and verify Step-07 completes successfully with cache-clearing flags
- Generate random project configurations and verify NuGet restore never searches for removed packages
- Test that all non-restore workflow steps produce identical results across many scenarios
- Generate random test suites and verify filtering correctly separates unit and integration tests

### Integration Tests

- Test full Release-CI workflow execution from checkout to package creation with cache-clearing flags
- Test CodeQL workflow execution with default setup disabled in repository settings
- Test that filtered test execution excludes LocalStack-based integration tests
- Test that package publishing to GitHub Packages works correctly after workflow fixes

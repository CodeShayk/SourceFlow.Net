# Implementation Plan

- [x] 1. Write bug condition exploration test
  - **Property 1: Fault Condition** - NuGet Restore and CodeQL Configuration Failures
  - **CRITICAL**: This test MUST FAIL on unfixed code - failure confirms the bug exists
  - **DO NOT attempt to fix the test or the code when it fails**
  - **NOTE**: This test encodes the expected behavior - it will validate the fix when it passes after implementation
  - **GOAL**: Surface counterexamples that demonstrate the bug exists
  - **Scoped PBT Approach**: For deterministic bugs, scope the property to the concrete failing case(s) to ensure reproducibility
  - Test that Release-CI workflow Step-07 fails with "Package 'sourceflow.cloud.core' not found" error on unfixed workflow
  - Test that no .csproj files reference `sourceflow.cloud.core` (confirms cache issue)
  - Test that Release-CodeQL workflow fails with "Advanced setup is currently configured but default setup would like to take over" error
  - Test that local `dotnet restore --no-cache` succeeds (confirms GitHub Actions cache issue)
  - Run test on UNFIXED workflow
  - **EXPECTED OUTCOME**: Test FAILS (this is correct - it proves the bug exists)
  - Document counterexamples found to understand root cause
  - Mark task complete when test is written, run, and failure is documented
  - _Requirements: 2.1, 2.2, 2.3, 2.4_

- [x] 2. Write preservation property tests (BEFORE implementing fix)
  - **Property 2: Preservation** - Existing CI Workflow Behavior
  - **IMPORTANT**: Follow observation-first methodology
  - Observe behavior on UNFIXED workflow for non-buggy workflow steps
  - Write property-based tests capturing observed behavior patterns from Preservation Requirements
  - Property-based testing generates many test cases for stronger guarantees
  - Test that GitVersion calculation produces correct NuGetVersion on unfixed workflow
  - Test that build steps succeed on unfixed workflow (when restore is manually fixed)
  - Test that all tests execute on unfixed workflow
  - Test that NuGet packages are created with correct versions on unfixed workflow
  - Test that package publishing to GitHub Packages works correctly on unfixed workflow
  - Test that LocalStack service initialization continues to work
  - Run tests on UNFIXED workflow
  - **EXPECTED OUTCOME**: Tests PASS (this confirms baseline behavior to preserve)
  - Mark task complete when tests are written, run, and passing on unfixed workflow
  - _Requirements: 3.1, 3.2, 3.3, 3.4, 3.5, 3.6, 3.7_

- [x] 3. Fix for GitHub Actions CI configuration issues

  - [x] 3.1 Update Release-CI.yml to add NuGet cache clearing
    - Modify Step-07 "Restore dependencies" to add `--no-cache --force` flags
    - Change `dotnet restore` to `dotnet restore --no-cache --force`
    - Optional: Add new Step-06b "Clear NuGet Cache" with `dotnet nuget locals all --clear`
    - _Bug_Condition: isBugCondition(input) where input.step == "Step-07 Restore dependencies" AND input.nugetRestore.searchesForPackage("sourceflow.cloud.core") AND NOT projectFilesReferencePackage("sourceflow.cloud.core")_
    - _Expected_Behavior: expectedBehavior(result) where result.step07.status == "success" AND NOT result.nugetRestore.searchesForPackage("sourceflow.cloud.core")_
    - _Preservation: GitVersion, build steps, test execution, package creation, package publishing, LocalStack initialization_
    - _Requirements: 2.1, 2.3, 3.1, 3.2, 3.3, 3.4, 3.5, 3.6, 3.7_

  - [x] 3.2 Optional: Add test filtering to exclude integration tests
    - Modify Step-09 "Test Solution" to add test filter
    - Change `dotnet test --configuration Release --no-build --no-restore --verbosity normal`
    - To `dotnet test --configuration Release --no-build --no-restore --verbosity normal --filter "FullyQualifiedName!~Integration"`
    - Add comments explaining test filtering strategy (unit tests vs integration tests)
    - _Bug_Condition: Integration tests cause CI timeouts and reliability issues_
    - _Expected_Behavior: Only unit tests execute in CI, integration tests excluded_
    - _Preservation: All unit tests continue to execute and pass_
    - _Requirements: 3.2_

  - [x] 3.3 Create CodeQL setup documentation
    - Create new file `docs/GitHub-Actions-Setup.md`
    - Add section "CodeQL Configuration Requirements"
    - Document that default CodeQL setup must be disabled in GitHub repository settings
    - Add instructions: Navigate to Settings > Code security and analysis > Code scanning > Disable default setup
    - Add troubleshooting section for common CI issues (NuGet cache, CodeQL conflicts, LocalStack timeouts)
    - _Bug_Condition: isBugCondition(input) where input.workflow == "Release-CodeQL" AND input.error.contains("Advanced setup is currently configured but default setup would like to take over")_
    - _Expected_Behavior: expectedBehavior(result) where result.codeql.status == "success" when default setup is disabled_
    - _Preservation: Master-CodeQL workflow remains unaffected_
    - _Requirements: 2.2, 2.4, 3.6_

  - [x] 3.4 Verify bug condition exploration test now passes
    - **Property 1: Expected Behavior** - NuGet Restore and CodeQL Configuration Success
    - **IMPORTANT**: Re-run the SAME test from task 1 - do NOT write a new test
    - The test from task 1 encodes the expected behavior
    - When this test passes, it confirms the expected behavior is satisfied
    - Run bug condition exploration test from step 1
    - **EXPECTED OUTCOME**: Test PASSES (confirms bug is fixed)
    - _Requirements: 2.1, 2.2, 2.3, 2.4_

  - [x] 3.5 Verify preservation tests still pass
    - **Property 2: Preservation** - Existing CI Workflow Behavior Unchanged
    - **IMPORTANT**: Re-run the SAME tests from task 2 - do NOT write new tests
    - Run preservation property tests from step 2
    - **EXPECTED OUTCOME**: Tests PASS (confirms no regressions)
    - Confirm all tests still pass after fix (no regressions)

- [x] 4. Checkpoint - Ensure all tests pass
  - Ensure all tests pass, ask the user if questions arise.

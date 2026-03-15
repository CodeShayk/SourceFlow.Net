# GitHub Actions Setup Guide

This document provides setup instructions and troubleshooting guidance for SourceFlow.Net's GitHub Actions CI/CD pipelines.

## CodeQL Configuration Requirements

### Overview

SourceFlow.Net uses **advanced CodeQL workflow files** for security analysis. These workflows are located at:
- `.github/workflows/Release-CodeQL.yml` - Runs on release branches
- `.github/workflows/Master-CodeQL.yml` - Runs on master branch

### Required Configuration

**IMPORTANT**: GitHub's default CodeQL setup **MUST be disabled** in repository settings to prevent configuration conflicts.

#### Steps to Disable Default CodeQL Setup

1. Navigate to your GitHub repository
2. Go to **Settings** > **Code security and analysis**
3. Locate the **Code scanning** section
4. Find **CodeQL analysis** with "Default setup" badge
5. Click **Disable** to turn off default setup
6. Confirm the action

#### Why This Is Required

GitHub provides two ways to configure CodeQL:
- **Default Setup**: Automatic configuration managed by GitHub
- **Advanced Setup**: Custom workflow files (what we use)

These two approaches are mutually exclusive. If both are enabled, workflows will fail with:
```
Error: Advanced setup is currently configured but default setup would like to take over
```

### Verification

After disabling default setup, verify the configuration:
1. Push a commit to a release branch
2. Check that the `release-codeql` workflow runs successfully
3. Verify no configuration conflict errors appear

## CI Pipeline Architecture

### Workflow Overview

SourceFlow.Net uses multiple CI workflows for different purposes:

| Workflow | Trigger | Purpose | Version Format |
|----------|---------|---------|----------------|
| `Release-CI.yml` | Push to release/** branches | Build, test, and package release candidates | `2.0.0-beta.1` (pre-release) |
| `Release-CI.yml` | Push `release-packages` tag | Build and publish stable packages | `2.0.0` (stable) |
| `Release-CodeQL.yml` | Push to release/** branches | Security analysis for releases | N/A |
| `Master-CodeQL.yml` | Push to master branch | Security analysis for production | N/A |
| `Master-Build.yml` | Push to master branch | Production build validation | `2.0.0` (stable) |
| `PR-CI.yml` | Pull requests | Validate PR changes | `2.0.0-PullRequest.123` |
| `Pre-release-CI.yml` | Push to pre-release branches | Pre-release validation | `2.0.0-alpha.1` |

### Versioning Strategy

SourceFlow.Net uses GitVersion for semantic versioning with the following configuration:

**Release Branches** (`release/**`):
- **Branch Pushes**: Generate pre-release versions with 'beta' tag (e.g., `2.0.0-beta.1`, `2.0.0-beta.2`)
- **Tag Pushes** (`release-packages`): Generate stable versions (e.g., `2.0.0`)
- **Purpose**: Allows testing release candidates before final publication

**Pull Request Branches** (`pr/**`, `pull-request/**`):
- Generate versions with PR number (e.g., `2.0.0-PullRequest.123`)
- Inherit versioning strategy from source branch
- Clear identification of PR builds

**Pre-Release Branches** (`pre-release/**`):
- Generate versions with 'alpha' tag (e.g., `2.0.0-alpha.1`)
- Used for early testing and validation

**Master Branch**:
- Generate stable versions (e.g., `2.0.0`)
- Production-ready releases

### Test Execution Strategy

#### Unit Tests vs Integration Tests

The CI pipeline distinguishes between two types of tests:

**Unit Tests** (Run in CI):
- Fast execution (< 1 second per test)
- No external dependencies
- No Docker containers required
- Always run in GitHub Actions

**Integration Tests** (Excluded from CI):
- Require LocalStack or external services
- Use Docker containers
- May have longer execution times
- Can cause CI timeouts
- Run manually or in dedicated integration test workflows

**Security Tests** (Excluded from CI):
- Require IAM permissions and LocalStack services
- Test authentication and authorization scenarios
- Validate encryption and access control
- Run manually or in dedicated security test workflows

#### Test Filtering

The `Release-CI.yml` workflow uses test filtering to exclude integration and security tests:

```yaml
dotnet test --filter "FullyQualifiedName!~Integration&FullyQualifiedName!~Security"
```

This filter excludes:
- Any tests in namespaces or folders containing "Integration" in their name
- Any tests in namespaces or folders containing "Security" in their name (which require IAM/LocalStack services)

**Test Organization Guidelines**:
- Place unit tests in `Unit/` folders
- Place integration tests in `Integration/` folders
- Place security tests in `Security/` folders
- Use `[Trait("Category", "Integration")]` attribute for explicit categorization
- Use `[Trait("Category", "Security")]` attribute for security tests requiring IAM/LocalStack

## Troubleshooting

### NuGet Package Restore Issues

#### Symptom
```
error: Package 'sourceflow.cloud.core' not found
```

#### Cause
GitHub Actions NuGet cache may contain stale package metadata from removed packages.

#### Solution
The `Release-CI.yml` workflow includes cache clearing steps:

```yaml
- name: Step-06b Clear NuGet Cache
  run: dotnet nuget locals all --clear

- name: Step-07 Restore dependencies
  run: dotnet restore --no-cache --force
```

These steps ensure fresh package metadata is fetched on every build.

#### Manual Resolution
If issues persist, manually clear the GitHub Actions cache:
1. Go to **Actions** > **Caches**
2. Delete all NuGet-related caches
3. Re-run the workflow

### CodeQL Configuration Conflicts

#### Symptom
```
Error: Advanced setup is currently configured but default setup would like to take over
```

#### Cause
Both default CodeQL setup and advanced workflow files are enabled.

#### Solution
Disable default CodeQL setup as described in the [CodeQL Configuration Requirements](#codeql-configuration-requirements) section above.

### LocalStack Integration Test Timeouts

#### Symptom
- Tests hang or timeout in GitHub Actions
- LocalStack container fails to start
- Tests pass locally but fail in CI

#### Cause
- LocalStack requires Docker and may have startup delays in CI
- Integration tests may exceed GitHub Actions timeout limits
- Network connectivity issues between test runner and LocalStack

#### Solution
Integration tests are now excluded from CI by default. To run integration tests:

**Option 1: Run Locally**
```bash
dotnet test --filter "FullyQualifiedName~Integration"
```

**Option 2: Create Dedicated Integration Test Workflow**
Create a separate workflow that:
- Runs on manual trigger or scheduled basis
- Has longer timeout limits
- Includes comprehensive LocalStack health checks

### Build Failures After Package Consolidation

#### Symptom
- Build fails with missing package references
- Namespace not found errors for `SourceFlow.Cloud.Core.*`

#### Cause
The v2.0.0 release consolidated `SourceFlow.Cloud.Core` into the main `SourceFlow` package.

#### Solution
1. Update namespace imports:
   ```csharp
   // Old
   using SourceFlow.Cloud.Core.Configuration;
   
   // New
   using SourceFlow.Cloud.Configuration;
   ```

2. Update project references:
   ```xml
   <!-- Remove this -->
   <ProjectReference Include="..\SourceFlow.Cloud.Core\SourceFlow.Cloud.Core.csproj" />
   
   <!-- Keep only this -->
   <ProjectReference Include="..\SourceFlow\SourceFlow.csproj" />
   ```

3. See `docs/Architecture/06-Cloud-Core-Consolidation.md` for complete migration guide

## Best Practices

### Workflow Maintenance

1. **Keep workflows DRY**: Use reusable workflows for common steps
2. **Version pinning**: Pin action versions (e.g., `@v4` not `@latest`)
3. **Secrets management**: Use GitHub Secrets for sensitive data
4. **Cache strategy**: Clear caches when package structure changes

### Test Organization

1. **Separate concerns**: Keep unit and integration tests in separate folders
2. **Fast feedback**: Unit tests should run in < 5 minutes total
3. **Explicit categorization**: Use `[Trait]` attributes for test categories
4. **Local validation**: Run full test suite locally before pushing

### Security

1. **CodeQL analysis**: Ensure CodeQL runs on all release branches
2. **Dependency scanning**: Monitor for vulnerable dependencies
3. **Secret scanning**: Enable GitHub secret scanning
4. **SBOM generation**: Consider generating Software Bill of Materials

## Related Documentation

- [Cloud Core Consolidation](Architecture/06-Cloud-Core-Consolidation.md) - v2.0.0 architectural changes
- [Cloud Integration Testing](Cloud-Integration-Testing.md) - LocalStack testing guide
- [AWS Cloud Architecture](Architecture/07-AWS-Cloud-Architecture.md) - AWS integration details

## Support

For issues not covered in this guide:
1. Check existing GitHub Issues
2. Review workflow run logs in Actions tab
3. Consult the SourceFlow.Net documentation
4. Open a new issue with workflow logs and error messages

# 02 — Branching, Versioning and Repository Layout

## 1. Branch model

```mermaid
gitGraph
   commit id: "master @086673b (v2.0.0)"
   branch v3.0.0
   checkout v3.0.0
   commit id: "WP-0.1 branch + GitVersion"
   branch v3/wp-0.2-scaffold
   commit id: "scaffold"
   checkout v3.0.0
   merge v3/wp-0.2-scaffold id: "PR squash"
   checkout master
   commit id: "v2 hotfix (if any)"
   checkout v3.0.0
   commit id: "… M1–M6 …"
   checkout master
   branch v2.x
   checkout master
   merge v3.0.0 id: "GA cutover"
   commit id: "tag v3.0.0"
```

| Branch | Purpose | Rules |
|--------|---------|-------|
| `master` | v2 line until GA cutover | v2 hotfixes only. Each hotfix is assessed for forward-port (plan 09 §4) |
| **`v3.0.0`** | Integration branch for all v3 work, cut from `master` at M0 start | Protected. Changes arrive by PR only. Required checks: `v3-pr` (plan 08). Squash-merge. No direct pushes |
| `v3/<wp-id>-<slug>` | One branch per work package, for example `v3/wp-1.6-command-processor` | Branched from and merged back into `v3.0.0`. Deleted after merge |
| `v2.x` | Created from `master` immediately **before** the GA cutover | Receives v2 security and critical fixes for 12 months (design doc 12 §1) |
| `origin/azure-cloud` | Source of Azure scenarios for WP-3.4 | Read-only reference. Archived (`archive/azure-cloud`) at M3 exit |

## 2. Versioning (GitVersion)

The existing `GitVersion.yml` uses GitVersion 5 syntax (`tag:`) and has no rule matching `v3.0.0`, so the branch would fall into an unknown or feature configuration with unpredictable labels. **WP-0.1** changes it **on the `v3.0.0` branch only**:

```yaml
next-version: 3.0.0
tag-prefix: '[vV]'
mode: ContinuousDeployment
branches:
  # … existing master / release / pre-release / develop / pull-request / feature entries unchanged …
  v3:
    regex: ^v3\.0\.0$
    tag: alpha            # switched to 'rc' at M6 (WP-6.3)
    increment: None
    prevent-increment-of-merged-branch-version: true
    source-branches: ['master']
  v3-work:
    regex: ^v3/
    tag: '{BranchName}'
    increment: Inherit
    source-branches: ['v3']
```

The resulting versions are `3.0.0-alpha.N` on the branch, `3.0.0-wp-1-6-command-processor.N` on work branches, `3.0.0-rc.N` at M6, and `3.0.0` after cutover.

**Ref-name collision, which must be avoided.** Tags use the `v` prefix (`v2.0.0` exists). A branch named `v3.0.0` and a tag named `v3.0.0` would make `git checkout v3.0.0` and GitVersion's tag lookup ambiguous ("refname is ambiguous"). Rules:

1. Never create the tag `v3.0.0` while the branch `v3.0.0` exists. Pre-release tags (`v3.0.0-alpha.N`, `v3.0.0-rc.N`) are distinct names and are allowed.
2. At GA (WP-6.3): merge `v3.0.0` into `master`, **delete** the branch `v3.0.0` (keep `archive/v3.0.0` if history is wanted), and then tag `v3.0.0` on `master`.
3. CI scripts always use fully qualified refs (`refs/heads/v3.0.0`, `refs/tags/…`).

## 3. Target repository layout on `v3.0.0`

```text
/
├─ global.json                          # SDK 10.0.4xx, rollForward latestFeature
├─ Directory.Build.props                # net10.0, C# latest, nullable, warnings-as-errors, deterministic, SourceLink
├─ Directory.Packages.props             # Central Package Management (all versions here)
├─ .editorconfig                        # analyzer severities, naming
├─ SourceFlow.slnx                      # replaces SourceFlow.Net.sln (deleted in WP-0.2)
├─ GitVersion.yml                       # §2
├─ src/
│  ├─ SourceFlow.Abstractions/          # WP-1.1  (domain-facing contracts; BCL only)
│  ├─ SourceFlow.Generators/            # WP-1.2  (netstandard2.0 analyzer, packed into Abstractions)
│  ├─ SourceFlow.Net/                   # WP-1.3…1.10, 1.12, 1.13 (runtime core + in-memory adapters)
│  ├─ SourceFlow.Testing/               # WP-1.11 (specs, test host, conformance-kit bases)
│  ├─ SourceFlow.PostgreSql/            # WP-2.1…2.6, 2.8
│  ├─ SourceFlow.SqlServer/             # WP-4.4
│  ├─ SourceFlow.EntityFrameworkCore/   # WP-2.7
│  ├─ SourceFlow.AmazonSqs/             # WP-3.2
│  ├─ SourceFlow.AzureServiceBus/       # WP-3.4
│  ├─ SourceFlow.GooglePubSub/          # WP-3.3
│  ├─ SourceFlow.Security.AwsKms/       # WP-5.1
│  ├─ SourceFlow.Security.AzureKeyVault/# WP-5.1
│  ├─ SourceFlow.Security.GoogleKms/    # WP-5.1
│  ├─ SourceFlow.AspNetCore/            # WP-4.2
│  ├─ SourceFlow.OpenTelemetry/         # WP-4.6
│  ├─ SourceFlow.Tool/                  # WP-4.1, 5.4 (dotnet tool "sourceflow"); + manifest/workflow/studio commands (track S)
│  ├─ SourceFlow.Workflows.Abstractions/# WP-S.5  (track S, plan 10)
│  ├─ SourceFlow.Workflows/             # WP-S.3, S.4 (netstandard2.0 + net10.0)
│  └─ SourceFlow.Workflows.Wasm/        # WP-S.7  (browser-wasm)
├─ studio/                              # WP-S.8…S.12 React SPA, TS facade, static builder, workflow-diff Action
├─ schemas/                             # workflow-1.json (DSL schema)
├─ tests/                               # layout and naming in plan 04 §3
├─ samples/
│  ├─ Bank/                             # WP-1.14: Accounts, Transfers, Notifications services (ported v2 E2E domain)
│  └─ OrderFulfilment/                  # WP-S.13: orders, payments, inventory, shipping + sourceflow.domain.yaml
├─ benchmarks/
│  └─ SourceFlow.Benchmarks/            # WP-0.4, 2.10, 6.2
├─ deploy/
│  ├─ observability/                    # WP-4.3 generated Grafana dashboards + Prometheus rules
│  └─ topology/                         # WP-3.5 Terraform/Bicep modules consuming topology JSON
└─ docs/                                # design (v3.0.0/) and these plans
```

## 4. v2 → v3 project mapping and retirement

**Policy: retire-on-replace.**
- In WP-0.2, every v2 project is **removed from the solution and from v3 CI**, but its folder stays in the tree as read-only reference.
- The retirement package (`WP-m.R`) **deletes** the folder once its replacement meets the parity checklist in [plan 07](07-V2-Test-Disposition-Matrix.md).
- The v2 sources are always available on `master` and `v2.x`.

| v2 project | Replaced by | Created in | Retired (deleted) in |
|------------|-------------|------------|----------------------|
| `src/SourceFlow` | `SourceFlow.Abstractions`, `SourceFlow.Net`, `SourceFlow.Generators`, `SourceFlow.Testing` | M1 | **WP-1.R** |
| `tests/SourceFlow.Core.Tests` | `tests/SourceFlow.Net.Tests`, `tests/SourceFlow.Generators.Tests`, `tests/SourceFlow.E2E.Tests` | M1 | **WP-1.R** |
| `src/SourceFlow.Stores.EntityFramework` | `SourceFlow.PostgreSql`, `SourceFlow.SqlServer`, `SourceFlow.EntityFrameworkCore` | M2, M4 | **WP-2.R** (SQL Server parity arrives in WP-4.4; EF users are covered by PostgreSQL + EF read models) |
| `tests/SourceFlow.Stores.EntityFramework.Tests` | `tests/SourceFlow.PostgreSql.Tests`, `tests/SourceFlow.EntityFrameworkCore.Tests` (storage conformance kit) | M2 | **WP-2.R** |
| `src/SourceFlow.Cloud.AWS` + tests | `SourceFlow.AmazonSqs` (+ `Security.AwsKms` in M5), `tests/SourceFlow.AmazonSqs.Tests` | M3 | **WP-3.R** |
| `src/SourceFlow.Cloud.GCP` + tests | `SourceFlow.GooglePubSub`, `tests/SourceFlow.GooglePubSub.Tests` | M3 | **WP-3.R** |
| `origin/azure-cloud` `SourceFlow.Cloud.Azure` + tests | `SourceFlow.AzureServiceBus`, `tests/SourceFlow.AzureServiceBus.Tests` | M3 | Branch archived at **WP-3.R** (never merged into `v3.0.0`) |
| `tests/SourceFlow.Net.EntityFramework.Tests` (empty) | — | — | **WP-0.2** |
| `SourceFlow.Net.sln` | `SourceFlow.slnx` | WP-0.2 | **WP-0.2** |
| `.github/workflows/Master-Build.yml`, `GCP-Build.yml` | `v3-pr.yml`, `v3-nightly.yml` (plan 08) | WP-0.3 | Untouched on `master`. On `v3.0.0` they are disabled by branch filters (they don't trigger on `v3.0.0`) |

## 5. Package identities

| Package | NuGet ID | Note |
|---------|----------|------|
| Runtime core | `SourceFlow.Net` | Keeps the v2 core ID so existing users find the upgrade |
| Everything else | `SourceFlow.<Name>` as listed in §3 | New IDs. `SourceFlow.Cloud.AWS`, `SourceFlow.Cloud.GCP` and `SourceFlow.Stores.EntityFramework` get a final 2.x release note pointing to their replacements (WP-6.3) |

All packages are versioned together by GitVersion. `SourceFlow.Generators` is packed under `analyzers/dotnet/cs` inside `SourceFlow.Abstractions`; it isn't published separately.

## 6. Build and coding standards (enforced from WP-0.2)

| Setting | Value | Why |
|---------|-------|-----|
| `TargetFramework` | `net10.0` (generators: `netstandard2.0`) | ADR-001 |
| `LangVersion` / `Nullable` | `latest` / `enable` | |
| `TreatWarningsAsErrors` | `true` for `src/`, `false` for tests (analyzers still on) | Fail fast |
| `IsAotCompatible` / `IsTrimmable` | `true` for every runtime package except `SourceFlow.EntityFrameworkCore` and `SourceFlow.Tool` | Doc 01 portability goal |
| `GenerateDocumentationFile` | `true`. Public API without XML docs is an error | API quality |
| `ManagePackageVersionsCentrally` | `true` | One place for versions |
| `ContinuousIntegrationBuild`, `EmbedUntrackedSources`, SourceLink | on in CI | Reproducible, debuggable packages |
| Public API approval | `PublicApiGenerator` + Verify snapshot per package (plan 04 §6) | Detect breaking changes |
| Package versions | Pinned in `Directory.Packages.props`. **WP-0.2 checks each major against nuget.org at the time**: Microsoft.Extensions.* 10.x, Npgsql 10.x, Microsoft.Data.SqlClient (current), EF Core 10, current AWS SDK for .NET major, Azure.Messaging.ServiceBus 7.x, Google.Cloud.PubSub.V1 3.x, Polly 8.x, OpenTelemetry 1.x, System.CommandLine (current) | Avoid stale majors |

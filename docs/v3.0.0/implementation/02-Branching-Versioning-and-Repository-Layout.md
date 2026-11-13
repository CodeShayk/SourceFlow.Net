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
| `origin/azure-cloud` | Source of Azure scenarios for WP-3.4 | Read-only reference. Archived (`archive/azure-cloud`) at M3 exit. **As built (WP-3.R):** `archive/azure-cloud` points at `1209821`; the old branch name is removed by the maintainer |

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
├─ Directory.Build.targets              # UseSourceFlowGenerator=true wires the generator as an analyzer (in-repo consumers)
├─ src/                                 # categorised (ADR-027); every project net10.0 except the generator
│  ├─ Core/
│  │  ├─ SourceFlow.Net/                # WP-1.1, 1.3…1.10, 1.12, 1.13: the single core. Contracts (namespace SourceFlow),
│  │  │                                 #   runtime, in-memory adapters, workflow runtime types, and the extensibility SPIs:
│  │  │                                 #   SourceFlow.Storage / .Transport / .Security / .Query / .Pipeline / .Diagnostics
│  │  └─ SourceFlow.Generators/         # WP-1.2  (netstandard2.0 compiler plugin, packed into SourceFlow.Net; not published)
│  ├─ Storage/
│  │  ├─ SourceFlow.Storage.PostgreSQL/ # WP-2.1…2.6, 2.8
│  │  ├─ SourceFlow.Storage.SqlServer/  # WP-4.4
│  │  └─ SourceFlow.Storage.EntityFrameworkCore/ # WP-2.7
│  ├─ Cloud/
│  │  ├─ SourceFlow.Cloud.Aws/          # WP-3.2 transport, WP-5.1 KMS key provider
│  │  ├─ SourceFlow.Cloud.Azure/        # WP-3.4 transport, WP-5.1 Key Vault key provider
│  │  └─ SourceFlow.Cloud.Gcp/          # WP-3.3 transport, WP-5.1 Cloud KMS key provider
│  ├─ Hosting/
│  │  └─ SourceFlow.AspNetCore/         # WP-4.2
│  ├─ Tooling/
│  │  ├─ SourceFlow.Testing/            # WP-1.11 (specs, test host, conformance-kit bases)
│  │  └─ SourceFlow.Tool/               # WP-4.1, 5.4 (dotnet tool "sourceflow"); + manifest/workflow/studio commands (track S)
│  └─ Workflows/
│     ├─ SourceFlow.Studio/             # WP-S.3, S.4 DSL semantic core (Dsl/** also compiled into the generator)
│     └─ SourceFlow.Studio.Wasm/        # WP-S.7  (browser-wasm)
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
| `src/SourceFlow` | `src/Core/SourceFlow.Net` (+ `src/Core/SourceFlow.Generators`), `src/Tooling/SourceFlow.Testing` | M1 | **WP-1.R** |
| `tests/SourceFlow.Core.Tests` (v2; same project name as the v3 one, different folder) | `tests/Core/SourceFlow.Core.Tests`, `tests/Core/SourceFlow.Generators.Tests`, `tests/EndToEnd/SourceFlow.EndToEnd.Tests` | M1 | **WP-1.R** |
| `src/SourceFlow.Stores.EntityFramework` | `src/Storage/SourceFlow.Storage.PostgreSQL`, `.Storage.SqlServer`, `.Storage.EntityFrameworkCore` | M2, M4 | **WP-2.R** (SQL Server parity arrives in WP-4.4; EF users are covered by PostgreSQL + EF read models) |
| `tests/SourceFlow.Stores.EntityFramework.Tests` | `tests/Storage/SourceFlow.Storage.Tests` (storage conformance kit) | M2 | **WP-2.R** |
| `src/SourceFlow.Cloud.AWS` + tests | `src/Cloud/SourceFlow.Cloud.Aws` (KMS key provider added in M5), `tests/Cloud/SourceFlow.Cloud.Tests` | M3 | **WP-3.R** |
| `src/SourceFlow.Cloud.GCP` + tests | `src/Cloud/SourceFlow.Cloud.Gcp`, `tests/Cloud/SourceFlow.Cloud.Tests` | M3 | **WP-3.R** |
| `origin/azure-cloud` `SourceFlow.Cloud.Azure` + tests | `src/Cloud/SourceFlow.Cloud.Azure`, `tests/Cloud/SourceFlow.Cloud.Tests` | M3 | Branch archived at **WP-3.R** (never merged into `v3.0.0`) |
| `tests/SourceFlow.Net.EntityFramework.Tests` (empty) | — | — | **WP-0.2** |
| `SourceFlow.Net.sln` | `SourceFlow.slnx` | WP-0.2 | **WP-0.2** |
| `.github/workflows/Master-Build.yml`, `GCP-Build.yml` | `v3-pr.yml`, `v3-nightly.yml` (plan 08) | WP-0.3 | Untouched on `master`. On `v3.0.0` they are disabled by branch filters (they don't trigger on `v3.0.0`) |

## 5. Package identities

| Package | NuGet ID | Note |
|---------|----------|------|
| Core | `SourceFlow.Net` | Keeps the v2 core ID so existing users find the upgrade. Contains the contracts, runtime, SPIs and the generator |
| Cloud | `SourceFlow.Cloud.Aws`, `SourceFlow.Cloud.Gcp` | **Continue** the v2 IDs `SourceFlow.Cloud.AWS` / `SourceFlow.Cloud.GCP` (NuGet IDs are case-insensitive); 3.0.0 is the breaking major |
| Cloud | `SourceFlow.Cloud.Azure` | First release (the 2.x Azure provider never shipped from `master`) |
| Everything else | `SourceFlow.<Category>.<Name>` or `SourceFlow.<Name>` as listed in §3 | New IDs. `SourceFlow.Stores.EntityFramework` gets a final 2.x release note pointing to `SourceFlow.Storage.PostgreSQL` + `SourceFlow.Storage.EntityFrameworkCore` (WP-6.3) |

All packages are versioned together by GitVersion. `SourceFlow.Generators` is packed under `analyzers/dotnet/cs` inside `SourceFlow.Net`; it isn't published separately. `SourceFlow.Studio.Wasm` ships inside `@sourceflow/studio` and the tool, not on NuGet. That makes **11 NuGet packages** ([ADR-027](../ADRs/ADR-027-single-core-package-with-extensibility-spis-and-categorised-adapters.md)).

## 6. Build and coding standards (enforced from WP-0.2)

| Setting | Value | Why |
|---------|-------|-----|
| `TargetFramework` | `net10.0` only. Single exception: `SourceFlow.Generators` targets `netstandard2.0` because Roslyn requires it for compiler plugins (RS1041); it never runs in the application | ADR-001, ADR-027 |
| `LangVersion` / `Nullable` | `latest` / `enable` | |
| `TreatWarningsAsErrors` | `true` for `src/`, `false` for tests (analyzers still on) | Fail fast |
| `IsAotCompatible` / `IsTrimmable` | `true` for every runtime package except `SourceFlow.Storage.EntityFrameworkCore` and `SourceFlow.Tool` | Doc 01 portability goal |
| `GenerateDocumentationFile` | `true`. Public API without XML docs is an error | API quality |
| `ManagePackageVersionsCentrally` | `true` | One place for versions |
| `ContinuousIntegrationBuild`, `EmbedUntrackedSources`, SourceLink | on in CI | Reproducible, debuggable packages |
| Public API approval | `PublicApiGenerator` + approved-file snapshot per package (plan 04 §6) | Detect breaking changes |
| Package versions | Pinned in `Directory.Packages.props`. **WP-0.2 checks each major against nuget.org at the time**: Microsoft.Extensions.* 10.x, Npgsql 10.x, Microsoft.Data.SqlClient (current), EF Core 10, current AWS SDK for .NET major, Azure.Messaging.ServiceBus 7.x, Google.Cloud.PubSub.V1 3.x, Polly 8.x, OpenTelemetry 1.x, System.CommandLine (current) | Avoid stale majors |

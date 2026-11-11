# 08 — CI/CD Pipelines and Quality Gates

## 1. Workflows on the `v3.0.0` branch

The v2 workflows (`Master-Build`, `GCP-Build`, `Release-CI`, CodeQL) stay unchanged for `master`. Their branch filters don't match `v3.0.0`, so they don't run there. New workflows (WP-0.3):

| Workflow | Trigger | Purpose | Target duration |
|----------|---------|---------|-----------------|
| `v3-pr.yml` | `pull_request` into `v3.0.0`; `push` to `v3.0.0` | Required checks for every merge | ≤ 25 min |
| `v3-nightly.yml` | schedule 02:00 UTC; manual | Full matrices, real cloud, chaos, long property runs, benchmarks trend | ≤ 3 h |
| `v3-weekly.yml` | schedule Sunday | Mutation testing (Stryker), fuzzing (2 h budget, including the request guard and **both** SFQL parsers), dependency licence report | ≤ 6 h |
| `v3-codeql.yml` | push and PR to `v3.0.0`; weekly | CodeQL C# (security-extended) | — |
| `v3-release.yml` | push to `v3.0.0` (alpha packages); tags `v3.0.0-rc.*`; tag `v3.0.0` on `master` after cutover | Pack, sign, SBOM, publish | — |

Track S adds `studio-pr.yml`, the `workflow-diff` Action and Studio release tags ([plan 10 §4](10-Studio-Workflow-Track-Plan.md#4-ci-additions-extends-plan-08)).

Dependency review runs on every PR (`actions/dependency-review-action`). Renovate keeps `Directory.Packages.props` and the pinned emulator images current (grouped weekly PRs into `v3.0.0`).

## 2. `v3-pr.yml` jobs

```mermaid
flowchart LR
    B[build<br/>ubuntu + windows<br/>-warnaserror, AOT publish check] --> U[unit<br/>Core, Generators, Hosting,<br/>Tooling, Studio, Architecture]
    B --> S[storage<br/>PostgreSQL 17 SCK + QRY<br/>SQL Server 2022 from M4]
    B --> T[transports matrix<br/>LocalStack · Pub/Sub emu · Service Bus emu<br/>UT + TCK]
    U & S & T --> E[e2e<br/>in-memory + PostgreSQL × emulators]
    U & S & T & E --> C[coverage merge + gate<br/>API approval + traceability ARCH-08]
    B --> P[micro-benchmarks<br/>regression gate]
    C --> K[pack (no publish)]
```

| Job | Details |
|-----|---------|
| build | `dotnet build SourceFlow.slnx -c Release -warnaserror` on `ubuntu-latest` and `windows-latest`. `dotnet publish samples/Bank/Bank.Api -p:PublishAot=true` (ARCH-06) on Linux |
| unit | `dotnet test --solution SourceFlow.slnx --filter-trait "Category=Unit" --filter-trait "Category=Conformance" --filter-not-trait "Requires=Docker"` (Microsoft.Testing.Platform runner, opted in by `global.json`; repeated `--filter-trait` options are OR-ed) with coverage collection. Includes the in-memory SCK and TCK |
| storage | Testcontainers PostgreSQL 17 (SQL Server 2022 from M4). SCK, SCK-M, QRY-21..45 |
| transports | Matrix over `aws` (LocalStack `3.x` pinned), `gcp` (Pub/Sub emulator), `azure` (Service Bus emulator + SQL Edge). Each runs its unit tests plus the emulator TCK subset |
| e2e | E2E scenarios for in-memory + PostgreSQL, parameterised over the three emulators (E2E-17..24) |
| coverage | ReportGenerator merges all Cobertura files. `tools/coverage-gate` applies plan 04 §5 thresholds per package and the "no drop > 0.5 pt" rule against the `v3.0.0` base. A summary is posted on the PR |
| api + traceability | PublicApi approval tests and ARCH-08 fail on unapproved API or uncovered IDs |
| micro-benchmarks | BenchmarkDotNet short run of PERF-01, PERF-02 and serializer benchmarks. Fail on > 10 % time or any allocation regression versus the stored baseline. Runs when `src/Core/**` changes |
| pack | `dotnet pack` all packages (verifies the generator is packed into `SourceFlow.Net` under `analyzers/dotnet/cs`, every `lib/` folder is `net10.0` only, package metadata, README, icon) |

## 3. `v3-nightly.yml` jobs

| Job | Details |
|-----|---------|
| Database matrix | SCK + QRY on PostgreSQL 15, 16, 17, 17-partitioned; SQL Server 2022, 2025 |
| Real cloud TCK | AWS, Azure and GCP sandbox accounts through **OIDC federation** (no long-lived secrets): GitHub → AWS IAM role; Azure federated credential; GCP Workload Identity Federation. Two roles per cloud: *provisioner* (CreateIfMissing cases) and *describe-only* (`Validate`, TCK-36). Every `RealCloudOnly` case runs here |
| Real KMS | UT-SEC integration on AWS KMS, Azure Key Vault and Google Cloud KMS |
| E2E real cloud | E2E-17..24 on real brokers |
| Chaos | CH-01..10 (Toxiproxy, container kill, SIGSTOP, long transaction) |
| Long property runs | SCK-24 gap-freedom and linearisability with 50× iterations |
| Component benchmarks | PERF-03..08. Results are pushed to the benchmark trend store (GitHub Pages JSON). A 15 % regression opens an issue automatically |
| Sweeper | Deletes cloud resources with prefix `sourceflow-test` older than 24 h. Budget alarms on each sandbox account |
| DAST | OWASP ZAP API scan of the Bank sample's query API (`/query*`), driven by its OpenAPI document, plus the QRY-57..59 injection corpus. Any high or medium finding fails the night and opens a security issue |

## 4. Benchmark baselines

- Baselines live in `benchmarks/baselines/*.json`. They are updated only by a PR labelled `perf-baseline` with reviewer approval.
- Micro-benchmarks run on a fixed GitHub runner type; component benchmarks run on a self-hosted runner pool sized as doc 09 assumes (4 vCPU, same-AZ database), so results can be compared over time.

## 5. Emulators

| Emulator | Source | Image policy | Health gate |
|----------|--------|--------------|-------------|
| LocalStack | v2 `Master-Build.yml` | **Pinned to `localstack/localstack:3.x`** (v2 commit `223c37a` pinned it after licence errors on `latest`). Renovate PRs for newer images need a manual licence review | `/_localstack/health` reports sqs, sns, kms `available` |
| Pub/Sub emulator | v2 `.github/gcp-emulator` (`gcp_cloud`) | Pinned `gcr.io/google.com/cloudsdktool/google-cloud-cli:<version>-emulators` | Topic create succeeds |
| Service Bus emulator | `azure-cloud` `.github/azure-emulator` | Pinned `mcr.microsoft.com/azure-messaging/servicebus-emulator:<version>` + `mcr.microsoft.com/azure-sql-edge` (or mssql) | Emulator health endpoint + a test queue round trip |

All three move to `.github/emulators/<name>/` (WP-0.3). Tests prefer the Testcontainers modules where they exist and fall back to the compose files otherwise.

## 6. Release pipeline (`v3-release.yml`)

| Stage | On `v3.0.0` push | On `v3.0.0-rc.*` tag | On `v3.0.0` tag (`master`, after cutover) |
|-------|------------------|----------------------|-------------------------------------------|
| Version | `3.0.0-alpha.N` (GitVersion) | `3.0.0-rc.N` | `3.0.0` |
| Build + full PR gate | ✔ | ✔ | ✔ |
| Sign (NuGet author signing), SBOM (CycloneDX), SourceLink | ✔ | ✔ | ✔ |
| Publish | GitHub Packages | GitHub Packages + NuGet.org (pre-release) | GitHub Packages + **NuGet.org** |
| Release notes | — | Draft from merged PR labels | `docs/Versions/v3.0.0/CHANGELOG.md` (WP-6.3) |

Publishing to NuGet.org was disabled in v2's `Release-CI`. It is enabled for v3 RC and GA, with the API key stored as an environment-protected secret that requires a release approver.

## 7. Quality gates

| Gate | When | Blocks |
|------|------|--------|
| Build warnings = 0, analyzers clean, AOT analysis clean | Every PR | Merge |
| Unit, SCK (in-memory + PostgreSQL), TCK (in-memory + emulators), E2E (in-memory + PostgreSQL + emulators) all green | Every PR | Merge |
| Coverage thresholds and no-drop rule (plan 04 §5) | Every PR | Merge |
| Public API approved; ARCH-01..08 green | Every PR | Merge |
| Micro-benchmark regression ≤ 10 % time, 0 extra allocations | PRs touching the core | Merge |
| Nightly green for 5 consecutive nights | Milestone exit | Milestone sign-off |
| Real-cloud TCK green for AWS, Azure and Google Cloud | M3 exit and every later milestone | Milestone sign-off (all three release-blocking per Q5/Q6) |
| Chaos suite green | M4 exit, RC, GA | Release |
| Mutation score ≥ 70 % (core) | M6 | GA |
| Zero quarantined tests; zero open Sev-1/Sev-2 bugs | RC → GA | GA |

## 8. Branch protection for `v3.0.0`

Required status checks: `build (ubuntu)`, `build (windows)`, `unit`, `storage`, `transports (aws|gcp|azure)`, `e2e`, `coverage`, `api-traceability`, `codeql`. Also required: one code-owner review (two for `src/Core/**` and `src/Storage/**`), up-to-date branches, squash-merge only, signed commits recommended, and no force-push or deletion (until WP-6.3).

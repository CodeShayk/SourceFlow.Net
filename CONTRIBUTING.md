# Contributing to SourceFlow.Net v3

v3 is being built on the **`v3.0.0`** integration branch according to the plans in [`docs/v3.0.0/implementation`](docs/v3.0.0/implementation/README.md). `master` carries v2 until the GA cutover.

## Branches

| Branch | Use |
|--------|-----|
| `v3.0.0` | Integration branch. Protected: changes arrive by pull request only (squash merge) |
| `v3/<wp-id>-<slug>` | One branch per work package, e.g. `v3/wp-1.6-command-processor`, or per milestone slice, e.g. `v3/m1-core`. Branch from `v3.0.0`, open the PR back into `v3.0.0` |
| `master` | v2 hotfixes only. Record each one in the [forward-port log](docs/v3.0.0/implementation/forward-port-log.md) |

Never create a tag named `v3.0.0` while the `v3.0.0` branch exists (implementation plan 02 §2). Pre-release tags such as `v3.0.0-rc.1` are fine.

## Pull requests

A PR into `v3.0.0` merges only when it meets the work package's **Definition of Done** ([plan 09 §1](docs/v3.0.0/implementation/09-Definition-of-Done-Risks-and-Tracking.md#1-definition-of-done-per-work-package)):

- every deliverable and test ID assigned to the work package exists and passes;
- tests carry `[Trait("Id", "…")]` and `[Trait("Covers", "…")]`;
- coverage meets [plan 04 §5](docs/v3.0.0/implementation/04-Test-Strategy-and-Coverage.md#5-coverage-targets-and-enforcement);
- the public API approval files are updated and reviewed;
- the build has zero warnings and no AOT or trimming warnings;
- any divergence from the design is written into the design docs in the same PR.

## Building and testing

```bash
dotnet build SourceFlow.slnx -c Release
dotnet test --solution SourceFlow.slnx -c Release --filter-not-trait "Category=Integration"
```

`dotnet test` runs on Microsoft.Testing.Platform (opted in by `global.json`), so filters use the xUnit v3 options (`--filter-trait`, `--filter-not-trait`, `--filter-query`), not the VSTest `--filter` syntax.

Integration tests need Docker. See [plan 06](docs/v3.0.0/implementation/06-Integration-and-E2E-Test-Plan.md).

Update [`STATUS.md`](docs/v3.0.0/implementation/STATUS.md) in the PR that completes a work package.

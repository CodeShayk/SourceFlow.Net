# ADR-025: SourceFlow Studio is a React SPA with a shared WebAssembly core, hosted locally

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

The product requires the visual designer to be built in **React** (product doc 07, WFD-30…37) as a separate utility. Validation and simulation in the browser must give exactly the same results as the build-time generator (WFD-NFR-2). The tool must work offline and against git (WFD-NFR-5), and must also render read-only diagrams for documentation and pull requests (WFD-36).

## Decision

1. **A React 19 + TypeScript single-page application** (`studio/`, npm `@sourceflow/studio`). It uses React Flow + elkjs for graphs, Monaco + monaco-yaml for text, and Radix primitives for accessibility.
2. **The semantic core is the C# `SourceFlow.Workflows` library compiled to WebAssembly** (`SourceFlow.Workflows.Wasm`, `[JSExport]`), running in a Web Worker. TypeScript never re-implements parsing, validation or simulation.
3. **Hosting**:
   - **local**: `dotnet sourceflow studio` serves the embedded SPA and a loopback-only file API over the workspace;
   - **static**: a read-only site built from a workspace;
   - **CI**: a GitHub Action renders before/after SVGs for PRs.

   There is no server database: **git is the store**.
4. **The DSL text is the model.** Visual edits produce minimal YAML edits through a CST-preserving writer.

## Alternatives

| Option | Why not chosen |
|--------|----------------|
| Blazor WebAssembly UI | The product requirement is React. React Flow and elkjs are also stronger ecosystems for graph editors |
| Re-implementing the core in TypeScript | Two implementations of workflow semantics would drift. Shared golden tests reduce the risk but don't remove it (violates DRY) |
| Server-side validation over HTTP | Needs a running backend for the static and PR modes; latency while editing |
| Electron desktop app | Heavier to distribute; the browser plus a local `dotnet tool` covers the need |
| Hosted SaaS designer | Security and privacy concerns (code topology leaves the network); out of scope |

## Consequences

- (+) Identical semantics everywhere. Studio works offline. Diagrams render in docs and PRs.
- (−) The WASM bundle is a few MB, loaded lazily in a worker with a cached download (implementation risk IR-15). Requires .NET WASM build tooling in CI.
- (−) The team needs React/TypeScript skills: a dedicated lane in the implementation plan (lane F).

## Traces

WFD-30…37, WFD-NFR-2…5.

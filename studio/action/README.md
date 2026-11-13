# SourceFlow workflow diff

A GitHub Action for pull requests that change SourceFlow workflow files (`*.sfflow.yaml`). For each changed workflow it:

- draws the statechart before and after the change, as SVG files, with what was added (＋), removed (−) or changed (△) marked by a word as well as a colour;
- lists the changes in words;
- comments once on the pull request with that list and Mermaid diagrams (GitHub draws them), and updates the same comment on every push.

It uses the same core as SourceFlow Studio and the build (the workflow engine compiled to WebAssembly), so the picture is what the generated process manager runs. Nothing leaves the runner except the comment.

## Usage

```yaml
on:
  pull_request:
    paths: [ "**/*.sfflow.yaml" ]

permissions:
  contents: read
  pull-requests: write

jobs:
  diff:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0          # the base commit's files too
      - uses: sourceflow/workflow-diff@v1
        with:
          out: workflow-diff
      - uses: actions/upload-artifact@v4
        with:
          name: workflow-diff
          path: workflow-diff
          if-no-files-found: ignore
```

| Input | Default | |
|-------|---------|--|
| `token` | `github.token` | Comments with it (needs `pull-requests: write`). |
| `base`, `head` | the pull request's | The commits to compare. |
| `out` | `workflow-diff` | Where the SVGs go: `<path with folders joined by __>.before.svg` and `.after.svg`. |
| `theme` | `light` | `light` or `dark`. |
| `comment` | `true` | `false` only writes the SVGs (for example on pull requests from forks, whose token is read-only). |
| `artifact` | `workflow-diff` | The artifact name the comment points to. |

Outputs: `changed` (how many workflow files changed) and `out`.

A workflow that can't be read is reported in the comment with the core's message, without a picture. A change to comments or formatting only is reported as "no change to the statechart".

## Building

In `studio/`: `npm run build:wasm`, then `npm run build:action`. That writes `dist/main.js` (everything bundled) and `dist/framework` (the WebAssembly core); `action.yml` runs `dist/main.js` on Node 24. This repository runs it from source in `.github/workflows/workflow-diff.yml`. Publishing it as `sourceflow/workflow-diff` means pushing `action.yml`, `README.md` and `dist/` to that repository and tagging a release.

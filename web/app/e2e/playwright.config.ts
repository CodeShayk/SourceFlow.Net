import { defineConfig } from '@playwright/test';
import { fileURLToPath } from 'node:url';

// STU-E2E-01..15, STU-A11Y-01..05 and STU-PERF-01..03 (plan 10 §3.3): the built app in a browser, served by `dotnet sourceflow studio` (a writable host and a
// read-only one) with the published WebAssembly core. Build first: npm run build:wasm && npm run build, and
// dotnet build src/Tooling/SourceFlow.Tool. Locally, PW_CHANNEL=chrome (or msedge) uses an installed browser.
const app = fileURLToPath(new URL('..', import.meta.url));
const framework = '../../artifacts/studio-wasm/wwwroot/_framework';
const tool = '../../src/Tooling/SourceFlow.Tool';

function host(port: number, readOnly: boolean, workspace = 'e2e/workspace', before = '') {
  return {
    command: `${before}dotnet run --project ${tool} --no-build -- studio --workspace ${workspace} --port ${port} --assets dist --framework ${framework}${readOnly ? ' --read-only' : ''}`,
    cwd: app,
    url: `http://127.0.0.1:${port}/api/workspace`,
    reuseExistingServer: false,
    timeout: 120_000,
    stdout: 'ignore' as const,
    stderr: 'pipe' as const,
  };
}

export default defineConfig({
  testDir: '.',
  testMatch: '*.spec.ts',
  timeout: 60_000,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? 'github' : 'list',
  use: {
    baseURL: 'http://127.0.0.1:5199',
    ...(process.env.PW_CHANNEL ? { channel: process.env.PW_CHANNEL } : {}),
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'chromium', use: { browserName: 'chromium' }, testIgnore: 'perf.spec.ts' },
    { name: 'perf', use: { browserName: 'chromium' }, testMatch: 'perf.spec.ts', dependencies: ['chromium'] },
  ],
  webServer: [
    host(5199, false),
    host(5198, true),
    // STU-PERF-02: a generated workspace of 20 services and 50 workflows.
    host(5197, true, '../../artifacts/studio-e2e/large', 'node e2e/generate.mjs large-workspace ../../artifacts/studio-e2e/large && '),
    // STU-E2E-01 and STU-A11Y-04: the OrderFulfilment sample's workspace, as committed (read-only).
    host(5194, true, '../../samples/OrderFulfilment'),
    // STU-E2E-07..10: a fresh, writable copy of the golden workspace for each run.
    host(5196, false, '../../artifacts/studio-e2e/editable', 'node e2e/generate.mjs editable-workspace ../../artifacts/studio-e2e/editable && '),
    // STU-E2E-15: the static site of e2e/workspace (npx @sourceflow/studio build), on a plain file server under a sub-path.
    {
      command: 'node bin/studio.mjs build --workspace e2e/workspace --out ../../artifacts/studio-e2e/site && node e2e/serve-static.mjs ../../artifacts/studio-e2e/site 5195 /docs/studio/',
      cwd: app,
      url: 'http://127.0.0.1:5195/docs/studio/index.html',
      reuseExistingServer: false,
      timeout: 120_000,
      stdout: 'ignore' as const,
      stderr: 'pipe' as const,
    },
  ],
});

import { defineConfig } from '@playwright/test';
import { fileURLToPath } from 'node:url';

// STU-E2E-01..03 (plan 10 §3.3): the built app in a browser, served by `dotnet sourceflow studio` (a writable host and a
// read-only one) with the published WebAssembly core. Build first: npm run build:wasm && npm run build, and
// dotnet build src/Tooling/SourceFlow.Tool. Locally, PW_CHANNEL=chrome (or msedge) uses an installed browser.
const app = fileURLToPath(new URL('..', import.meta.url));
const framework = '../../artifacts/studio-wasm/wwwroot/_framework';
const tool = '../../src/Tooling/SourceFlow.Tool';

function host(port: number, readOnly: boolean) {
  return {
    command: `dotnet run --project ${tool} --no-build -- studio --workspace e2e/workspace --port ${port} --assets dist --framework ${framework}${readOnly ? ' --read-only' : ''}`,
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
  projects: [{ name: 'chromium', use: { browserName: 'chromium' } }],
  webServer: [host(5199, false), host(5198, true)],
});

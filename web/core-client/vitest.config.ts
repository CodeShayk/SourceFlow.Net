import { defineConfig } from 'vitest/config';

// WASM-01..08 (plan 10 §3): the built core in Node, against the WF-GOLD fixtures the .NET suite uses.
export default defineConfig({
  test: {
    include: ['test/**/*.test.ts'],
    testTimeout: 60_000,
    hookTimeout: 120_000,
    pool: 'forks',
    // The .NET runtime files are imported as they are, not through Vite's transform.
    server: { deps: { external: [/_framework[\/]/] } },
  },
});

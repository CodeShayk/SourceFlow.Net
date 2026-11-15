import { defineConfig } from 'vitest/config';

// The workflow-diff action against a fixture repository: real git, the real core (WebAssembly in Node), real ELK, and a
// fake GitHub API.
export default defineConfig({
  test: {
    include: ['test/**/*.test.ts'],
    testTimeout: 120_000,
    hookTimeout: 120_000,
    pool: 'forks',
    server: { deps: { external: [/_framework[\/]/] } },
  },
});

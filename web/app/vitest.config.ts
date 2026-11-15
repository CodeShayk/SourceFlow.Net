import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// STU-UT (store, workspace mapping, keys, theme, API client) and STU-CT (components) in jsdom.
export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    include: ['test/**/*.test.{ts,tsx,mjs}'],
    setupFiles: ['test/setup.ts'],
  },
});

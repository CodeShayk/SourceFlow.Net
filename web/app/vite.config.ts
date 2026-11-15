import react from '@vitejs/plugin-react';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { defineConfig } from 'vite';

// Monaco 0.57 exports `monaco-editor/<path>` for its ESM files; monaco-yaml's worker manager still imports the older
// `monaco-editor/esm/vs/<path>`. The alias points that at the same files.
// (Its exports map hides package.json: its main entry, min/vs/index.js, is two folders below the package.)
const monaco = join(dirname(createRequire(import.meta.url).resolve('monaco-editor')), '..', '..');

// The Studio SPA. In development the local host (dotnet sourceflow studio, port 5180) serves the API and the
// WebAssembly core; `npm run build` writes dist/, which the tool embeds or serves with --assets.
export default defineConfig({
  // Relative URLs everywhere (assets, workers, the ELK worker): the static site (npx @sourceflow/studio build) works from
  // any folder. index.html's <base href="/"> keeps them at the root for the local host's deep routes; the static build
  // drops it.
  base: './',
  plugins: [react()],
  resolve: {
    alias: [{ find: /^monaco-editor\/esm\/vs\/(.*)$/, replacement: join(monaco, 'esm', 'vs', '$1') }],
  },
  worker: { format: 'es' },
  // React Flow makes the app ~520 kB (170 kB gzip); ELK's worker and Monaco load only when they are first needed.
  build: { outDir: 'dist', emptyOutDir: true, sourcemap: false, target: 'es2022', chunkSizeWarningLimit: 4000 },
  server: {
    port: 5173,
    fs: { allow: ['..', '../../schemas'] },
    proxy: {
      '/api': 'http://127.0.0.1:5180',
      '/_framework': 'http://127.0.0.1:5180',
    },
  },
});

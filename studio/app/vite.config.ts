import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// The Studio SPA. In development the local host (dotnet sourceflow studio, port 5180) serves the API and the
// WebAssembly core; `npm run build` writes dist/, which the tool embeds or serves with --assets.
export default defineConfig({
  plugins: [react()],
  worker: { format: 'es' },
  // React Flow makes the app ~520 kB (170 kB gzip); ELK's 1.6 MB worker loads only when a diagram is laid out.
  build: { outDir: 'dist', emptyOutDir: true, sourcemap: false, target: 'es2022', chunkSizeWarningLimit: 700 },
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://127.0.0.1:5180',
      '/_framework': 'http://127.0.0.1:5180',
    },
  },
});

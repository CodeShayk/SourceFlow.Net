import { defineConfig } from 'vite';

// The action runs as one file on the runner's Node (action.yml: node24): everything is bundled except the WebAssembly
// core, which scripts/bundle-framework.mjs copies beside it (dist/framework) and main.ts loads at run time.
export default defineConfig({
  build: {
    ssr: 'src/main.ts',
    outDir: 'dist',
    emptyOutDir: true,
    target: 'node22',
    rollupOptions: { output: { entryFileNames: 'main.js', format: 'es' } },
  },
  ssr: { noExternal: true, target: 'node' },
});

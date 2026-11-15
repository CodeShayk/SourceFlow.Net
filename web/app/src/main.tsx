import { createBrowserClient } from '@sourceflow/core-client';
import '@xyflow/react/dist/style.css';
import ELK from 'elkjs/lib/elk-api.js';
import elkWorker from 'elkjs/lib/elk-worker.min.js?url';
import { elkEngine } from './graph/layout.ts';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { lazy, StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, HashRouter } from 'react-router';
import { hostApi, staticApi } from './api.ts';
import { App } from './App.tsx';
import { ServicesProvider } from './services.tsx';
import './styles.css';

// The WebAssembly core loads in its worker on the first validation; the local host serves it at /_framework/.
// ELK lays out in its own worker, so a large statechart never blocks the page.
// A static site (npx @sourceflow/studio build) marks its index.html: it reads the snapshot beside it, keeps the route in
// the hash (a static host has no fallback to index.html) and loads the core from its own folder, wherever it is served.
const staticSite = document.querySelector('meta[name="sourceflow-mode"]')?.getAttribute('content') === 'static';
const site = new URL('.', window.location.href).href;
const Router = staticSite ? HashRouter : BrowserRouter;
const services = {
  api: staticSite ? staticApi(site) : hostApi(),
  core: createBrowserClient(new URL(staticSite ? '_framework/' : '/_framework/', site).href),
  layout: elkEngine(new ELK({ workerUrl: elkWorker })),
  // Monaco is the largest part of the app: it loads when the YAML tab first opens.
  codeEditor: lazy(() => import('./edit/MonacoEditor.tsx')),
};
const queries = new QueryClient({ defaultOptions: { queries: { retry: 1, refetchOnWindowFocus: false, staleTime: 5_000 } } });

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queries}>
      <ServicesProvider services={services}>
        <Router>
          <App />
        </Router>
      </ServicesProvider>
    </QueryClientProvider>
  </StrictMode>,
);

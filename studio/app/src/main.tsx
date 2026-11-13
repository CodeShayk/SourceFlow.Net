import { createBrowserClient } from '@sourceflow/core-client';
import '@xyflow/react/dist/style.css';
import ELK from 'elkjs/lib/elk-api.js';
import elkWorker from 'elkjs/lib/elk-worker.min.js?url';
import { elkEngine } from './graph/layout.ts';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { lazy, StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router';
import { hostApi } from './api.ts';
import { App } from './App.tsx';
import { ServicesProvider } from './services.tsx';
import './styles.css';

// The WebAssembly core loads in its worker on the first validation; the local host serves it at /_framework/.
// ELK lays out in its own worker, so a large statechart never blocks the page.
const services = {
  api: hostApi(),
  core: createBrowserClient(new URL('/_framework/', window.location.href).href),
  layout: elkEngine(new ELK({ workerUrl: elkWorker })),
  // Monaco is the largest part of the app: it loads when the YAML tab first opens.
  codeEditor: lazy(() => import('./edit/MonacoEditor.tsx')),
};
const queries = new QueryClient({ defaultOptions: { queries: { retry: 1, refetchOnWindowFocus: false, staleTime: 5_000 } } });

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queries}>
      <ServicesProvider services={services}>
        <BrowserRouter>
          <App />
        </BrowserRouter>
      </ServicesProvider>
    </QueryClientProvider>
  </StrictMode>,
);

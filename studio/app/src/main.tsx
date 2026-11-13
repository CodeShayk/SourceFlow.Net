import { createBrowserClient } from '@sourceflow/core-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router';
import { hostApi } from './api.ts';
import { App } from './App.tsx';
import { ServicesProvider } from './services.tsx';
import './styles.css';

// The WebAssembly core loads in its worker on the first validation; the local host serves it at /_framework/.
const services = { api: hostApi(), core: createBrowserClient(new URL('/_framework/', window.location.href).href) };
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

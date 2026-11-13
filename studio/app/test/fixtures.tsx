// Test fixtures: a commerce workspace, fake host API and core, and a render helper for the shell.
import type { Diagnostic, ValidateRequest, ValidateResult } from '@sourceflow/core-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { vi } from 'vitest';
import type { WorkspaceApi, WorkspaceDto } from '../src/api.ts';
import { App } from '../src/App.tsx';
import { ServicesProvider, type StudioCore } from '../src/services.tsx';

export const ordersManifest = JSON.stringify({
  manifestVersion: 1,
  service: 'orders',
  aggregates: [{ name: 'order', commands: [{ type: 'orders.place-order' }, { type: 'orders.cancel-order' }], events: [] }],
});

export function workspace(overrides: Partial<WorkspaceDto> = {}): WorkspaceDto {
  return {
    domain: 'commerce',
    description: 'Order to delivery',
    file: 'sourceflow.domain.yaml',
    readOnly: false,
    services: [
      {
        name: 'orders',
        manifest: 'manifests/orders.manifest.json',
        manifestJson: ordersManifest,
        workflows: 'orders/workflows',
        project: 'orders/src',
        workflowFiles: ['orders/workflows/returns.sfflow.yaml', 'orders/workflows/order-fulfilment.sfflow.yaml'],
      },
      { name: 'inventory', manifest: 'manifests/inventory.manifest.json', manifestJson: '{"service":"inventory"}', workflowFiles: [] },
    ],
    ...overrides,
  };
}

export const fulfilment = 'sourceflow.workflow: 1\nname: order-fulfilment\nversion: 1\nowner: orders\nsteps:\n  - id: reserve-stock\n';

export const diagnostics: Diagnostic[] = [
  { id: 'WF-V07', severity: 'warning', line: 5, column: 5, message: 'A warning.' },
  { id: 'WF-V01', severity: 'error', line: 4, column: 8, message: "'orders' is not a known service." },
];

export function fakeApi(data: WorkspaceDto = workspace(), files: Record<string, string> = { 'orders/workflows/order-fulfilment.sfflow.yaml': fulfilment }) {
  const api = {
    workspace: vi.fn(async () => data),
    readFile: vi.fn(async (path: string) => {
      const text = files[path];
      if (text === undefined) {
        throw new Error('No file ' + path);
      }

      return text;
    }),
    writeFile: vi.fn(async () => undefined),
    exportManifest: vi.fn(async () => undefined),
  } satisfies WorkspaceApi;
  return api;
}

export function fakeCore(result: Diagnostic[] = diagnostics) {
  return {
    validate: vi.fn(async (_request: ValidateRequest): Promise<ValidateResult> => ({ valid: !result.some((d) => d.severity === 'error'), diagnostics: result })),
  } satisfies StudioCore;
}

export function renderApp(route: string, api = fakeApi(), core: StudioCore = fakeCore()) {
  const queries = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const view = render(
    <QueryClientProvider client={queries}>
      <ServicesProvider services={{ api, core }}>
        <MemoryRouter initialEntries={[route]}>
          <App />
        </MemoryRouter>
      </ServicesProvider>
    </QueryClientProvider>,
  );
  return { ...view, api, core };
}

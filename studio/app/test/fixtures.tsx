// Test fixtures: a commerce workspace, fake host API and core, and a render helper for the shell.
import type { Diagnostic, GraphResult, PathsResult, SequenceResult, ValidateRequest, ValidateResult } from '@sourceflow/core-client';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import ELK from 'elkjs/lib/elk.bundled.js';
import { elkEngine } from '../src/graph/layout.ts';
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

/** The real statechart of the golden order-fulfilment workflow (the core's output, checked by the .NET and WASM suites). */
export const orderGraph: GraphResult = JSON.parse(readFileSync(join(process.cwd(), '..', '..', 'tests', 'workflow-golden', 'cases', '01-order-fulfilment', 'expected.graph.json'), 'utf8')) as GraphResult;

export const paths: PathsResult = {
  paths: [
    { id: 'P01', number: 1, text: 'P01 reserve-stock: success ⇒ Completed', end: 'Completed', moves: [{ step: 'reserve-stock', outcome: 'success', edge: 'reserve-stock:success' }], guards: {} },
    { id: 'P02', number: 2, text: 'P02 reserve-stock: rejected ⇒ Compensated', end: 'Compensated', moves: [{ step: 'reserve-stock', outcome: 'rejected inventory.out_of_stock', edge: 'x' }], guards: {} },
  ],
  edges: ['reserve-stock:success', 'x'],
  uncovered: [],
  truncated: false,
  minimalCover: ['P02'],
};

export function sequenceOf(path: string): SequenceResult {
  return {
    path: { id: path, text: path + ' text', end: 'Completed' },
    lanes: [{ id: 'workflow', kind: 'workflow', label: 'order-fulfilment@1 (orders)' }, { id: 'orders', kind: 'service', label: 'orders' }, { id: 'inventory', kind: 'service', label: 'inventory' }],
    messages: [
      { from: 'orders', to: 'workflow', kind: 'start', label: '▶ orders.order-placed.v1', type: 'orders.order-placed.v1' },
      { from: 'workflow', to: 'inventory', kind: 'command', label: '→ inventory.reserve-stock.v1', step: 'reserve-stock', type: 'inventory.reserve-stock.v1', attempt: 1 },
      { from: 'inventory', to: 'workflow', kind: path === 'P02' ? 'rejection' : 'event', label: path === 'P02' ? '✕ rejected inventory.out_of_stock' : '✓ inventory.stock-reserved.v1', step: 'reserve-stock', attempt: 2 },
      { from: 'workflow', to: 'workflow', kind: 'status', label: path === 'P02' ? 'Compensated' : 'Completed' },
    ],
  };
}

export function fakeCore(result: Diagnostic[] = diagnostics) {
  return {
    validate: vi.fn(async (_request: ValidateRequest): Promise<ValidateResult> => ({ valid: !result.some((d) => d.severity === 'error'), diagnostics: result })),
    graph: vi.fn(async () => orderGraph),
    enumeratePaths: vi.fn(async () => paths),
    sequence: vi.fn(async (request: { path: string }) => sequenceOf(request.path)),
  } satisfies StudioCore;
}

/** ELK on this thread (the app runs it in a worker). */
export const layout = elkEngine(new ELK());

export function renderApp(route: string, api = fakeApi(), core: StudioCore = fakeCore()) {
  const queries = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const view = render(
    <QueryClientProvider client={queries}>
      <ServicesProvider services={{ api, core, layout }}>
        <MemoryRouter initialEntries={[route]}>
          <App />
        </MemoryRouter>
      </ServicesProvider>
    </QueryClientProvider>,
  );
  return { ...view, api, core };
}

// STU-CT-06..18: the legend, the statechart (nodes, kinded edges, labels, errors), tabs, the swimlane and the domain map.
import { StudioCoreError } from '@sourceflow/core-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import type { ReactNode } from 'react';
import { useState } from 'react';
import { MemoryRouter } from 'react-router';
import { describe, expect, it } from 'vitest';
import type { WorkspaceDto } from '../src/api.ts';
import { Tabs } from '../src/components/Tabs.tsx';
import { EDGE_ENCODINGS, EDGE_KINDS } from '../src/graph/encodings.ts';
import { ServicesProvider, type StudioCore } from '../src/services.tsx';
import { DomainMapView } from '../src/views/DomainMapView.tsx';
import { loopPath } from '../src/views/KindEdge.tsx';
import { Legend } from '../src/views/Legend.tsx';
import { StatechartView } from '../src/views/StatechartView.tsx';
import { SwimlaneView } from '../src/views/SwimlaneView.tsx';
import { fakeApi, fakeCore, layout, orderGraph, renderApp, workspace } from './fixtures.tsx';

const golden = join(process.cwd(), '..', '..', 'tests', 'workflow-golden', 'domain');
const commerce: WorkspaceDto = {
  domain: 'commerce',
  file: 'sourceflow.domain.yaml',
  readOnly: false,
  services: ['orders', 'inventory', 'payments', 'shipping'].map((name) => ({
    name, manifest: `${name}.manifest.json`, manifestJson: readFileSync(join(golden, `${name}.manifest.json`), 'utf8'), workflowFiles: [],
  })),
};

function withServices(children: ReactNode, core: StudioCore = fakeCore()) {
  const queries = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queries}>
      <ServicesProvider services={{ api: fakeApi(), core, layout }}>
        <MemoryRouter>{children}</MemoryRouter>
      </ServicesProvider>
    </QueryClientProvider>,
  );
}

async function statechart(core: StudioCore = fakeCore()) {
  const view = withServices(<StatechartView text="yaml" name="order-fulfilment.sfflow.yaml" />, core);
  await waitFor(() => expect(view.container.querySelector('[data-ready="true"]')).not.toBeNull(), { timeout: 10_000 });
  await waitFor(() => expect(view.container.querySelectorAll('.react-flow__edge').length).toBeGreaterThan(0), { timeout: 10_000 });
  return view;
}

describe('STU-CT-06 legend', () => {
  it('lists every edge kind with its line, symbol and name, and the counts', () => {
    render(<Legend counts={new Map([['retry', 3]])} />);
    const legend = screen.getByRole('figure', { name: 'Legend' });
    for (const kind of EDGE_KINDS) {
      expect(legend.textContent).toContain(`${EDGE_ENCODINGS[kind].symbol} ${EDGE_ENCODINGS[kind].name}`);
    }

    expect(legend.querySelector('[data-kind="retry"]')?.textContent).toContain('(3)');
    expect(legend.querySelector('[data-kind="failure"] line')?.getAttribute('style')).toContain('stroke-dasharray: 7 4');
    expect(legend.textContent).toContain('◆ Pivot (point of no return)');
  });
});

describe('STU-CT-07 statechart nodes', () => {
  it('draws every state with its shape, the pivot as a diamond, the compensations inside their group', async () => {
    const { container } = await statechart();
    expect(screen.getByRole('region', { name: 'Statechart of order-fulfilment.sfflow.yaml' })).toBeTruthy();
    expect(container.querySelectorAll('.react-flow__node')).toHaveLength(orderGraph.nodes.length);
    expect(container.querySelector('[data-kind="pivot"]')?.classList.contains('shape-diamond')).toBe(true);
    expect(container.querySelector('[data-kind="pivot"]')?.textContent).toContain('◆ create-shipment');
    expect(container.querySelector('[data-id="undo:reserve-stock"]')).not.toBeNull();
    expect(container.querySelector('[data-id="Compensating"] .state-group-title')?.textContent).toBe('⟲ Compensating');
    expect(container.querySelector('[data-kind="manual"]')?.textContent).toContain('✋');
  });
});

describe('STU-CT-08 statechart edges', () => {
  it('draws each edge with its kind, accessible description and symbol label', async () => {
    const { container } = await statechart();
    const edges = [...container.querySelectorAll('.react-flow__edge')];
    expect(edges).toHaveLength(orderGraph.edges.length);
    const retry = orderGraph.edges.find((e) => e.kind === 'retry' && e.source === 'charge-payment')!;
    expect(edges.some((e) => e.getAttribute('aria-label') === retry.description)).toBe(true);
    expect(container.querySelectorAll('.react-flow__edge.kind-retry').length).toBe(orderGraph.edges.filter((e) => e.kind === 'retry').length);
    const labels = [...container.querySelectorAll('.edge-label')].map((l) => l.textContent);
    expect(labels).toContain('↻ 3× every PT10M on payments.declined');
    expect(labels).toContain('⏱ P1D');
    const dashes = [...container.querySelectorAll('.react-flow__edge.kind-failure path.react-flow__edge-path')].map((p) => p.getAttribute('style') ?? '');
    expect(dashes.every((style) => style.includes('stroke-dasharray: 7 4'))).toBe(true);
  });
});

describe('STU-CT-09 retry loops', () => {
  it('draws each further loop of a state further out', () => {
    const first = loopPath(100, 100, 100, 40, 0);
    const second = loopPath(100, 100, 100, 40, 1);
    expect(first.path.startsWith('M 100 100 C 190')).toBe(true);
    expect(second.labelX).toBeGreaterThan(first.labelX);
  });
});

describe('STU-CT-10 a workflow that does not read', () => {
  it('explains why the statechart is missing', async () => {
    const core = fakeCore();
    core.graph.mockRejectedValue(new StudioCoreError('The workflow has errors.', [{ id: 'WF-S01', severity: 'error', line: 1, column: 1, message: 'x' }]));
    withServices(<StatechartView text="bad" name="bad.sfflow.yaml" />, core);
    expect((await screen.findByRole('alert')).textContent).toContain('1 error(s) in the file');
  });
});

function TabsHarness() {
  const [selected, setSelected] = useState('a');
  return <Tabs label="Views" selected={selected} onSelect={setSelected} tabs={[{ id: 'a', label: 'A', content: () => 'panel A' }, { id: 'b', label: 'B', content: () => 'panel B' }, { id: 'c', label: 'C', content: () => 'panel C' }]} />;
}

describe('STU-CT-11 tabs', () => {
  it('follow the tabs pattern: arrow keys, Home and End, one panel', async () => {
    render(<TabsHarness />);
    const list = screen.getByRole('tablist', { name: 'Views' });
    expect(screen.getByRole('tabpanel').textContent).toBe('panel A');
    screen.getByRole('tab', { name: 'A' }).focus();
    fireEvent.keyDown(list, { key: 'ArrowRight' });
    expect(screen.getByRole('tab', { name: 'B' }).getAttribute('aria-selected')).toBe('true');
    expect(document.activeElement).toBe(screen.getByRole('tab', { name: 'B' }));
    fireEvent.keyDown(list, { key: 'End' });
    expect(screen.getByRole('tabpanel').textContent).toBe('panel C');
    fireEvent.keyDown(list, { key: 'ArrowRight' });
    expect(screen.getByRole('tabpanel').textContent).toBe('panel A');
    fireEvent.keyDown(list, { key: 'ArrowLeft' });
    expect(screen.getByRole('tab', { name: 'C' }).getAttribute('tabindex')).toBe('0');
    fireEvent.keyDown(list, { key: 'Home' });
    expect(screen.getByRole('tabpanel').getAttribute('aria-labelledby')).toBe(screen.getByRole('tab', { name: 'A' }).id);
    fireEvent.keyDown(list, { key: 'x' });
    expect(screen.getByRole('tabpanel').textContent).toBe('panel A');
  });
});

describe('STU-CT-12 swimlane path picker', () => {
  it('starts on the minimal cover, lists every path, and replays the one chosen', async () => {
    const core = fakeCore();
    withServices(<SwimlaneView text="yaml" workspace={workspace()} name="order-fulfilment.sfflow.yaml" />, core);
    const picker = (await screen.findByRole('combobox', { name: 'Path' })) as HTMLSelectElement;
    expect(picker.value).toBe('P02');
    expect([...picker.options].map((o) => o.textContent)).toEqual(['P01 · Completed · 1 move', 'P02 · Compensated · 1 move · in the minimal cover']);
    expect(await screen.findByRole('list', { name: 'Messages of P02' })).toBeTruthy();
    await userEvent.selectOptions(picker, 'P01');
    expect(await screen.findByRole('list', { name: 'Messages of P01' })).toBeTruthy();
    expect(core.sequence).toHaveBeenLastCalledWith(expect.objectContaining({ path: 'P01' }));
  });
});

describe('STU-CT-13 swimlane messages', () => {
  it('draws each message between its lanes and reads it as a sentence', async () => {
    const { container } = withServices(<SwimlaneView text="yaml" workspace={workspace()} name="w" />);
    const messages = await screen.findByRole('list', { name: 'Messages of P02' });
    const items = within(messages).getAllByRole('listitem');
    expect(items.map((i) => i.getAttribute('aria-label'))).toEqual([
      'start from orders to order-fulfilment@1 (orders): ▶ orders.order-placed.v1',
      'command from order-fulfilment@1 (orders) to inventory: → inventory.reserve-stock.v1',
      'rejection from inventory to order-fulfilment@1 (orders): ✕ rejected inventory.out_of_stock, attempt 2',
      'order-fulfilment@1 (orders): status Compensated',
    ]);
    expect(container.querySelector('.message-command .arrow')?.classList.contains('arrow-right')).toBe(true);
    expect(container.querySelector('.message-rejection .arrow-label')?.textContent).toBe('◀ ✕ rejected inventory.out_of_stock (attempt 2)');
    expect(container.querySelector('.message-status .arrow')?.classList.contains('arrow-self')).toBe(true);
    expect([...container.querySelectorAll('.lane-head')].map((l) => l.textContent)).toEqual(['order-fulfilment@1 (orders)', 'orders', 'inventory']);
  });
});

async function domainMapView(workspace: WorkspaceDto = commerce) {
  const view = withServices(<DomainMapView workspace={workspace} />);
  await waitFor(() => expect(view.container.querySelector('[data-ready="true"]')).not.toBeNull(), { timeout: 10_000 });
  await waitFor(() => expect(view.container.querySelectorAll('.react-flow__edge').length).toBeGreaterThan(0), { timeout: 10_000 });
  return view;
}

describe('STU-CT-14 domain map', () => {
  it('shows services, aggregates and topics with accessible names', async () => {
    const { container } = await domainMapView();
    expect(screen.getByRole('region', { name: 'Domain map of commerce' })).toBeTruthy();
    const labels = [...container.querySelectorAll('.react-flow__node')].map((n) => n.getAttribute('aria-label'));
    expect(labels).toContain('Service orders');
    expect(labels).toContain('Aggregate stock: inventory.release-stock.v1, inventory.reserve-stock.v1');
    expect(labels.some((l) => l?.startsWith('Topic orders.events'))).toBe(true);
    const edges = [...container.querySelectorAll('.react-flow__edge')].map((e) => e.getAttribute('aria-label'));
    expect(edges).toContain('orders commands stock: inventory.release-stock.v1, inventory.reserve-stock.v1');
    expect(edges).toContain('inventory.events subscribes orders: inventory.stock-reserved.v1, inventory.stock-released.v1');
  });
});

describe('STU-CT-15 choreography highlight', () => {
  it('highlights the chain from the hovered service and dims the rest', async () => {
    // The whole commerce domain is one loop through orders; without it, inventory's chain is only its publication.
    const { container } = await domainMapView({ ...commerce, services: commerce.services.filter((s) => s.name === 'inventory' || s.name === 'payments') });
    const flow = container.querySelector('.flow-map')!;
    expect(flow.getAttribute('data-highlighted')).toBe('0');
    const inventory = container.querySelector('.react-flow__node[data-id="service:inventory"]') as HTMLElement;
    await userEvent.hover(inventory);
    await waitFor(() => expect(flow.getAttribute('data-highlighted')).toBe('1'));
    expect(container.querySelectorAll('path.map-edge.highlighted').length).toBeGreaterThan(0);
    expect(container.querySelectorAll('path.map-edge.dimmed').length).toBeGreaterThan(0);
    await userEvent.unhover(inventory);
    await waitFor(() => expect(flow.getAttribute('data-highlighted')).toBe('0'));

    await userEvent.click(container.querySelector('.react-flow__node[data-id="aggregate:payments/payment"]') as HTMLElement);
    await waitFor(() => expect(flow.getAttribute('data-highlighted')).toBe('1'), { timeout: 3_000 });
    expect(container.querySelector('path.map-edge.highlighted')?.id).toBe('publishes:service:payments->topic:payments.events');
  });
});

describe('STU-CT-16 domain map navigation', () => {
  it('links each service to its view', async () => {
    const { container } = await domainMapView();
    const link = within(container.querySelector('[data-id="service:payments"]') as HTMLElement).getByRole('link', { name: 'payments' });
    expect(link.getAttribute('href')).toBe('/services/payments');
  });
});

describe('STU-CT-17 swimlane errors', () => {
  it('explains a workflow whose paths cannot be enumerated', async () => {
    const core = fakeCore();
    core.enumeratePaths.mockRejectedValue(new Error('The workflow has errors.'));
    withServices(<SwimlaneView text="bad" workspace={workspace()} name="bad" />, core);
    expect((await screen.findByRole('alert')).textContent).toContain('The workflow has errors.');
  });
});

describe('STU-CT-18 workflow view tabs', () => {
  it('opens on the statechart and switches to the swimlane and the source', async () => {
    const { container } = renderApp('/workflows/orders/workflows/order-fulfilment.sfflow.yaml');
    expect(await screen.findByRole('tab', { name: 'Statechart', selected: true })).toBeTruthy();
    await waitFor(() => expect(container.querySelector('[data-ready="true"]')).not.toBeNull(), { timeout: 10_000 });
    await userEvent.click(screen.getByRole('tab', { name: 'Swimlane' }));
    expect(await screen.findByRole('combobox', { name: 'Path' })).toBeTruthy();
    await userEvent.click(screen.getByRole('tab', { name: 'Source' }));
    expect(await screen.findByRole('list', { name: 'Source of order-fulfilment.sfflow.yaml' })).toBeTruthy();
  });
});

// WP-S.12: the standalone SVG statechart (the export, the static site and the workflow-diff action draw with it), the
// statechart diff, and the export buttons of the workflow view.
import type { GraphResult } from '@sourceflow/core-client';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { exportWorkflow } from '../src/export.ts';
import { describeDiff, diffGraphs } from '../src/graph/diff.ts';
import { statechartPositions } from '../src/graph/statechart.ts';
import { absoluteBoxes, escapeXml, selfLoop, statechartSvg, stepPath } from '../src/graph/svg.ts';
import { fakeApi, fakeCore, layout, orderGraph, renderApp, workspace } from './fixtures.tsx';

function parse(svg: string): Document {
  const doc = new DOMParser().parseFromString(svg, 'image/svg+xml');
  expect(doc.getElementsByTagName('parsererror')).toHaveLength(0);
  return doc;
}

describe('the SVG statechart', () => {
  it('draws every state and transition with its words, and is the same every time', async () => {
    const positions = await statechartPositions(orderGraph, layout);
    const svg = statechartSvg(orderGraph, positions, { title: 'Statechart of order-fulfilment.sfflow.yaml' });
    const doc = parse(svg);
    const root = doc.documentElement;
    expect(root.getAttribute('role')).toBe('img');
    expect(doc.getElementById('sf-title')?.textContent).toBe('Statechart of order-fulfilment.sfflow.yaml');
    expect(doc.getElementById('sf-desc')?.textContent).toBe(`${orderGraph.nodes.length} states and ${orderGraph.edges.length} transitions. Line style, symbol and label give each transition's kind.`);
    expect(root.querySelectorAll('g.state')).toHaveLength(orderGraph.nodes.length);
    expect(root.querySelectorAll('g.edge')).toHaveLength(orderGraph.edges.length);
    const titles = [...root.querySelectorAll('title')].map((t) => t.textContent);
    for (const edge of orderGraph.edges) {
      expect(titles).toContain(edge.description);
    }

    expect(root.textContent).toContain('◆ create-shipment');
    expect(root.textContent).toContain('send inventory.reserve-stock.v1');
    expect([...root.querySelectorAll('g.edge-label text')].map((t) => t.textContent)).toEqual(orderGraph.edges.map((e) => e.label));
    expect(root.querySelector('g.kind-failure path[stroke-dasharray="7 4"]')).not.toBeNull();
    expect(statechartSvg(orderGraph, positions, { title: 'Statechart of order-fulfilment.sfflow.yaml' })).toBe(svg);

    const dark = parse(statechartSvg(orderGraph, positions, { theme: 'dark' })).documentElement;
    expect(dark.querySelector('rect')?.getAttribute('fill')).toBe('#15181c');
    // Children are drawn where their group puts them.
    const boxes = absoluteBoxes(orderGraph, positions);
    expect(boxes.get('undo:reserve-stock')!.x).toBeGreaterThanOrEqual(boxes.get('Compensating')!.x);
  });

  it('routes transitions down and across, and loops retries out to the side', () => {
    expect(stepPath({ x: 0, y: 0, width: 100, height: 40 }, { x: 0, y: 100, width: 100, height: 40 })).toEqual({ path: 'M 50 40 L 50 100', labelX: 50, labelY: 70 });
    const across = stepPath({ x: 0, y: 0, width: 100, height: 40 }, { x: 200, y: 100, width: 100, height: 40 });
    expect(across.path.startsWith('M 50 40 L 50 ')).toBe(true);
    expect(across.path.endsWith('L 250 100')).toBe(true);
    expect(selfLoop({ x: 0, y: 0, width: 100, height: 40 }, 1).labelX).toBeGreaterThan(selfLoop({ x: 0, y: 0, width: 100, height: 40 }, 0).labelX);
    expect(escapeXml('a < b & "c" > d')).toBe('a &lt; b &amp; &quot;c&quot; &gt; d');
  });
});

describe('the statechart diff (WFD-36)', () => {
  it('finds added, removed and changed states and transitions, with marks for both pictures', async () => {
    const after: GraphResult = structuredClone(orderGraph);
    after.nodes = after.nodes.filter((n) => n.id !== 'await-address-fix');
    after.edges = after.edges.filter((e) => e.source !== 'await-address-fix' && e.target !== 'await-address-fix');
    after.nodes.find((n) => n.id === 'reserve-stock')!.timeout = 'PT10M';
    after.nodes.push({ id: 'refund', kind: 'step', label: 'refund', sends: 'payments.refund.v1', compensable: false });
    after.edges.push({ id: 'create-shipment->refund:success:0', source: 'create-shipment', target: 'refund', kind: 'success', label: '✓ x', description: 'Success: x' });

    const diff = diffGraphs(orderGraph, after);
    expect(diff.added.nodes.map((n) => n.id)).toEqual(['refund']);
    expect(diff.removed.nodes.map((n) => n.id)).toEqual(['await-address-fix']);
    expect(diff.changed.nodes.map((c) => c.after.id)).toEqual(['reserve-stock']);
    expect(diff.after.get('refund')).toBe('added');
    expect(diff.before.get('await-address-fix')).toBe('removed');
    expect(diff.before.get('reserve-stock')).toBe('changed');
    const lines = describeDiff(diff);
    expect(lines[0]).toBe('＋ Added step refund');
    expect(lines).toContain('− Removed step await-address-fix');
    expect(lines).toContain('△ Changed step reserve-stock: timeout PT5M → PT10M');
    expect(lines.some((l) => l.startsWith('− Removed failure create-shipment → await-address-fix'))).toBe(true);
    expect(diffGraphs(orderGraph, orderGraph).unchanged).toBe(true);
    expect(diffGraphs(undefined, orderGraph).added.nodes).toHaveLength(orderGraph.nodes.length);

    const positions = await statechartPositions(after, layout);
    const marked = parse(statechartSvg(after, positions, { marks: diff.after })).documentElement;
    expect(marked.querySelector('g.state.added')?.textContent).toContain('＋ added');
    expect(marked.querySelector('g.state.changed')?.textContent).toContain('△ changed');
  });
});

describe('the export buttons', () => {
  afterEach(() => vi.restoreAllMocks());

  it('download the statechart and the core exports of the open workflow', async () => {
    const saved: Array<{ name: string; blob: Blob }> = [];
    const urls = new Map<string, Blob>();
    URL.createObjectURL = vi.fn((blob: Blob) => {
      const url = `blob:${urls.size}`;
      urls.set(url, blob);
      return url;
    });
    URL.revokeObjectURL = vi.fn();
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      saved.push({ name: this.download, blob: urls.get(this.href)! });
    });
    const core = fakeCore([]);
    renderApp('/workflows/orders/workflows/order-fulfilment.sfflow.yaml', fakeApi(workspace()), core);
    const exports = await screen.findByRole('group', { name: 'Export' });
    expect([...exports.querySelectorAll('button')].map((b) => b.getAttribute('aria-label'))).toEqual(['Export SVG', 'Export PNG', 'Export Mermaid', 'Export BPMN']);

    await userEvent.click(screen.getByRole('button', { name: 'Export Mermaid' }));
    await waitFor(() => expect(saved).toHaveLength(1));
    expect(saved[0]!.name).toBe('order-fulfilment.mmd');
    expect(await saved[0]!.blob.text()).toContain('stateDiagram-v2');
    await userEvent.click(screen.getByRole('button', { name: 'Export BPMN' }));
    await waitFor(() => expect(saved).toHaveLength(2));
    expect(saved[1]!.name).toBe('order-fulfilment.bpmn');

    await userEvent.click(screen.getByRole('button', { name: 'Export SVG' }));
    await waitFor(() => expect(saved).toHaveLength(3));
    expect(saved[2]!.name).toBe('order-fulfilment.svg');
    expect(saved[2]!.blob.type).toBe('image/svg+xml');
    expect(parse(await saved[2]!.blob.text()).getElementById('sf-title')?.textContent).toBe('Statechart of order-fulfilment.sfflow.yaml');

    core.toBpmn.mockRejectedValueOnce(new Error('The workflow has 2 errors.'));
    await userEvent.click(screen.getByRole('button', { name: 'Export BPMN' }));
    expect((await screen.findByRole('alert')).textContent).toBe('The export needs a readable workflow: The workflow has 2 errors.');
  });

  it('applies the positions saved beside the workflow', async () => {
    const svg = await (await exportWorkflow('svg', 'yaml', 'x.sfflow.yaml', { core: fakeCore([]), layout }, { version: 1, nodes: { 'reserve-stock': { x: 5000, y: 7000 } } }, 'light')).content.text();
    expect(svg).toContain('x="5016" y="7028"');
  });
});
